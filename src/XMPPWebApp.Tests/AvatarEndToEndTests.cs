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

using System.Text;

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
    /// A real server with personal eventing, a contact who publishes a picture,
    /// and the web app that ends up with a face in its list.
    /// </summary>
    /// <remarks>
    /// <b>This is the one round that goes the whole way</b>, which is what it is
    /// for: the store's own rules are checked next door in
    /// <see cref="AvatarStoreTests"/> with no server in sight, and everything
    /// between the announcement and the disk - the +notify that makes the server
    /// push at all, the id that decides whether anything is fetched, the second
    /// round trip for the bytes, the check that they are the announced ones - is
    /// only exercised by letting somebody actually publish one.
    ///
    /// It is also the one place where the rule the web app differs from the
    /// console on is visible: <b>an announcement here causes a request.</b> The
    /// console shows a note and fetches nothing, because a terminal cannot draw
    /// a face. A page can, and a conversation list without faces is the feature
    /// not being there.
    /// </remarks>
    [TestFixture]
    public class AvatarEndToEndTests
    {

        #region Data

        private XMPPServer?  xmpp;
        private HTTPServer?  web;
        private XMPPWebAPI?  api;
        private XMPPClient?  alice;
        private String       root   = "";

        #endregion

        #region Setup / Teardown

        [SetUp]
        public async Task StartEverything()
        {

            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-avatars-e2e-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            xmpp = new XMPPServer(useTLS: false) { OfferPersonalEventing = true };
            xmpp.Start();

            xmpp.AddAccount("me");
            xmpp.AddAccount("alice");

            // Both ways, because the push follows the presence: a server sends
            // a PEP event to whoever is allowed to see the publisher.
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

            if (alice is not null)
                await alice.DisposeAsync();

            if (web is not null)
                await web.Stop();

            if (api?.Client is not null)
                await api.Client.DisposeAsync();

            if (xmpp is not null)
                await xmpp.DisposeAsync();

            alice  = null;
            web    = null;
            api    = null;
            xmpp   = null;

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

        /// <summary>
        /// Connects a second, ordinary Ratatoskr client as the contact.
        /// </summary>
        private async Task<XMPPClient> ConnectAliceAsync()
        {

            var connection = new XMPPConnection(Alice, "pw", xmpp!.Uri) {
                                 ServerCertificateValidator = xmpp.IsOwnCertificate
                             };

            alice = new XMPPClient(connection);

            await alice.ConnectAsync();

            return alice;

        }

        /// <summary>
        /// Bytes beginning like a PNG. Nothing in this process decodes one, and
        /// the browser is the program in this picture whose image decoders are
        /// sandboxed - what the store asks is only that the bytes say the same
        /// thing about themselves that the type does.
        /// </summary>
        private static Byte[] APicture(String Content = "a face")

            => [.. new Byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                .. Encoding.UTF8.GetBytes(Content)];

        private String? AvatarOfAlice()
        {

            api!.Chats.TrySnapshot(Alice, out _, out var summary, out _);

            return summary?.Avatar;

        }

        #endregion


        #region AContactsFaceArrivesAndIsKept()

        /// <summary>
        /// Alice publishes; the list has a face and the bytes are on the disk.
        /// </summary>
        /// <remarks>
        /// The whole chain in one round: the metadata is pushed because the
        /// caps say <c>urn:xmpp:avatar:metadata+notify</c>, the id says the
        /// picture is not here yet, the data node is asked for it, the bytes are
        /// checked against the id they were fetched under, and only then does
        /// the conversation point at anything.
        ///
        /// What is asserted at the end is the <b>bytes</b> and not the id. An
        /// id in the summary would pass for a client that noted the
        /// announcement and fetched nothing, which is precisely the behaviour
        /// the console has and this one deliberately does not.
        /// </remarks>
        [Test]
        public async Task AContactsFaceArrivesAndIsKept()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.IsConnected == true, "the web app to sign in");
            await Until(() => api.Chats.Count > 0,             "the roster to arrive");

            var client  = await ConnectAliceAsync();
            var picture = APicture();

            var info    = await client.PublishAvatarAsync(picture, "image/png");

            Assert.That(info, Is.Not.Null, "Alice could not publish a picture at all.");

            await Until(() => AvatarOfAlice() is not null, "the face to reach the conversation list");

            Assert.That(AvatarOfAlice(), Is.EqualTo(info!.Id),
                        "The conversation points at a different picture than the one announced.");

            Assert.That(api.Avatars, Is.Not.Null);

            Assert.That(api.Avatars!.TryGet(info.Id, out var path, out var type), Is.True,
                        "The announcement was noted and the picture never fetched - which is what the " +
                        "console does, and is not what a page that draws faces can do.");

            Assert.Multiple(() =>
            {

                Assert.That(File.ReadAllBytes(path!), Is.EqualTo(picture),
                            "What is on the disk is not the picture that was published.");

                Assert.That(type, Is.EqualTo("image/png"));

            });

        }

        #endregion

        #region TakingItDownEmptiesTheList()

        /// <summary>
        /// An empty <c>&lt;metadata/&gt;</c> is the removal, and it has to
        /// arrive as something.
        /// </summary>
        /// <remarks>
        /// A node left alone goes on announcing the old picture, so a client
        /// that treats "nothing I could read" and "there is nothing" as the same
        /// answer keeps showing a face somebody took down. The library
        /// distinguishes the two - that distinction was a mutation survivor in
        /// D122 and turned out not to exist - and this is the end of the chain
        /// that depends on it.
        /// </remarks>
        [Test]
        public async Task TakingItDownEmptiesTheList()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.IsConnected == true, "the web app to sign in");
            await Until(() => api.Chats.Count > 0,             "the roster to arrive");

            var client = await ConnectAliceAsync();

            Assert.That(await client.PublishAvatarAsync(APicture(), "image/png"), Is.Not.Null,
                        "Alice could not publish a picture at all.");

            await Until(() => AvatarOfAlice() is not null, "the face to reach the conversation list");

            Assert.That(await client.RemoveAvatarAsync(), Is.True,
                        "The server would not take the picture down.");

            await Until(() => AvatarOfAlice() is null, "the face to go again");

            // And the bytes stay: the file may be somebody else's face as well,
            // and a picture that comes back is one that need not be fetched
            // twice.
            Assert.That(Directory.GetFiles(Path.Combine(root, AvatarStore.DirectoryName),
                                           "*.png",
                                           SearchOption.AllDirectories),
                        Has.Length.EqualTo(1),
                        "Taking a picture down deleted the bytes.");

        }

        #endregion

        #region OurOwnPictureGoesUpAndComesBackDown()

        /// <summary>
        /// The other direction: this app publishes, and can show what it
        /// published.
        /// </summary>
        /// <remarks>
        /// The local copy is the point of the second half. A picture that was
        /// published but not kept here would be a face in the roster of every
        /// contact and a blank on the settings page of the person whose face it
        /// is - the page shows its own the same way it shows everybody else's,
        /// out of the store and through the one route.
        /// </remarks>
        [Test]
        public async Task OurOwnPictureGoesUpAndComesBackDown()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.IsConnected == true, "the web app to sign in");

            var picture  = APicture("our own");
            var info     = await api.Client!.PublishAvatarAsync(picture, "image/png");

            Assert.That(info, Is.Not.Null, "Nothing was published.");

            var (id, problem) = await api.Avatars!.StoreAsync(api.Client.BareJid, info!, picture);

            Assert.That(id, Is.EqualTo(info!.Id), $"The published picture was not kept here: {problem}");

            // And somebody else can read what was published, which is what
            // publishing means.
            var client = await ConnectAliceAsync();
            var theirs = await client.FetchAvatarInfoAsync(JID.Parse($"me@{xmpp!.Domain}"));

            Assert.That(theirs, Is.Not.Null, "A contact could not ask what our picture is.");

            Assert.That(theirs!.Select(one => one.Id), Does.Contain(info.Id),
                        "A contact is told about a different picture than the one that was published.");

            Assert.That(await api.Client.RemoveAvatarAsync(), Is.True,
                        "The picture could not be taken down.");

            var afterwards = await client.FetchAvatarInfoAsync(JID.Parse($"me@{xmpp.Domain}"));

            Assert.That(afterwards, Is.Not.Null.And.Empty,
                        "A contact asking after the removal is told there is still a picture - an " +
                        "empty list is the answer 'this person has none', and null would be 'could " +
                        "not ask'.");

        }

        #endregion

        #region AStrangersAnnouncementIsNotFetched()

        /// <summary>
        /// Only contacts make this machine fetch anything.
        /// </summary>
        /// <remarks>
        /// <b>The announcement has to arrive for this to be about anything</b>,
        /// and making it arrive is the work of the round. Who is pushed a PEP
        /// event is decided by the <i>publisher's</i> roster, so the stranger is
        /// given one that has us in it with <c>both</c> while ours has no
        /// stranger at all. That is not a contrived shape: it is what a far
        /// server that keeps a one-sided subscription, or simply does not care,
        /// looks like from here.
        ///
        /// The first version of this had no such roster. Nothing was fetched,
        /// the round was green, and a mutation that deleted the check survived
        /// it - because the event had never left the server. The test was
        /// measuring the far side's discipline and claiming, in this very
        /// remark, that it was measuring ours.
        ///
        /// <b>What the assertions here cannot show is that the event arrives</b>,
        /// since they assert that nothing happened. The mutation is the proof:
        /// with the roster check deleted this round fails, and it could not fail
        /// if the announcement had never reached this side.
        /// </remarks>
        [Test]
        public async Task AStrangersAnnouncementIsNotFetched()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.IsConnected == true, "the web app to sign in");
            await Until(() => api.Chats.Count > 0,             "the roster to arrive");

            var stranger = JID.Parse($"mallory@{xmpp!.Domain}");

            xmpp.AddAccount("mallory");

            // One-sided on purpose: the stranger counts us as a subscriber, so
            // the server pushes us the event. We do not count the stranger as
            // anything, so nothing is fetched.
            Roster("mallory", "me");

            Assert.That(api.Client!.Roster.GetItem(stranger), Is.Null,
                        "The stranger is in our roster, so this round is about a contact.");

            var connection = new XMPPConnection(stranger, "pw", xmpp.Uri) {
                                 ServerCertificateValidator = xmpp.IsOwnCertificate
                             };

            alice = new XMPPClient(connection);

            await alice.ConnectAsync();

            Assert.That(await alice.PublishAvatarAsync(APicture("a stranger"), "image/png"), Is.Not.Null,
                        "The stranger could not publish at all, so this round proves nothing.");

            // There is nothing to wait for - the assertion is that nothing
            // happens - so the wait is for the announcement to have had every
            // chance to arrive and be acted on.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {

                Assert.That(api.Chats.Count, Is.EqualTo(1),
                            "A stranger's picture put a row in the conversation list.");

                Assert.That(Directory.Exists(Path.Combine(root, AvatarStore.DirectoryName))
                                ? Directory.GetFiles(Path.Combine(root, AvatarStore.DirectoryName),
                                                     "*.png",
                                                     SearchOption.AllDirectories)
                                : [],
                            Is.Empty,
                            "A picture was fetched and written for somebody who is not a contact.");

            });

        }

        #endregion

    }

}
