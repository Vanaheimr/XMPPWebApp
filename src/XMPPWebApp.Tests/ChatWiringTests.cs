/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of XMPPWebApp <https://www.github.com/Vanaheimr/XMPPWebApp>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// A conversation's stanzas arriving over a real connection, written
    /// straight into the session the way a server would send them.
    /// </summary>
    /// <remarks>
    /// The same approach as <see cref="RoomWiringTests"/>, for what the chat
    /// store gets from the wire rather than from a test calling it: the
    /// wiring is where this app's own mistakes live.
    /// </remarks>
    [TestFixture]
    public class ChatWiringTests
    {

        #region Data

        private XMPPServer?  xmpp;
        private HTTPServer?  web;
        private XMPPWebAPI?  api;
        private String       root  = "";

        #endregion

        #region Setup / Teardown

        [SetUp]
        public async Task StartEverything()
        {

            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-chats-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            xmpp = new XMPPServer(useTLS: false);
            xmpp.Start();

            xmpp.AddAccount("me");

            web = await HTTPServer.StartNew(IPv4Address.Parse("127.0.0.1"));

            api = new XMPPWebAPI(
                      web,
                      new AccountFile(Path.Combine(root, "xmpp-account.json")),
                      OmemoDirectory:  Path.Combine(root, "omemo"),
                      DataDirectory:   root
                  );

            await api.LoadDatabase();
            await api.ApplyAccountAsync(new AccountSettings(JID.Parse($"me@{xmpp.Domain}"),
                                                            "pw",
                                                            xmpp.Uri.ToString(),
                                                            AllowInsecure: true),
                                        Save: false);

            await Until(() => api.Client?.IsConnected == true, "the web app to sign in");

        }

        [TearDown]
        public async Task StopEverything()
        {

            if (web is not null)
                await web.Stop();

            if (api?.Client is not null)
                await api.Client.DisposeAsync();

            if (xmpp is not null)
                await xmpp.DisposeAsync();

            web   = null;
            api   = null;
            xmpp  = null;

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (Exception)
            { }

        }

        private static async Task Until(Func<Boolean> Condition, String What)
        {
            Assert.That(await XMPPServer.WaitUntilAsync(Condition, TimeSpan.FromSeconds(20)),
                        Is.True,
                        $"Timeout while waiting for: {What}");
        }

        private async Task<XMPPSession> SessionAsync()
        {

            await Until(() => xmpp!.SessionOf(api!.Client!.FullJid.ToString()) is not null,
                        "the server session");

            return xmpp!.SessionOf(api!.Client!.FullJid.ToString())!;

        }

        private IReadOnlyList<ChatMessage> Lines(JID Chat)
        {
            api!.Chats.TrySnapshot(Chat, out _, out _, out var messages);
            return messages;
        }

        #endregion


        #region AnAnswerFromAnotherDeviceOfOurs_KeepsWhatItAnswers()

        /// <summary>
        /// XEP-0280 with XEP-0461: an answer written on another device of our
        /// own is filed as an answer here too.
        /// </summary>
        /// <remarks>
        /// The carbon used to bring the body and nothing else, so the line
        /// stood here with the quoted lines in the middle of its own text - and
        /// with nothing to lead back to the line it answers.
        /// </remarks>
        [Test]
        public async Task AnAnswerFromAnotherDeviceOfOurs_KeepsWhatItAnswers()
        {

            var session  = await SessionAsync();
            var me       = api!.Client!.BareJid;
            var alice    = JID.Parse($"alice@{xmpp!.Domain}");

            var composed = MessageReply.Compose("Eight is fine",
                                                "m1",
                                                JID.Parse($"{alice}/phone"),
                                                quotedText: "Shall we meet at eight?");

            await session.SendAsync(
                $"<message from='{me}' to='{api.Client.FullJid}'>" +
                    "<sent xmlns='urn:xmpp:carbons:2'>" +
                        "<forwarded xmlns='urn:xmpp:forward:0'>" +
                            $"<message xmlns='jabber:client' from='{me}/phone' to='{alice}' type='chat' id='a2'>" +
                                $"<body>{XmlEscaping.Escape(composed.Body)}</body>" +
                                composed.Extras +
                            "</message>" +
                        "</forwarded>" +
                    "</sent>" +
                "</message>");

            await Until(() => Lines(alice).Count == 1, "the carbon in the conversation with Alice");

            var line = Lines(alice)[0];

            Assert.Multiple(() =>
            {

                Assert.That(line.Direction,  Is.EqualTo(MessageDirection.Outgoing));
                Assert.That(line.Carbon,     Is.True);

                Assert.That(line.Body,       Is.EqualTo("Eight is fine"),
                            "The quotation was left in the text of the mirrored answer.");

                Assert.That(line.RepliesTo,  Is.EqualTo("m1"),
                            "The mirrored answer refers to nothing.");

                Assert.That(line.Quote,      Is.EqualTo("> Shall we meet at eight?\n"));

            });

        }

        #endregion

        #region WhatAnotherDeviceOfOursCorrectsOrTakesBack_IsCorrectedOrTakenBackHere()

        /// <summary>
        /// XEP-0280 with XEP-0308 and XEP-0424: a line of ours corrected or
        /// taken back on another device of ours.
        /// </summary>
        /// <remarks>
        /// The carbon used to bring the body and nothing else, so the
        /// correction stood here as a second line beside the line it corrects,
        /// and the retraction as the fallback sentence beside the words it was
        /// meant to take away.
        /// </remarks>
        [Test]
        public async Task WhatAnotherDeviceOfOursCorrectsOrTakesBack_IsCorrectedOrTakenBackHere()
        {

            var session  = await SessionAsync();
            var me       = api!.Client!.BareJid;
            var alice    = JID.Parse($"alice@{xmpp!.Domain}");

            String Carbon(String Id, String Inner)

                => $"<message from='{me}' to='{api.Client.FullJid}'>" +
                       "<sent xmlns='urn:xmpp:carbons:2'>" +
                           "<forwarded xmlns='urn:xmpp:forward:0'>" +
                               $"<message xmlns='jabber:client' from='{me}/phone' to='{alice}' type='chat' id='{Id}'>" +
                                   Inner +
                               "</message>" +
                           "</forwarded>" +
                       "</sent>" +
                   "</message>";

            await session.SendAsync(Carbon("p1", "<body>the wrogn word</body>"));
            await session.SendAsync(Carbon("p2", "<body>another line</body>"));

            await Until(() => Lines(alice).Count == 2, "both lines from the phone");

            await session.SendAsync(Carbon("p3", "<body>the right word</body>" + MessageCorrection.ReplaceXml("p1")));
            await session.SendAsync(Carbon("p4", $"<body>{XmlEscaping.Escape(MessageRetraction.FallbackBody)}</body>" +
                                                 MessageRetraction.Extras("p2")));

            await Until(() => Lines(alice).Any(line => line.Retracted), "the retraction from the phone");

            var lines = Lines(alice);

            Assert.Multiple(() =>
            {

                Assert.That(lines, Has.Count.EqualTo(2),
                            "The correction or the retraction stood as a line of its own.");

                Assert.That(lines[0].Id,         Is.EqualTo("p1"));
                Assert.That(lines[0].Body,       Is.EqualTo("the right word"));
                Assert.That(lines[0].Corrected,  Is.True);

                Assert.That(lines[1].Id,         Is.EqualTo("p2"));
                Assert.That(lines[1].Body,       Is.Empty);
                Assert.That(lines[1].Retracted,  Is.True);

            });

        }

        #endregion

    }

}
