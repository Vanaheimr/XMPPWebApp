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
    /// A real XMPP server, a real contact, a real encrypted message - and the
    /// web app's own chat store at the far end of it.
    /// </summary>
    /// <remarks>
    /// <b>This is the test the unit tests beside it cannot be.</b> Everything
    /// they check is a decision taken on a value that was handed to them; what
    /// is checked here is whether anything ever hands that value over. Switching
    /// OMEMO on hangs off a connection state change, the decrypted message
    /// arrives on a second event that a guard may throw away, and both of those
    /// are wiring rather than logic - the kind that is right in every line and
    /// still does nothing.
    ///
    /// The server speaks <c>ws://</c>, which this application refuses unless it
    /// is told to (<c>--insecure</c>), and the settings here say so. The
    /// alternative would be the test server's self-signed certificate, and
    /// AccountSettings offers no way to trust one - rightly, because a web app
    /// that could would be a web app that can be talked into it.
    /// </remarks>
    [TestFixture]
    public class OmemoEndToEndTests
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

            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-omemo-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            xmpp = new XMPPServer(useTLS: false);
            xmpp.Start();

            xmpp.AddAccount("me");
            xmpp.AddAccount("alice");

            // The two-sided subscription a complete handshake would have left
            // behind. OMEMO fetches the other side's PEP nodes, and a server is
            // entitled to answer those only to somebody who is allowed to see
            // the presence.
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

        /// <summary>
        /// The account this web app signs in with.
        /// </summary>
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

        /// <summary>
        /// A second client, straight off the library, standing in for whatever
        /// the contact really uses.
        /// </summary>
        private XMPPClient ClientFor(String LocalPart)

            => new (new XMPPConnection(JID.Parse($"{LocalPart}@{xmpp!.Domain}"),
                                       "pw",
                                       xmpp.Uri));

        #endregion


        #region AnEncryptedMessage_ArrivesWithItsLockOn()

        /// <summary>
        /// Alice writes encrypted; the conversation on the page shows it, says
        /// it was encrypted, and the server never saw the words.
        /// </summary>
        /// <remarks>
        /// The last of those three is the one that makes the other two worth
        /// anything. Without it this test would pass just as well if the
        /// message had travelled in the clear beside a lock drawn on it.
        /// </remarks>
        [Test]
        public async Task AnEncryptedMessage_ArrivesWithItsLockOn()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.OmemoEnabled == true,
                        "the web app to announce its OMEMO device");

            var alice = ClientFor("alice");

            try
            {

                await alice.ConnectAsync();

                Assert.That(await alice.EnableOmemoAsync(), Is.True,
                            "Alice could not switch OMEMO on.");

                const String secret = "Shall we meet at eight?";

                var skipped = await alice.SendEncryptedMessageAsync(JID.Parse($"me@{xmpp!.Domain}"), secret);

                Assert.That(skipped, Is.Empty,
                            "Not every device could read along: " +
                            String.Join(", ", skipped.Select(device => $"{device.Jid}/{device.DeviceId}: {device.Reason}")));

                await Until(() => Message() is not null, "the decrypted message in the chat store");

                var message = Message()!;

                Assert.Multiple(() =>
                {

                    Assert.That(message.Body,       Is.EqualTo(secret));
                    Assert.That(message.Direction,  Is.EqualTo(MessageDirection.Incoming));
                    Assert.That(message.Chat,       Is.EqualTo(JID.Parse($"alice@{xmpp.Domain}")),
                                "it belongs in the conversation with Alice and nowhere else");

                    Assert.That(message.Encrypted,  Is.True,
                                "the line does not say how it arrived");
                    Assert.That(message.Identity,   Is.EqualTo(OmemoIdentityCheck.New),
                                "Alice's device was supposedly known here already");

                });

                // And the check without which none of the above means anything.
                var stanzas = xmpp.Sessions.SelectMany(session => session.Received.Concat(session.Sent)).ToList();

                Assert.Multiple(() =>
                {

                    Assert.That(stanzas.Any(stanza => stanza.Contains(secret, StringComparison.Ordinal)),
                                Is.False,
                                "the words stand in a stanza the server has seen");

                    Assert.That(stanzas.Any(stanza => stanza.Contains("urn:xmpp:omemo:2", StringComparison.Ordinal)),
                                Is.True,
                                "nothing encrypted went over the wire at all - then this test is measuring something else");

                });

            }
            finally
            {
                await alice.DisposeAsync();
            }

        }

        #endregion

        #region APlainMessage_CarriesNoLock()

        /// <summary>
        /// The same path without encryption, so that the lock is known to mean
        /// something.
        /// </summary>
        /// <remarks>
        /// Written because the test above would pass even if <c>Encrypted</c>
        /// were true for every message that ever arrives - and then the lock
        /// would say nothing at all while looking like it did.
        /// </remarks>
        [Test]
        public async Task APlainMessage_CarriesNoLock()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.OmemoEnabled == true,
                        "the web app to announce its OMEMO device");

            var alice = ClientFor("alice");

            try
            {

                await alice.ConnectAsync();

                await alice.SendMessageAsync(JID.Parse($"me@{xmpp!.Domain}"), "In the clear.");

                await Until(() => Message() is not null, "the message in the chat store");

                var message = Message()!;

                Assert.Multiple(() =>
                {
                    Assert.That(message.Body,       Is.EqualTo("In the clear."));
                    Assert.That(message.Encrypted,  Is.False);
                    Assert.That(message.Identity,   Is.Null);
                });

            }
            finally
            {
                await alice.DisposeAsync();
            }

        }

        #endregion

        #region AKeptStore_KeepsTheFingerprintAcrossAStart()

        /// <summary>
        /// The keys survive the process - which is the whole reason they are on
        /// disk.
        /// </summary>
        /// <remarks>
        /// A device whose fingerprint changes at every start is a device nobody
        /// can ever compare anything with, and blind trust before verification
        /// rests entirely on the verification being possible later. So the
        /// store is written once, read again by a second API against the same
        /// directory, and the fingerprint has to be the one from before.
        /// </remarks>
        [Test]
        public async Task AKeptStore_KeepsTheFingerprintAcrossAStart()
        {

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.OmemoEnabled == true, "the first start to announce its device");

            var first  = api.Client!.Omemo!.Fingerprint;
            var device = api.Client.Omemo.Identity.DeviceId;

            await api.Client.DisposeAsync();

            // A second API on the same private directory - what a restart is,
            // as far as the keys are concerned.
            var second = new XMPPWebAPI(
                             web!,
                             new AccountFile(Path.Combine(root, "xmpp-account.json")),
                             RootPath:        HTTPPath.Parse("/api2"),
                             OmemoDirectory:  Path.Combine(root, "omemo"),
                             DataDirectory:   root
                         );

            try
            {

                await second.LoadDatabase();
                await second.ApplyAccountAsync(Account("me"), Save: false);

                await Until(() => second.Client?.OmemoEnabled == true, "the second start to announce its device");

                Assert.Multiple(() =>
                {
                    Assert.That(second.Client!.Omemo!.Fingerprint,          Is.EqualTo(first),
                                "a new fingerprint at every start makes every comparison worthless");
                    Assert.That(second.Client.Omemo.Identity.DeviceId,      Is.EqualTo(device),
                                "and a new device number leaves the old one in the list forever");
                });

            }
            finally
            {

                if (second.Client is not null)
                    await second.Client.DisposeAsync();

            }

        }

        #endregion

        #region OnUnix_TheKeyStore_IsOwnerOnly()

        /// <summary>
        /// The one file in this program whose loss and whose theft are both
        /// serious.
        /// </summary>
        /// <remarks>
        /// It holds the identity key and every chain key of every session, and
        /// it is not encrypted - so on Unix it wants 0600 with 0700 around it,
        /// and this is where that is checked rather than asserted in a comment.
        /// The directory is the half that goes wrong quietly: the library
        /// creating it would take whatever the umask gives, and a 0755
        /// directory is a listing of who this account talks to.
        ///
        /// On Windows the directory decides and there are no modes to read -
        /// see PrivatePathsTests for the half that holds there.
        /// </remarks>
        [Test]
        public async Task OnUnix_TheKeyStore_IsOwnerOnly()
        {

            // Written out rather than called from a helper, because CA1416 has
            // to see it: an analyser cannot know that Assert.Ignore never
            // returns, and GetUnixFileMode is unsupported on Windows.
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix file modes. On Windows the directory decides, not the file.");
                return;
            }

            await api!.ApplyAccountAsync(Account("me"), Save: false);

            await Until(() => api.Client?.OmemoEnabled == true, "the web app to announce its OMEMO device");

            var directory = Path.Combine(root, "omemo");
            var files     = Directory.GetFiles(directory);

            Assert.That(files, Has.Length.EqualTo(1), "one key file, named after the account");

            // Read outside the Assert.Multiple below: CA1416 follows the guard
            // above through straight-line code and not into a lambda.
            var fileMode       = File.GetUnixFileMode(files[0]);
            var directoryMode  = File.GetUnixFileMode(directory);

            Assert.Multiple(() =>
            {

                Assert.That(fileMode,
                            Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite),
                            "0600 - whoever reads this file reads the conversations along");

                Assert.That(directoryMode,
                            Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                            "0700 - and the names in it are a list of who this account talks to");

            });

        }

        #endregion


        /// <summary>
        /// The one message in the one conversation, or null while there is
        /// none yet.
        /// </summary>
        private ChatMessage? Message()
        {

            api!.Chats.Snapshot(out var chats);

            if (chats.Count == 0 ||
                !api.Chats.TrySnapshot(chats[0].Jid, out _, out _, out var messages))
            {
                return null;
            }

            return messages.LastOrDefault();

        }

    }

}
