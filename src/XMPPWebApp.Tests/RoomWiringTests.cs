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
using org.GraphDefined.Vanaheimr.XMPPWebApp.Rooms;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// XEP-0045 where it meets the web app: a room's stanzas arriving over a
    /// real connection.
    /// </summary>
    /// <remarks>
    /// <b>The test server has no room service</b>, so the presences and the
    /// messages a service would send are written straight into the session -
    /// the same way Ratatoskr's own room tests have worked since D116. What a
    /// real service settles is settled in the conformance suite; what is
    /// settled here is the wiring, which is where this app's own mistakes live.
    ///
    /// And one of them is the reason this file exists at all: until D126 a
    /// groupchat message was dropped on the floor with the comment "a room is
    /// not a conversation this client knows how to hold".
    /// </remarks>
    [TestFixture]
    public class RoomWiringTests
    {

        #region Data

        private XMPPServer?  xmpp;
        private HTTPServer?  web;
        private XMPPWebAPI?  api;
        private String       root  = "";

        private JID RoomJid => JID.Parse($"chat@conference.{xmpp!.Domain}");

        #endregion

        #region Setup / Teardown

        [SetUp]
        public async Task StartEverything()
        {

            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-rooms-" + Guid.NewGuid().ToString("N"));

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
            await api.ApplyAccountAsync(Account("me"), Save: false);

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

        private async Task<XMPPSession> SessionAsync()
        {

            await Until(() => xmpp!.SessionOf(api!.Client!.FullJid.ToString()) is not null,
                        "the server session");

            return xmpp!.SessionOf(api!.Client!.FullJid.ToString())!;

        }

        private String OccupantPresence(String   nick,
                                        JID      to,
                                        JID?     realJid  = null,
                                        Boolean  self     = false)

            => $"<presence from='{RoomJid}/{nick}' to='{to}'>" +
                   "<x xmlns='http://jabber.org/protocol/muc#user'>" +
                       "<item affiliation='" + (self ? "owner" : "none") + "' " +
                             "role='" + (self ? "moderator" : "participant") + "'" +
                             (realJid is not null ? $" jid='{realJid}/device'" : "") + "/>" +
                       (realJid is not null ? "<status code='100'/>" : "") +
                       (self ? $"<status code='{MucStatus.Self}'/>" : "") +
                   "</x>" +
               "</presence>";

        /// <summary>
        /// Walks the web app into a room the test plays.
        /// </summary>
        private async Task<XMPPSession> JoinAsync(Boolean NonAnonymous = false)
        {

            var session  = await SessionAsync();
            var joining  = api!.Client!.JoinRoomAsync(RoomJid, "me");

            await session.SendAsync(OccupantPresence("alice", api.Client.FullJid,
                                                     NonAnonymous ? JID.Parse($"alice@{xmpp!.Domain}") : null));

            await session.SendAsync(OccupantPresence("me", api.Client.FullJid,
                                                     NonAnonymous ? api.Client.BareJid : null,
                                                     self: true));

            Assert.That((await joining).Joined, Is.True,
                        "The web app did not get into the room, so nothing below says anything.");

            await Until(() => api.Rooms.Count == 1, "the room to reach the store");

            return session;

        }

        private RoomSummary? TheRoom()
        {
            api!.Rooms.TrySnapshot(RoomJid, out _, out var summary, out _);
            return summary;
        }

        private IReadOnlyList<RoomMessage> Lines()
        {
            api!.Rooms.TrySnapshot(RoomJid, out _, out _, out var messages);
            return messages;
        }

        #endregion


        #region ARoomReachesTheRoomsAndNotTheChats()

        /// <summary>
        /// The split, checked where it is easiest to get wrong.
        /// </summary>
        /// <remarks>
        /// Both halves matter. A groupchat message landing in the conversations
        /// would put the room into the chat list under its bare address and
        /// attribute every occupant's line to it; a room that reaches neither
        /// is what this app did until D126.
        /// </remarks>
        [Test]
        public async Task ARoomReachesTheRoomsAndNotTheChats()
        {

            var session  = await JoinAsync();
            var before   = api!.Chats.Count;

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api.Client!.FullJid}' type='groupchat' id='m1'>" +
                    "<body>hello everybody</body>" +
                "</message>");

            await Until(() => Lines().Count == 1, "the line to reach the room");

            Assert.Multiple(() =>
            {

                Assert.That(Lines()[0].Body,  Is.EqualTo("hello everybody"));
                Assert.That(Lines()[0].Nick,  Is.EqualTo("alice"));
                Assert.That(Lines()[0].Mine,  Is.False);

                Assert.That(api.Chats.Count, Is.EqualTo(before),
                            "A room put a row into the conversations. A room is not a conversation - " +
                            "see RoomStore for the four rules that differ.");

                Assert.That(api.Rooms.Unread, Is.EqualTo(1));

            });

        }

        #endregion

        #region OurOwnLineComesBackAndIsNotShownTwice()

        /// <summary>
        /// A service hands every message to everybody, the sender included.
        /// </summary>
        /// <remarks>
        /// The line is in the store already, put there on sending - it has to
        /// be, because in an encrypted room the reflection cannot be read at
        /// all. So the reflection is recognised by its id and dropped, and
        /// without that every room shows everything said in it twice.
        /// </remarks>
        [Test]
        public async Task OurOwnLineComesBackAndIsNotShownTwice()
        {

            var session = await JoinAsync();

            api!.Rooms.AddOutgoing(RoomJid, "mine-1", "said once", DateTimeOffset.UtcNow, Encrypted: false);

            await session.SendAsync(
                $"<message from='{RoomJid}/me' to='{api.Client!.FullJid}' type='groupchat' id='mine-1'>" +
                    "<body>said once</body>" +
                "</message>");

            // Nothing is expected to happen, so it is given every chance to.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {

                Assert.That(Lines(), Has.Count.EqualTo(1),
                            "The line this app sent came back and was shown a second time.");

                Assert.That(api.Rooms.Unread, Is.EqualTo(0),
                            "This app's own sentence counted as something nobody has read.");

            });

        }

        #endregion

        #region AnOccupantLeavingIsGoneFromTheList()

        /// <summary>
        /// The occupant list is replaced from the library's picture, not
        /// patched.
        /// </summary>
        /// <remarks>
        /// A missed event would otherwise leave somebody in the list for ever -
        /// and in an encrypted room that is not cosmetic, because the count of
        /// who can read this would be wrong.
        /// </remarks>
        [Test]
        public async Task AnOccupantLeavingIsGoneFromTheList()
        {

            var session = await JoinAsync();

            Assert.That(TheRoom()!.Occupants, Has.Count.EqualTo(2));

            await session.SendAsync(
                $"<presence from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='unavailable'>" +
                    "<x xmlns='http://jabber.org/protocol/muc#user'>" +
                        "<item affiliation='none' role='none'/>" +
                    "</x>" +
                "</presence>");

            await Until(() => TheRoom()!.Occupants.Count == 1, "the occupant to go");

            Assert.That(TheRoom()!.Occupants.Single().Nick, Is.EqualTo("me"));

        }

        #endregion

        #region ASemiAnonymousRoomSaysWhyItCannotBeEncryptedIn()

        /// <summary>
        /// The reason travels to the browser, and it is a sentence.
        /// </summary>
        /// <remarks>
        /// This is what the banner in the room view shows. A Boolean would draw
        /// a shut lock and leave somebody to guess - and the answer is usually
        /// one setting away, which is the thing worth saying.
        /// </remarks>
        [Test]
        public async Task ASemiAnonymousRoomSaysWhyItCannotBeEncryptedIn()
        {

            await JoinAsync(NonAnonymous: false);

            Assert.Multiple(() =>
            {

                Assert.That(TheRoom()!.NonAnonymous,  Is.False);

                Assert.That(TheRoom()!.CannotEncrypt, Does.Contain("semi-anonymous"),
                            "An ordinary room did not say why it cannot carry an encrypted line.");

                Assert.That(TheRoom()!.Occupants.Single(o => o.Nick == "alice").Jid, Is.Null,
                            "A semi-anonymous room handed out a real address.");

            });

        }

        #endregion

        #region ANonAnonymousRoomCanBeEncryptedIn()

        /// <summary>
        /// The other side of the same question, and the one the whole D125 lane
        /// turns on.
        /// </summary>
        [Test]
        public async Task ANonAnonymousRoomCanBeEncryptedIn()
        {

            await JoinAsync(NonAnonymous: true);

            await Until(() => api!.Client!.OmemoEnabled, "the web app to announce its OMEMO device");

            // The occupant list reached the store when the room was entered;
            // whether it can be encrypted in is worked out from it.
            await Until(() => TheRoom()!.CannotEncrypt is null,
                        "the room to become one that can be encrypted in");

            Assert.Multiple(() =>
            {

                Assert.That(TheRoom()!.NonAnonymous, Is.True);

                Assert.That(TheRoom()!.Occupants.Single(o => o.Nick == "alice").Jid,
                            Is.EqualTo($"alice@{xmpp!.Domain}"),
                            "The real address did not reach the browser, so the occupant list cannot " +
                            "show who is actually there.");

            });

        }

        #endregion

        #region TheRoomsGoWhenTheAccountDoes()

        /// <summary>
        /// A room is a place one is in while a connection holds it open.
        /// </summary>
        [Test]
        public async Task TheRoomsGoWhenTheAccountDoes()
        {

            await JoinAsync();

            Assert.That(api!.Rooms.Count, Is.EqualTo(1));

            xmpp!.AddAccount("somebodyelse");

            await api.ApplyAccountAsync(Account("somebodyelse"), Save: false);

            Assert.That(api.Rooms.Count, Is.EqualTo(0),
                        "A room outlived the account that was in it, so the page shows rooms this " +
                        "account is not in, under a nickname that is not its own.");

        }

        #endregion

        #region ADestroyedRoomDoesNotStayInTheList()

        /// <summary>
        /// XEP-0045, section 10.9: the room is gone, and so is its row.
        /// </summary>
        /// <remarks>
        /// <b>Written because D130 nearly broke it.</b> Until then a destruction
        /// reached this app as an ordinary departure - the library could not
        /// tell the two apart - and the row disappeared by accident. The moment
        /// the library learned the difference, this app stopped hearing about it
        /// at all, and a room taken down would have stayed in the list for ever
        /// with somebody typing into it.
        ///
        /// A working thing that works for the wrong reason breaks as soon as the
        /// reason is corrected, and nothing said so. This round is what says so.
        /// </remarks>
        [Test]
        public async Task ADestroyedRoomDoesNotStayInTheList()
        {

            var session = await JoinAsync();

            Assert.That(TheRoom(), Is.Not.Null);

            await session.SendAsync(
                $"<presence from='{RoomJid}/me' to='{api!.Client!.FullJid}' type='unavailable'>" +
                    "<x xmlns='http://jabber.org/protocol/muc#user'>" +
                        "<item affiliation='none' role='none'/>" +
                        $"<destroy jid='moved@conference.{xmpp!.Domain}'>" +
                            "<reason>moving on</reason>" +
                        "</destroy>" +
                    "</x>" +
                "</presence>");

            await Until(() => TheRoom() is null,
                        "the destroyed room to leave the list");

        }

        #endregion

        #region APrivateWordIsFiledUnderThePersonAndNotTheRoom()

        /// <summary>
        /// XEP-0045, section 7.5: where a private word goes, and what answering
        /// it would reach.
        /// </summary>
        /// <remarks>
        /// <b>The conversation key is the address an answer is sent to.</b> A
        /// private message arrives from <c>room@service/nick</c>, and filed
        /// under its bare address it opens a conversation with the room - so
        /// whoever types into it addresses <c>room@service</c>. On the two
        /// services measured in D134 that reaches nobody at all; on one that
        /// treats it as a shout it reaches everybody. Neither is what somebody
        /// answering a private word meant.
        ///
        /// Nothing here could tell the two apart until D134 - both are a
        /// <c>chat</c> from a full address - so this app was right by not having
        /// the case. It has it now.
        /// </remarks>
        [Test]
        public async Task APrivateWordIsFiledUnderThePersonAndNotTheRoom()
        {

            var session = await JoinAsync();

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='chat' id='pm-1'>" +
                    "<body>for you alone</body>" +
                    "<x xmlns='http://jabber.org/protocol/muc#user'/>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Body == "for you alone"),
                        "the private word to be filed as a line in the room");

            Assert.Multiple(() =>
            {

                Assert.That(Lines().First(line => line.Body == "for you alone").Private, Is.True,
                            "It stands in the room unmarked, and the box below the conversation " +
                            "answers the room - so reading it as an ordinary line is an " +
                            "invitation to say out loud what was told in confidence.");

                api!.Chats.Snapshot(out var filed);

                Assert.That(filed.Any(chat => chat.Jid.ToString() == RoomJid.ToString()), Is.False,
                            "A conversation was opened with the room itself, so answering it " +
                            "addresses room@service - which is either nobody or everybody, and " +
                            "never the person who said it.");

            });

        }

        #endregion

        #region ACorrectionInARoomReplacesTheLineAndKeepsItsPlace()

        /// <summary>
        /// XEP-0308 in a room: the corrected line, not a second one.
        /// </summary>
        /// <remarks>
        /// Until D135 the room view had no correction handling at all, so a
        /// correction arrived as a fresh line and the mistyped one stood above
        /// it for ever - with nothing saying which of the two holds.
        /// </remarks>
        [Test]
        public async Task ACorrectionInARoomReplacesTheLineAndKeepsItsPlace()
        {

            var session = await JoinAsync();

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='groupchat' id='typo-1'>" +
                    "<body>the wrogn word</body>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Body == "the wrogn word"), "the mistyped line");

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='groupchat' id='fix-1'>" +
                    "<body>the right word</body>" +
                    "<replace id='typo-1' xmlns='urn:xmpp:message-correct:0'/>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Body == "the right word"), "the correction");

            Assert.Multiple(() =>
            {

                Assert.That(Lines().Count(line => line.Nick == "alice"), Is.EqualTo(1),
                            "The correction came in as a second line, so the wrong word stands " +
                            "above the right one with nothing saying which of them holds.");

                Assert.That(Lines().First(line => line.Nick == "alice").Corrected, Is.True,
                            "The line was replaced without saying so, which is worse than not " +
                            "replacing it: what was read a moment ago is gone and nothing marks " +
                            "that it changed.");

            });

        }

        #endregion

        #region NobodyCorrectsSomebodyElsesLine()

        /// <summary>
        /// XEP-0308, section 5: whose line a correction may replace.
        /// </summary>
        /// <remarks>
        /// <blockquote>A correction MUST only be allowed when both the original
        /// message and correction originate from the same sender ... in MUCs and
        /// MUC-PMs the correction's full-JID must match the original
        /// full-JID.</blockquote>
        ///
        /// In a room the full address is the room plus the nickname, so the
        /// nickname is the comparison. <b>Without it anybody standing in the room
        /// can rewrite anybody else's words</b> by naming their id - and the line
        /// keeps the name of the person who never wrote it, which is worse than
        /// a forged message, because it is signed by somebody who is there to be
        /// asked about it.
        ///
        /// What the nickname cannot catch is somebody leaving and another taking
        /// the name. The section says a correction across a rejoin SHOULD be
        /// refused, and in a semi-anonymous room there is nothing to tell the
        /// two apart with. Named rather than guessed at.
        /// </remarks>
        [Test]
        public async Task NobodyCorrectsSomebodyElsesLine()
        {

            var session = await JoinAsync();

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='groupchat' id='hers-1'>" +
                    "<body>what Alice said</body>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Body == "what Alice said"), "Alice's line");

            // Bob names her id.
            await session.SendAsync(
                $"<message from='{RoomJid}/bob' to='{api!.Client!.FullJid}' type='groupchat' id='his-1'>" +
                    "<body>what Bob put in her mouth</body>" +
                    "<replace id='hers-1' xmlns='urn:xmpp:message-correct:0'/>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Body == "what Bob put in her mouth"),
                        "Bob's line to arrive as its own");

            Assert.Multiple(() =>
            {

                Assert.That(Lines().First(line => line.Nick == "alice").Body,
                            Is.EqualTo("what Alice said"),
                            "Somebody else rewrote her line by naming its id, and it still " +
                            "carries her name.");

                Assert.That(Lines().Any(line => line.Nick == "bob" &&
                                                line.Body == "what Bob put in her mouth"), Is.True,
                            "Bob's own words went nowhere, so the refusal above swallowed a " +
                            "message instead of refusing a correction.");

            });

        }

        #endregion

    }

}
