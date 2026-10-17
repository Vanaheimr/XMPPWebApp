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

using System.Net;
using System.Text;
using System.Xml.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
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

        private const String WebUser      = "admin";
        private const String WebPassword  = "correct-horse-battery-staple";

        /// <summary>
        /// A browser signed in to the web page, for what only the routes do:
        /// look a line up, choose its name, and put the stanza together.
        /// </summary>
        private async Task<HttpClient> BrowserAsync()
        {

            Assert.That(await api!.CreateUserIfNotExists(
                                   User_Id.Parse(WebUser),
                                   I18NString.Create(WebUser),
                                   SimpleEMailAddress.Parse($"{WebUser}@localhost"),
                                   Password:                  WebPassword,
                                   IsAuthenticated:           true,
                                   SkipNewUserEMail:          true,
                                   SkipNewUserNotifications:  true,
                                   SkipDefaultNotifications:  true
                               ),
                        Is.Not.Null,
                        "the account the browser signs in as");

            var browser = new HttpClient(new HttpClientHandler {
                                             CookieContainer    = new CookieContainer(),
                                             UseCookies         = true,
                                             AllowAutoRedirect  = false
                                         }) {
                              BaseAddress = new Uri($"http://127.0.0.1:{web!.TCPPort}/"),
                              Timeout     = TimeSpan.FromSeconds(30)
                          };

            var signIn = await Post(browser, "api/auth/login",
                                    new JObject(new JProperty("login",    WebUser),
                                                new JProperty("password", WebPassword)));

            Assert.That(signIn.IsSuccessStatusCode, Is.True, "the sign-in");

            return browser;

        }

        private static Task<HttpResponseMessage> Post(HttpClient Browser, String Path, JObject JSON)

            => Browser.PostAsync(Path, new StringContent(JSON.ToString(), Encoding.UTF8, "application/json"));

        private String MessagesPath
            => $"api/v1/rooms/{Uri.EscapeDataString(RoomJid.ToString())}/messages";

        /// <summary>
        /// The last stanza this app sent that carries the given namespace.
        /// </summary>
        private static XElement LastSent(XMPPSession Session, String Namespace)
            => XElement.Parse(Session.Received.Last(frame => frame.Contains(Namespace, StringComparison.Ordinal)));

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

        #region ARetractedLineLosesItsWordsAndKeepsItsPlace()

        /// <summary>
        /// XEP-0424 in a room: the line is emptied, not removed.
        /// </summary>
        /// <remarks>
        /// <b>Removing it outright would reshuffle the room under somebody who
        /// is reading it</b>, and an answer pointing at it would come to point
        /// at nothing. Keeping the words would defeat the request entirely. So
        /// the place stays, the words go, and the mark says which of the two
        /// happened.
        ///
        /// The retraction names the name the <i>room</i> gave the line and not
        /// the id on the stanza - the one thing XEP-0424 says outright about
        /// group chats - which is why the store carries both.
        /// </remarks>
        [Test]
        public async Task ARetractedLineLosesItsWordsAndKeepsItsPlace()
        {

            var session = await JoinAsync();

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='groupchat' id='said-1'>" +
                    "<body>something better left unsaid</body>" +
                    $"<stanza-id xmlns='urn:xmpp:sid:0' id='room-42' by='{RoomJid}'/>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Body == "something better left unsaid"),
                        "the line");

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='groupchat' id='retract-1'>" +
                    "<retract id='room-42' xmlns='urn:xmpp:message-retract:1'/>" +
                    "<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:message-retract:1'/>" +
                    "<body>A previous message was retracted.</body>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Retracted), "the retraction");

            Assert.Multiple(() =>
            {

                Assert.That(Lines(), Has.Count.EqualTo(1),
                            "The retraction was filed as a line of its own, so the fallback " +
                            "sentence stands in the room as though somebody had typed it - " +
                            "beside the line it was supposed to take back.");

                Assert.That(Lines()[0].Body, Is.Empty,
                            "The words are still there, so the retraction did nothing but add a " +
                            "mark to them.");

                Assert.That(Lines()[0].Nick, Is.EqualTo("alice"),
                            "The emptied line lost whose it was.");

            });

        }

        #endregion

        #region NobodyTakesBackSomebodyElsesLine()

        /// <summary>
        /// XEP-0424: whose line a retraction may empty.
        /// </summary>
        /// <remarks>
        /// <blockquote>a retraction ... MUST only be processed when both the
        /// original message and the retraction request are received from the
        /// same bare-JID (in a one-on-one conversation) or full-JID (in a
        /// non-anonymous MUC).</blockquote>
        ///
        /// The same rule as XEP-0308's and a worse failure without it: a
        /// correction under somebody else's name puts words in their mouth, and
        /// this takes their words away. Both leave the line carrying the name of
        /// somebody who did not do it.
        /// </remarks>
        [Test]
        public async Task NobodyTakesBackSomebodyElsesLine()
        {

            var session = await JoinAsync();

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='groupchat' id='hers-1'>" +
                    "<body>what Alice said</body>" +
                    $"<stanza-id xmlns='urn:xmpp:sid:0' id='room-7' by='{RoomJid}'/>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Body == "what Alice said"), "her line");

            // Bob names the room's name for her line.
            await session.SendAsync(
                $"<message from='{RoomJid}/bob' to='{api!.Client!.FullJid}' type='groupchat' id='his-1'>" +
                    "<retract id='room-7' xmlns='urn:xmpp:message-retract:1'/>" +
                    "<body>A previous message was retracted.</body>" +
                "</message>");

            // Something has to have happened before the absence below means
            // anything, so a line of Bob's own follows and is waited for.
            await session.SendAsync(
                $"<message from='{RoomJid}/bob' to='{api!.Client!.FullJid}' type='groupchat' id='his-2'>" +
                    "<body>and then Bob said this</body>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Body == "and then Bob said this"),
                        "a line after the attempt");

            Assert.That(Lines().First(line => line.Nick == "alice").Body,
                        Is.EqualTo("what Alice said"),
                        "Somebody else emptied her line by naming its id, and it still carries " +
                        "her name.");

        }

        #endregion

        #region AnAnswerInARoomPointsAtTheRoomsNameForTheLine()

        /// <summary>
        /// XEP-0461 in a room, from this side.
        /// </summary>
        /// <remarks>
        /// <b>The reference is the name the room gave the line</b> (section 4):
        /// the id on the stanza is the sender's, and everybody present may see a
        /// different one. Pointing at it points, for every other reader, at a
        /// line that is not the one that was meant - or at nothing.
        ///
        /// And the quotation says whose words they were, because in a room
        /// there is more than one person they could be from.
        /// </remarks>
        [Test]
        public async Task AnAnswerInARoomPointsAtTheRoomsNameForTheLine()
        {

            var session = await JoinAsync();

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='groupchat' id='hers-1'>" +
                    "<body>Shall we meet at eight?</body>" +
                    $"<stanza-id xmlns='urn:xmpp:sid:0' id='room-7' by='{RoomJid}'/>" +
                "</message>");

            await Until(() => Lines().Any(line => line.RetractableId == "room-7"), "her line");

            using var browser = await BrowserAsync();

            var answer = await Post(browser, MessagesPath,
                                    new JObject(new JProperty("body",       "Eight is fine"),
                                                new JProperty("repliesTo",  "hers-1")));

            Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.Created), await answer.Content.ReadAsStringAsync());

            await Until(() => session.CountReceived(MessageReply.Namespace) > 0, "the answer on the wire");

            var stanza    = LastSent(session, MessageReply.Namespace);
            var reply     = stanza.Element(XName.Get("reply",    MessageReply.Namespace));
            var fallback  = stanza.Elements().FirstOrDefault(element => element.Name.LocalName == "fallback");
            var mine      = Lines().Single(line => line.Mine);

            Assert.Multiple(() =>
            {

                Assert.That(stanza.Attribute("type")?.Value,  Is.EqualTo("groupchat"));

                Assert.That(reply?.Attribute("id")?.Value,    Is.EqualTo("room-7"),
                            "The answer named the id on her stanza rather than the room's name for " +
                            "the line, which other readers of the room may never have seen.");

                Assert.That(reply?.Attribute("to")?.Value,    Is.EqualTo($"{RoomJid}/alice"));

                Assert.That(fallback?.Attribute("for")?.Value, Is.EqualTo(MessageReply.Namespace),
                            "The quoted lines went out unmarked, so a client that shows its own " +
                            "quotation shows this one as well.");

                Assert.That(stanza.Elements().First(element => element.Name.LocalName == "body").Value,
                            Is.EqualTo("> alice:\n> Shall we meet at eight?\nEight is fine"));

                Assert.That(mine.Body,       Is.EqualTo("Eight is fine"), "the quotation stays out of the text");
                Assert.That(mine.RepliesTo,  Is.EqualTo("room-7"));
                Assert.That(mine.Quote,      Is.EqualTo("> alice:\n> Shall we meet at eight?\n"));

            });

        }

        #endregion

        #region APrivateWordIsNotAnsweredInTheRoom()

        /// <summary>
        /// XEP-0045, section 7.5, from the other side: an answer goes to the whole
        /// room and carries what it answers along.
        /// </summary>
        /// <remarks>
        /// For a word said to us alone that is repeating it out loud to
        /// everybody present, in the speaker's name.
        /// </remarks>
        [Test]
        public async Task APrivateWordIsNotAnsweredInTheRoom()
        {

            var session = await JoinAsync();

            await session.SendAsync(
                $"<message from='{RoomJid}/alice' to='{api!.Client!.FullJid}' type='chat' id='pm-1'>" +
                    "<body>for you alone</body>" +
                    "<x xmlns='http://jabber.org/protocol/muc#user'/>" +
                "</message>");

            await Until(() => Lines().Any(line => line.Private), "the private word");

            using var browser = await BrowserAsync();

            var answer = await Post(browser, MessagesPath,
                                    new JObject(new JProperty("body",       "Sure!"),
                                                new JProperty("repliesTo",  "pm-1")));

            Assert.Multiple(() =>
            {

                Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
                            "An answer to a private word was sent to the whole room, quoting it.");

                Assert.That(session.CountReceived("for you alone"), Is.EqualTo(0),
                            "The private word went back out to the room.");

                Assert.That(Lines().Any(line => line.Mine), Is.False);

            });

        }

        #endregion

        #region OurOwnLineIsTakenBackByTheNameItsReflectionBrought()

        /// <summary>
        /// XEP-0359 and XEP-0424: a line of ours learns its name in the room
        /// from its reflection, and is taken back under that name.
        /// </summary>
        /// <remarks>
        /// <b>The line put in on sending cannot know the name</b> - the room
        /// gives it on the way through - and the reflection, which is dropped
        /// as a duplicate, was the only stanza that carries it. Until it was
        /// read, nothing this app said in a room could be taken back: the
        /// page named the line by its id and the route looked for the room's
        /// name, which no line of ours ever had.
        /// </remarks>
        [Test]
        public async Task OurOwnLineIsTakenBackByTheNameItsReflectionBrought()
        {

            var session  = await JoinAsync();

            using var browser = await BrowserAsync();

            var said     = await Post(browser, MessagesPath, new JObject(new JProperty("body", "something hasty")));

            Assert.That(said.StatusCode, Is.EqualTo(HttpStatusCode.Created), await said.Content.ReadAsStringAsync());

            var id       = JObject.Parse(await said.Content.ReadAsStringAsync())["message"]!.Value<String>("id")!;

            await session.SendAsync(
                $"<message from='{RoomJid}/me' to='{api!.Client!.FullJid}' type='groupchat' id='{id}'>" +
                    "<body>something hasty</body>" +
                    $"<stanza-id xmlns='urn:xmpp:sid:0' id='room-9' by='{RoomJid}'/>" +
                "</message>");

            await Until(() => Lines().Any(line => line.RetractableId == "room-9"), "the line to learn its name");

            Assert.That(Lines(), Has.Count.EqualTo(1), "The reflection was shown as a second line.");

            var taken    = await Post(browser, MessagesPath, new JObject(new JProperty("retracts", id)));

            Assert.That(taken.StatusCode, Is.EqualTo(HttpStatusCode.OK), await taken.Content.ReadAsStringAsync());

            await Until(() => session.CountReceived("urn:xmpp:message-retract:1") > 0, "the retraction on the wire");

            var retract  = LastSent(session, "urn:xmpp:message-retract:1")
                               .Elements().First(element => element.Name.LocalName == "retract");

            Assert.Multiple(() =>
            {

                Assert.That(retract.Attribute("id")?.Value, Is.EqualTo("room-9"),
                            "The retraction named the id on our stanza rather than the room's name " +
                            "for the line (XEP-0424 in a group chat).");

                Assert.That(Lines()[0].Retracted, Is.True);
                Assert.That(Lines()[0].Body,      Is.Empty);

            });

        }

        #endregion

        #region AControlCharacterInALine_DoesNotEndTheConnection()

        /// <summary>
        /// What a browser lets somebody paste and an XML stream may not carry,
        /// said in a room.
        /// </summary>
        /// <remarks>
        /// A conversation has cleaned its lines since they were first sent; a
        /// room never did. A NUL or a vertical tab is not escaped by anybody -
        /// it becomes a stream error, and the stream is the one every
        /// conversation and every room of this account travels on. One pasted
        /// character took all of them down.
        ///
        /// <b>Checked twice: on the stanza and on the connection.</b> Prosody
        /// and ejabberd answer a stanza that is not well-formed XML 1.0 with
        /// not-well-formed (RFC 6120, 4.9.3.13) and close the stream, and so
        /// does the test server since Ratatoskr learnt to - before that it read
        /// frames as text and forgave this, which is how the gap went
        /// unnoticed. The frame has to parse, which is the condition this side
        /// controls, and the connection has to be standing afterwards.
        /// </remarks>
        [Test]
        public async Task AControlCharacterInALine_DoesNotEndTheConnection()
        {

            var session = await JoinAsync();

            using var browser = await BrowserAsync();

            var said = await Post(browser, MessagesPath,
                                  new JObject(new JProperty("body", "one\u0001two\u000Bthree￿")));

            Assert.That(said.StatusCode, Is.EqualTo(HttpStatusCode.Created), await said.Content.ReadAsStringAsync());

            await Until(() => session.CountReceived("three") > 0, "the line on the wire");

            var frame = session.Received.Last(received => received.Contains("three", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {

                Assert.That(() => XElement.Parse(frame), Throws.Nothing,
                            "The stanza is not well-formed XML 1.0, and a real server ends the stream " +
                            "over it - for every room and conversation of this account.");

                Assert.That(Lines().Single(line => line.Mine).Body, Is.EqualTo("onetwothree"));

                Assert.That(api!.Client!.IsConnected, Is.True);

            });

        }

        #endregion

        #region ALineTooLong_IsRefused()

        [Test]
        public async Task ALineTooLong_IsRefused()
        {

            var session = await JoinAsync();

            using var browser = await BrowserAsync();

            var said = await Post(browser, MessagesPath,
                                  new JObject(new JProperty("body", new String('x', XMPPWebAPI.MaxMessageLength + 1))));

            Assert.Multiple(() =>
            {
                Assert.That(said.StatusCode,                                         Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(session.CountReceived(new String('x', 100)),             Is.EqualTo(0), "it went out anyway");
                Assert.That(Lines().Any(line => line.Mine),                          Is.False);
            });

        }

        #endregion

        #region AControlCharacterInTheSubject_DoesNotEndTheConnection()

        /// <summary>
        /// The subject travels as the text of a message, and had the same gap.
        /// </summary>
        /// <remarks>
        /// Checked on the stanza, for the reason the test above gives.
        /// </remarks>
        [Test]
        public async Task AControlCharacterInTheSubject_DoesNotEndTheConnection()
        {

            var session = await JoinAsync();

            using var browser = await BrowserAsync();

            var set = await Post(browser, $"api/v1/rooms/{Uri.EscapeDataString(RoomJid.ToString())}/subject",
                                 new JObject(new JProperty("subject", "Tref\u0007fen heute")));

            Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await set.Content.ReadAsStringAsync());

            await Until(() => session.CountReceived("fen heute") > 0, "the subject on the wire");

            var frame = session.Received.Last(received => received.Contains("fen heute", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {

                Assert.That(() => XElement.Parse(frame), Throws.Nothing,
                            "The subject stanza is not well-formed XML 1.0, and a real server ends " +
                            "the stream over it.");

                Assert.That(frame, Does.Contain("Treffen heute"));

            });

        }

        #endregion

        #region ANicknameThatIsNoResource_IsRefusedBeforeAnythingHappens()

        /// <summary>
        /// A nickname is the resource of the address the room writes somebody
        /// under (RFC 7622) - and the library puts the room into its table
        /// before it builds that address.
        /// </summary>
        [Test]
        public async Task ANicknameThatIsNoResource_IsRefusedBeforeAnythingHappens()
        {

            var session = await JoinAsync();

            using var browser = await BrowserAsync();

            var other   = $"other@conference.{xmpp!.Domain}";

            var joined  = await Post(browser, "api/v1/rooms",
                                     new JObject(new JProperty("jid",  other),
                                                 new JProperty("nick", "me\u0001")));

            Assert.Multiple(() =>
            {

                Assert.That(joined.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

                Assert.That(api!.Rooms.Count, Is.EqualTo(1),
                            "The refused room stands in the list anyway.");

                Assert.That(session.CountReceived(other), Is.EqualTo(0),
                            "Something was sent to the room the nickname was refused for.");

                Assert.That(api.Client!.IsConnected, Is.True);

            });

        }

        #endregion

        #region OurEncryptedLineLearnsItsNameAndCanBeAnswered()

        /// <summary>
        /// XEP-0359 for an encrypted line of ours: its reflection cannot be
        /// read, and it still brings the name the room gave the line.
        /// </summary>
        /// <remarks>
        /// A plain line of ours learns its name from its reflection; an
        /// encrypted one did not, because the library dropped the unreadable
        /// reflection whole - name and all. So every encrypted line this app
        /// said in a room could be neither answered nor taken back: both have
        /// to point at the room's name for it.
        /// </remarks>
        [Test]
        public async Task OurEncryptedLineLearnsItsNameAndCanBeAnswered()
        {

            var session = await JoinAsync(NonAnonymous: true);

            await Until(() => api!.Client!.OmemoEnabled,            "the web app to announce its OMEMO device");
            await Until(() => TheRoom()!.CannotEncrypt is null,     "the room to become one that can be encrypted in");

            using var browser = await BrowserAsync();

            var said    = await Post(browser, MessagesPath, new JObject(new JProperty("body", "Only for the people here.")));

            Assert.That(said.StatusCode, Is.EqualTo(HttpStatusCode.Created), await said.Content.ReadAsStringAsync());

            var line    = JObject.Parse(await said.Content.ReadAsStringAsync())["message"]!;
            var id      = line.Value<String>("id")!;

            Assert.That(line.Value<Boolean>("encrypted"), Is.True, "the line did not go encrypted - then this tests something else");

            // The room hands it back: unreadable here, its name on the outside.
            await Until(() => session.Received.Any(frame => frame.Contains($"id='{id}'", StringComparison.Ordinal)),
                        "our encrypted line on the wire");

            var sent      = session.Received.First(frame => frame.Contains($"id='{id}'", StringComparison.Ordinal));

            var reflected = $"<message from='{RoomJid}/me' to='{api!.Client!.FullJid}' type='groupchat' id='{id}'>" +
                            sent[(sent.IndexOf('>') + 1)..].Replace("</message>",
                                                                   $"<stanza-id xmlns='urn:xmpp:sid:0' id='room-11' by='{RoomJid}'/></message>");

            await session.SendAsync(reflected);

            await Until(() => Lines().Any(line => line.RetractableId == "room-11"), "our encrypted line to learn its name");

            // And now it can be answered.
            var answer  = await Post(browser, MessagesPath,
                                     new JObject(new JProperty("body",       "And so I answer myself."),
                                                 new JProperty("repliesTo",  id)));

            Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.Created), await answer.Content.ReadAsStringAsync());

            Assert.Multiple(() =>
            {

                Assert.That(Lines().Count(line => line.Mine), Is.EqualTo(2),
                            "The reflection stood as a line of its own.");

                Assert.That(Lines().Single(line => line.Body == "And so I answer myself.").RepliesTo,
                            Is.EqualTo("room-11"));

            });

        }

        #endregion

        #region OurCorrectionsNameTheOriginalAndKeepTheLine()

        /// <summary>
        /// XEP-0308 in a room, from this side: every correction names the
        /// original line, and the line keeps its id and its place.
        /// </summary>
        /// <remarks>
        /// Section 5 since 1.1.0: the payload is replaced, not the identity of
        /// the message, and a correction of a correction names the original
        /// again. A line that took the id of each correction stanza was a new
        /// line to the page, and the reflection of the correction - carrying
        /// the original's id in its &lt;replace/&gt; - named a line that was no
        /// longer there.
        /// </remarks>
        [Test]
        public async Task OurCorrectionsNameTheOriginalAndKeepTheLine()
        {

            var session  = await JoinAsync();

            using var browser = await BrowserAsync();

            var said     = await Post(browser, MessagesPath, new JObject(new JProperty("body", "the wrogn word")));
            var id       = JObject.Parse(await said.Content.ReadAsStringAsync())["message"]!.Value<String>("id")!;

            var first    = await Post(browser, MessagesPath, new JObject(new JProperty("body", "the wrnog word"), new JProperty("corrects", true)));

            Assert.That(first.StatusCode,  Is.EqualTo(HttpStatusCode.OK), await first.Content.ReadAsStringAsync());

            var second   = await Post(browser, MessagesPath, new JObject(new JProperty("body", "the right word"), new JProperty("corrects", true)));

            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK), await second.Content.ReadAsStringAsync());

            await Until(() => session.CountReceived("urn:xmpp:message-correct:0") >= 2, "both corrections on the wire");

            var named    = session.Received
                                  .Where (frame => frame.Contains("urn:xmpp:message-correct:0", StringComparison.Ordinal))
                                  .Select(frame => XElement.Parse(frame).Elements()
                                                           .First(element => element.Name.LocalName == "replace")
                                                           .Attribute("id")?.Value)
                                  .ToList();

            // The room hands the second correction back, as it hands back
            // everything; it names the original, and must change nothing.
            await session.SendAsync(
                $"<message from='{RoomJid}/me' to='{api!.Client!.FullJid}' type='groupchat' id='fix-2'>" +
                    "<body>the right word</body>" +
                    $"<replace id='{id}' xmlns='urn:xmpp:message-correct:0'/>" +
                "</message>");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {

                Assert.That(named, Is.EqualTo(new[] { id, id }),
                            "A correction named the previous correction instead of the original line.");

                Assert.That(Lines(), Has.Count.EqualTo(1),
                            "The corrected line or the reflection of the correction stood as a line of its own.");

                Assert.That(Lines()[0].Id,         Is.EqualTo(id), "The line took the id of a correction stanza.");
                Assert.That(Lines()[0].Body,       Is.EqualTo("the right word"));
                Assert.That(Lines()[0].Corrected,  Is.True);

            });

        }

        #endregion

    }

}
