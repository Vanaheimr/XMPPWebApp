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

using System.Security.Cryptography;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// A real server with an upload service, and a file sent through the web
    /// app to it.
    /// </summary>
    /// <remarks>
    /// <b>The rule this is here for is one line long and easy to get backwards:
    /// the conversation's encryption decides the file's.</b> Somebody who turned
    /// encryption on for a chat and then sent a photograph in the clear would be
    /// right to be surprised - and nothing about a working upload would tell
    /// them it had happened.
    ///
    /// So both directions are checked, and both by looking at what is actually
    /// on the server: an <c>aesgcm://</c> address and bytes the service cannot
    /// read in the one case, an ordinary address and the file itself in the
    /// other. An assertion on the address alone would pass for a client that
    /// wrote the scheme and forgot the encryption.
    /// </remarks>
    [TestFixture]
    public class FileSendEndToEndTests
    {

        #region Data

        private XMPPServer?  xmpp;
        private HTTPServer?  web;
        private XMPPWebAPI?  api;
        private String       root   = "";

        #endregion

        #region Setup / Teardown

        [SetUp]
        public async Task StartEverything()
        {

            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-files-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            // The upload service switched on before the start, because the
            // address it hands out has to name the port the bind settles.
            xmpp = new XMPPServer(useTLS: false) { OfferFileUploads = true };
            xmpp.Start();

            xmpp.AddAccount("me");
            xmpp.AddAccount("alice");

            Roster("me",    "alice");
            Roster("alice", "me");

            web = await HTTPServer.StartNew(IPv4Address.Parse("127.0.0.1"));

            api = new XMPPWebAPI(
                      web,
                      new AccountFile(Path.Combine(root, "xmpp-account.json")),
                      OmemoDirectory:  Path.Combine(root, "omemo"),
                      DataDirectory:   root
                  );

            await api.LoadDatabase();

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

        private void Roster(String LocalPart, String Contact)
        {

            xmpp!.GetAccount($"{LocalPart}@{xmpp.Domain}")!
                 .SetRosterEntry(new RosterEntry($"{Contact}@{xmpp.Domain}", null, "both"));

        }

        private AccountSettings Account(String LocalPart)

            => new (JID.Parse($"{LocalPart}@{xmpp!.Domain}"),
                    "pw",
                    xmpp.Uri.ToString(),
                    AllowInsecure: true);

        private static async Task Until(Func<Boolean> Condition, String What)
        {
            Assert.That(await XMPPServer.WaitUntilAsync(Condition, TimeSpan.FromSeconds(20)),
                        Is.True,
                        $"Timeout while waiting for: {What}");
        }

        private JID Alice => JID.Parse($"alice@{xmpp!.Domain}");

        private static Byte[] APicture()
            => RandomNumberGenerator.GetBytes(1024);

        #endregion


        #region AFileGoesUpAndTheConversationSaysWhere()

        /// <summary>
        /// The plain case: the file is on the server and the line names it.
        /// </summary>
        /// <remarks>
        /// <b>Encryption is turned off for this conversation rather than left to
        /// chance</b>, and that is the whole difference between this round
        /// measuring the plain case and it measuring whichever case the timing
        /// produced. The rule is that the conversation decides - so a round
        /// about the plain path has to say what the conversation is, or OMEMO
        /// switching on between the sign-in and the send silently turns it into
        /// the encrypted path, which over this plaintext test server is the
        /// refusal the round below pins.
        ///
        /// Found by D126, which added enough fixtures to make the race show:
        /// one full run in three.
        /// </remarks>
        [Test]
        public async Task AFileGoesUpAndTheConversationSaysWhere()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.IsConnected == true, "the web app to sign in");

            api.Chats.Open(Alice);
            api.Chats.SetEncryption(Alice, On: false);

            var picture = APicture();

            var (message, refusal) = await api.SendFileToAsync(Alice, picture, "holiday.png");

            Assert.That(message, Is.Not.Null, $"Nothing was sent: {refusal}");

            Assert.That(Uri.TryCreate(message!.Body, UriKind.Absolute, out var url), Is.True,
                        "The line in the conversation is not an address, so nothing downstream can " +
                        "show it as a picture.");

            Assert.That(AesGcmUrl.IsAesGcmUrl(url!), Is.False,
                        "An unencrypted conversation produced an aesgcm:// address.");

            // And the file really is there, fetched the way any recipient would.
            var fetched = await api.Client!.DownloadFileAsync(url!);

            Assert.That(fetched, Is.EqualTo(picture),
                        "What the service is holding is not the file that was sent.");

        }

        #endregion

        #region AFileInAnEncryptedChatIsNotPublishedOverPlainHttp()

        /// <summary>
        /// The rule, and what it does when the transport will not carry it.
        /// </summary>
        /// <remarks>
        /// <b>The conversation's encryption decides the file's</b> - somebody who
        /// turned encryption on and then sent a photograph in the clear would be
        /// right to be surprised. Here the encryption is on, so the file is meant
        /// to travel under XEP-0454.
        ///
        /// And here it cannot. <c>aesgcm://</c> is defined as an https address
        /// with the scheme swapped, so an aesgcm URL built over plain http names
        /// somewhere that does not answer - and, worse, carries the key on a
        /// transport that shows it. This test server speaks <c>ws://</c> and
        /// <c>http://</c>, because AccountSettings offers no way to trust a
        /// self-signed certificate and rightly so.
        ///
        /// So the right outcome is a refusal, and that is what this pins. It
        /// was found rather than reasoned out: the first version of this round
        /// expected the round trip and spent thirty seconds failing a TLS
        /// handshake against a server that speaks none.
        ///
        /// The encrypted round trip itself is checked where there is TLS to check
        /// it over - against our own server in Ratatoskr's FileUploadServiceTests,
        /// and against Prosody and ejabberd in the conformance suite.
        /// </remarks>
        [Test]
        public async Task AFileInAnEncryptedChatIsNotPublishedOverPlainHttp()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.OmemoEnabled == true,
                        "the web app to announce its OMEMO device");

            var (message, refusal) = await api.SendFileToAsync(Alice, APicture(), "passport.png");

            Assert.Multiple(() =>
            {

                Assert.That(message, Is.Null,
                            "A file in an encrypted conversation was published at a plain http " +
                            "address - which would put the key on a transport that shows it.");

                Assert.That(refusal, Is.Not.Null.And.Not.Empty,
                            "It was not sent and nothing said why.");

            });

        }

        #endregion

        #region AFileNameThatWritesElsewhereIsRefused()

        /// <summary>
        /// The name travels into the URL the service hands out.
        /// </summary>
        /// <remarks>
        /// Checked at the route rather than here, so this asks the route. A name
        /// that had to be repaired is not the name that was asked for, and the
        /// sender should be told rather than have it quietly changed.
        /// </remarks>
        [Test]
        public async Task AFileNameThatWritesElsewhereIsRefused()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.IsConnected == true, "the web app to sign in");

            // Through SendFileToAsync the name reaches the upload service, which
            // refuses it in turn - so this checks that the refusal happens at
            // all, wherever it happens.
            var (message, refusal) = await api.SendFileToAsync(
                                         Alice,
                                         Encoding.UTF8.GetBytes("x"),
                                         "../../etc/passwd");

            Assert.That(message, Is.Null,
                        "A file name with a path in it was accepted.");

            Assert.That(refusal, Is.Not.Null.And.Not.Empty,
                        "It was refused and nothing said why.");

        }

        #endregion

    }

}
