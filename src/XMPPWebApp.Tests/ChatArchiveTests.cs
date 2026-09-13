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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// What the archive keeps, where it puts it, and what comes back out.
    /// </summary>
    /// <remarks>
    /// The archive is the only part of this program whose mistakes are
    /// permanent: a message that was not written is not there next year, and a
    /// message written to the wrong place is worse than one not written at all.
    /// So what is checked here is the layout on disk as much as the round trip
    /// - the directory per conversation, the file per month, the ISO 8601 name
    /// of a stored file - and the two things a path built from a stranger's
    /// text must never do.
    ///
    /// Nothing here reaches the network: MediaStore is not exercised, only the
    /// rules that decide what it would be asked to fetch.
    /// </remarks>
    [TestFixture]
    public class ChatArchiveTests
    {

        #region Data

        private static readonly JID  me     = JID.Parse("me@example.org");
        private static readonly JID  alice  = JID.Parse("alice@example.org");
        private static readonly JID  bob    = JID.Parse("bob@example.org");

        private static readonly DateTimeOffset  september  = new (2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset  october    = new (2026, 10, 3, 8, 30, 0, TimeSpan.Zero);

        private String root = "";

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void CreateArchiveDirectory()
        {
            root = Path.Combine(Path.GetTempPath(), $"xmppwebapp-archive-{Guid.NewGuid():N}");
        }

        [TearDown]
        public void RemoveArchiveDirectory()
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // A temporary directory that will not go is the operating
                // system's business, not this test's.
            }
        }

        #endregion

        #region (private) Open(KeepMedia = false) / Incoming(...) / Outgoing(...)

        /// <summary>
        /// An archive for <see cref="me"/>, without the media store: nothing in
        /// here may reach the network.
        /// </summary>
        private ChatArchive Open(Boolean KeepMedia = false)
        {

            var archive = new ChatArchive(root, KeepMedia);

            archive.UseAccount(me);

            return archive;

        }

        private static ChatMessage Incoming(JID Chat, String Id, String Body, DateTimeOffset When)

            => new (Id,
                    Chat,
                    MessageDirection.Incoming,
                    $"{Chat}/phone",
                    Body,
                    When);

        private static ChatMessage Outgoing(JID Chat, String Id, String Body, DateTimeOffset When)

            => new (Id,
                    Chat,
                    MessageDirection.Outgoing,
                    "me",
                    Body,
                    When);

        #endregion


        #region AMessage_LandsInTheDirectoryOfItsChatAndTheFileOfItsMonth()

        [Test]
        public async Task AMessage_LandsInTheDirectoryOfItsChatAndTheFileOfItsMonth()
        {

            await using (var archive = Open())
            {
                archive.Record(Incoming(alice, "m1", "Hallo 👋", september));
                archive.Record(Incoming(alice, "m2", "Und im Oktober?", october));
                archive.Record(Incoming(bob,   "m3", "Hi",             september));
                await archive.FlushAsync();
            }

            var aliceSeptember  = Path.Combine(root, "me@example.org", "alice@example.org", "alice@example.org_202609.jsonl");
            var aliceOctober    = Path.Combine(root, "me@example.org", "alice@example.org", "alice@example.org_202610.jsonl");
            var bobSeptember    = Path.Combine(root, "me@example.org", "bob@example.org",   "bob@example.org_202609.jsonl");

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(aliceSeptember),  Is.True,  "one file per month");
                Assert.That(File.Exists(aliceOctober),    Is.True);
                Assert.That(File.Exists(bobSeptember),    Is.True,  "one directory per conversation");
            });

            var lines = File.ReadAllLines(aliceSeptember);

            Assert.That(lines, Has.Length.EqualTo(1), "one line per message");

            var json = JObject.Parse(lines[0]);

            Assert.Multiple(() =>
            {
                Assert.That(json.Value<String>("id"),         Is.EqualTo("m1"));
                Assert.That(json.Value<String>("chat"),       Is.EqualTo("alice@example.org"));
                Assert.That(json.Value<String>("body"),       Is.EqualTo("Hallo 👋"), "Unicode survives the round trip");
                Assert.That(json.Value<String>("direction"),  Is.EqualTo("in"));
            });

        }

        #endregion

        #region AMessageWithLineBreaks_StaysOneLine()

        /// <summary>
        /// The whole point of one object per line: a message that contains line
        /// breaks may not become several lines, or nothing can read the file
        /// back without a parser of its own.
        /// </summary>
        [Test]
        public async Task AMessageWithLineBreaks_StaysOneLine()
        {

            await using (var archive = Open())
            {
                archive.Record(Incoming(alice, "m1", "one\ntwo\r\nthree", september));
                await archive.FlushAsync();
            }

            var path = Path.Combine(root, "me@example.org", "alice@example.org", "alice@example.org_202609.jsonl");

            Assert.That(File.ReadAllLines(path), Has.Length.EqualTo(1));

            await using var reading = Open();

            Assert.That(reading.Load(alice, september.AddDays(-1))[0].Body, Is.EqualTo("one\ntwo\r\nthree"));

        }

        #endregion

        #region WhatWasWritten_ComesBackAsItWent()

        [Test]
        public async Task WhatWasWritten_ComesBackAsItWent()
        {

            var sent = Outgoing(alice, "m1", "Hallo", september) with {
                           Delivered = true,
                           Media     = new MediaRef("20260912T120000Z_photo.jpg", "image/jpeg", 4711, "https://upload.example.org/x/photo.jpg")
                       };

            await using (var archive = Open())
            {
                archive.Record(sent);
                await archive.FlushAsync();
            }

            await using var reading  = Open();
            var             messages = reading.Load(alice, september.AddDays(-1));

            Assert.That(messages, Has.Count.EqualTo(1));

            Assert.Multiple(() =>
            {
                Assert.That(messages[0].Id,                  Is.EqualTo("m1"));
                Assert.That(messages[0].Chat,                Is.EqualTo(alice));
                Assert.That(messages[0].Direction,           Is.EqualTo(MessageDirection.Outgoing));
                Assert.That(messages[0].Body,                Is.EqualTo("Hallo"));
                Assert.That(messages[0].Timestamp,           Is.EqualTo(september));
                Assert.That(messages[0].Delivered,           Is.True);
                Assert.That(messages[0].Media?.Name,         Is.EqualTo("20260912T120000Z_photo.jpg"));
                Assert.That(messages[0].Media?.ContentType,  Is.EqualTo("image/jpeg"));
                Assert.That(messages[0].Media?.Size,         Is.EqualTo(4711));
            });

        }

        #endregion

        #region ALaterLineForTheSameMessage_Wins()

        /// <summary>
        /// A correction and a receipt are appended, not written over. What
        /// comes back is the last word about the message, in the place the
        /// first word had.
        /// </summary>
        [Test]
        public async Task ALaterLineForTheSameMessage_Wins()
        {

            await using (var archive = Open())
            {

                archive.Record(Incoming(alice, "m1", "Teh message",  september));
                archive.Record(Incoming(alice, "m2", "Second",       september.AddMinutes(1)));
                archive.Record(Incoming(alice, "m1", "The message",  september) with { Corrected = true });

                await archive.FlushAsync();

            }

            var path = Path.Combine(root, "me@example.org", "alice@example.org", "alice@example.org_202609.jsonl");

            Assert.That(File.ReadAllLines(path), Has.Length.EqualTo(3), "nothing was overwritten");

            await using var reading  = Open();
            var             messages = reading.Load(alice, september.AddDays(-1));

            Assert.Multiple(() =>
            {
                Assert.That(messages,             Has.Count.EqualTo(2), "three lines, two messages");
                Assert.That(messages[0].Id,       Is.EqualTo("m1"));
                Assert.That(messages[0].Body,     Is.EqualTo("The message"));
                Assert.That(messages[0].Corrected, Is.True);
                Assert.That(messages[1].Id,       Is.EqualTo("m2"));
            });

        }

        #endregion

        #region TheSameLineTwice_IsWrittenOnce()

        [Test]
        public async Task TheSameLineTwice_IsWrittenOnce()
        {

            var message = Incoming(alice, "m1", "Hallo", september);

            await using (var archive = Open())
            {

                archive.Record(message);
                archive.Record(message);
                await archive.FlushAsync();

                archive.Record(message with { Delivered = true });
                await archive.FlushAsync();

            }

            var path = Path.Combine(root, "me@example.org", "alice@example.org", "alice@example.org_202609.jsonl");

            Assert.That(File.ReadAllLines(path), Has.Length.EqualTo(2), "the repetition was dropped, the change was not");

        }

        #endregion

        #region OnlyWhatIsInsideTheWindow_IsLoaded()

        [Test]
        public async Task OnlyWhatIsInsideTheWindow_IsLoaded()
        {

            await using (var archive = Open())
            {
                archive.Record(Incoming(alice, "old", "Im September",  september));
                archive.Record(Incoming(alice, "new", "Im Oktober",    october));
                await archive.FlushAsync();
            }

            await using var reading = Open();

            Assert.Multiple(() =>
            {
                Assert.That(reading.Load(alice, october.AddDays(-1)).Select(message => message.Id),  Is.EqualTo(new[] { "new" }));
                Assert.That(reading.Load(alice, september.AddDays(-1)).Select(message => message.Id), Is.EqualTo(new[] { "old", "new" }));
                Assert.That(reading.Load(alice, october.AddDays(1)),                                  Is.Empty);
            });

        }

        #endregion

        #region TooManyForTheWindow_KeepsTheNewest()

        [Test]
        public async Task TooManyForTheWindow_KeepsTheNewest()
        {

            await using (var archive = Open())
            {

                for (var i = 0; i < 20; i++)
                    archive.Record(Incoming(alice, $"m{i:D2}", $"Nummer {i}", september.AddMinutes(i)));

                await archive.FlushAsync();

            }

            await using var reading  = Open();
            var             messages = reading.Load(alice, september.AddDays(-1), MaxMessages: 5);

            Assert.Multiple(() =>
            {
                Assert.That(messages,          Has.Count.EqualTo(5));
                Assert.That(messages[0].Id,    Is.EqualTo("m15"));
                Assert.That(messages[^1].Id,   Is.EqualTo("m19"), "the newest are the ones that matter");
            });

        }

        #endregion

        #region LoadBefore_PagesBackwardsAndSaysWhetherThereIsMore()

        [Test]
        public async Task LoadBefore_PagesBackwardsAndSaysWhetherThereIsMore()
        {

            await using (var archive = Open())
            {

                // Five in September, five in October.
                for (var i = 0; i < 5; i++)
                {
                    archive.Record(Incoming(alice, $"s{i}", $"September {i}", september.AddMinutes(i)));
                    archive.Record(Incoming(alice, $"o{i}", $"Oktober {i}",   october.  AddMinutes(i)));
                }

                await archive.FlushAsync();

            }

            await using var reading = Open();

            var (newest, moreAfterNewest) = reading.LoadBefore(alice, october.AddDays(1), Limit: 4);

            Assert.Multiple(() =>
            {
                Assert.That(newest.Select(message => message.Id),  Is.EqualTo(new[] { "o1", "o2", "o3", "o4" }));
                Assert.That(moreAfterNewest,                        Is.True);
            });

            // The next scroll up: everything before the oldest one on screen,
            // which crosses from October back into September.
            var (older, moreAfterOlder) = reading.LoadBefore(alice, newest[0].Timestamp, Limit: 4);

            Assert.Multiple(() =>
            {
                Assert.That(older.Select(message => message.Id),  Is.EqualTo(new[] { "s2", "s3", "s4", "o0" }));
                Assert.That(moreAfterOlder,                        Is.True, "s0 and s1 are still there");
            });

            // And back to the beginning: nothing is older than the first thing
            // ever said.
            var (first, moreBeforeFirst) = reading.LoadBefore(alice, september, Limit: 10);

            Assert.Multiple(() =>
            {
                Assert.That(first,             Is.Empty);
                Assert.That(moreBeforeFirst,   Is.False);
            });

        }

        #endregion

        #region HasOlderThan_IsWhatTellsABrowserWhetherScrollingUpLeadsAnywhere()

        [Test]
        public async Task HasOlderThan_IsWhatTellsABrowserWhetherScrollingUpLeadsAnywhere()
        {

            await using (var archive = Open())
            {
                archive.Record(Incoming(alice, "m1", "Erste", september));
                archive.Record(Incoming(alice, "m2", "Zweite", october));
                await archive.FlushAsync();
            }

            await using var reading = Open();

            Assert.Multiple(() =>
            {
                Assert.That(reading.HasOlderThan(alice, october),           Is.True,  "September is older than October");
                Assert.That(reading.HasOlderThan(alice, september),         Is.False, "nothing precedes the first message");
                Assert.That(reading.HasOlderThan(bob,   october),           Is.False, "a conversation with no archive has no past");
            });

        }

        #endregion

        #region Conversations_AreTheJIDsTheFilesName()

        [Test]
        public async Task Conversations_AreTheJIDsTheFilesName()
        {

            await using (var archive = Open())
            {
                archive.Record(Incoming(alice, "m1", "Hallo", september));
                archive.Record(Incoming(bob,   "m2", "Hallo", september));
                await archive.FlushAsync();
            }

            await using var reading = Open();

            Assert.That(reading.Conversations().OrderBy(jid => jid.ToString()),
                        Is.EqualTo(new[] { alice, bob }));

        }

        #endregion

        #region WithoutAnAccount_NothingIsWritten()

        /// <summary>
        /// Everything is filed under the account, so before there is one there
        /// is nowhere to put it. That is not a loss: without an account there
        /// is no connection either, and therefore no message.
        /// </summary>
        [Test]
        public async Task WithoutAnAccount_NothingIsWritten()
        {

            await using (var archive = new ChatArchive(root, KeepMedia: false))
            {
                archive.Record(Incoming(alice, "m1", "Hallo", september));
                await archive.FlushAsync(TimeSpan.FromMilliseconds(200));
            }

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(root),                      Is.True, "the root is made at once, so that a wrong path is noticed at the start");
                Assert.That(Directory.EnumerateFileSystemEntries(root),  Is.Empty);
            });

        }

        #endregion

        #region AnAccountChange_ChangesTheArchive()

        [Test]
        public async Task AnAccountChange_ChangesTheArchive()
        {

            var other = JID.Parse("other@example.org");

            await using (var archive = Open())
            {

                archive.Record(Incoming(alice, "m1", "Für mich", september));
                await archive.FlushAsync();

                archive.UseAccount(other);

                archive.Record(Incoming(alice, "m2", "Für den anderen", september));
                await archive.FlushAsync();

            }

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(root, "me@example.org",    "alice@example.org", "alice@example.org_202609.jsonl")), Is.True);
                Assert.That(File.Exists(Path.Combine(root, "other@example.org", "alice@example.org", "alice@example.org_202609.jsonl")), Is.True);
            });

            await using var reading = Open();

            Assert.That(reading.Load(alice, september.AddDays(-1)).Select(message => message.Id),
                        Is.EqualTo(new[] { "m1" }),
                        "what was said to one account is not shown under the other");

        }

        #endregion

        #region ADamagedLine_DoesNotCostTheRest()

        /// <summary>
        /// What a process killed mid-write leaves behind. The months around it
        /// are still worth reading, and so is the rest of the month.
        /// </summary>
        [Test]
        public async Task ADamagedLine_DoesNotCostTheRest()
        {

            await using (var archive = Open())
            {
                archive.Record(Incoming(alice, "m1", "Erste",  september));
                archive.Record(Incoming(alice, "m2", "Zweite", september.AddMinutes(1)));
                await archive.FlushAsync();
            }

            var path = Path.Combine(root, "me@example.org", "alice@example.org", "alice@example.org_202609.jsonl");

            File.AppendAllText(path, "{\"id\":\"m3\",\"body\":\"halb geschr");

            await using var reading = Open();

            Assert.That(reading.Load(alice, september.AddDays(-1)).Select(message => message.Id),
                        Is.EqualTo(new[] { "m1", "m2" }));

        }

        #endregion

        #region TryGetMedia_RefusesANameThatIsAPath()

        [Test]
        public async Task TryGetMedia_RefusesANameThatIsAPath()
        {

            await using var archive = Open();

            var directory = Path.Combine(root, "me@example.org", "alice@example.org", "media");

            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "20260912T120000Z_photo.jpg"), [0x01, 0x02]);

            // Something worth reaching for, one directory up.
            File.WriteAllText(Path.Combine(root, "me@example.org", "secret.txt"), "not for you");

            Assert.Multiple(() =>
            {

                Assert.That(archive.TryGetMedia(alice, "20260912T120000Z_photo.jpg", out var path, out var type), Is.True);
                Assert.That(path,  Does.EndWith("20260912T120000Z_photo.jpg"));
                Assert.That(type,  Is.EqualTo("image/jpeg"));

                Assert.That(archive.TryGetMedia(alice, "../secret.txt",              out _, out _), Is.False);
                Assert.That(archive.TryGetMedia(alice, "..\\secret.txt",             out _, out _), Is.False);
                Assert.That(archive.TryGetMedia(alice, "media/../../secret.txt",     out _, out _), Is.False);
                Assert.That(archive.TryGetMedia(alice, "C:\\Windows\\win.ini",       out _, out _), Is.False);
                Assert.That(archive.TryGetMedia(alice, "",                           out _, out _), Is.False);
                Assert.That(archive.TryGetMedia(alice, null,                         out _, out _), Is.False);
                Assert.That(archive.TryGetMedia(alice, "nothing-of-the-sort.jpg",    out _, out _), Is.False);

            });

        }

        #endregion

        #region AStoredFileOfAnUnknownKind_IsNotOpenedByTheBrowser()

        /// <summary>
        /// The one thing that must not happen to a file served from this
        /// program's own origin: being handed to the browser as a document.
        /// </summary>
        [Test]
        public async Task AStoredFileOfAnUnknownKind_IsNotOpenedByTheBrowser()
        {

            await using var archive = Open();

            var directory = Path.Combine(root, "me@example.org", "alice@example.org", "media");

            Directory.CreateDirectory(directory);

            foreach (var name in new[] { "20260912T120000Z_x.svg", "20260912T120000Z_x.html", "20260912T120000Z_x.ogg" })
                File.WriteAllText(Path.Combine(directory, name), "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");

            Assert.Multiple(() =>
            {

                Assert.That(archive.TryGetMedia(alice, "20260912T120000Z_x.svg",  out _, out var svg),  Is.True);
                Assert.That(svg,  Is.Null, "an SVG is a document with scripts in it - it is a download, not a picture");

                Assert.That(archive.TryGetMedia(alice, "20260912T120000Z_x.html", out _, out var html), Is.True);
                Assert.That(html, Is.Null);

                Assert.That(archive.TryGetMedia(alice, "20260912T120000Z_x.ogg",  out _, out var ogg),  Is.True);
                Assert.That(ogg,  Is.Null, ".ogg says neither audio nor video, so it says nothing");

            });

        }

        #endregion

    }

}
