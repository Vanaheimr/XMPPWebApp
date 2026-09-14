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

using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// That what this program writes is readable by its owner and nobody else.
    /// </summary>
    /// <remarks>
    /// Most of this fixture only means something on Unix, and that is where it
    /// matters: the servers this runs on are Linux, and a mode is the whole of
    /// the answer there. On Windows the modes are ignored by the operating
    /// system and the question is answered by the directory instead, which is
    /// what <see cref="PrivatePathsTests"/> is about - so these are skipped
    /// there with a reason rather than asserted into something they do not
    /// mean. The Debian leg of CI is where they run.
    ///
    /// The append is checked on both, because that one is not about permissions
    /// at all: every line of every conversation goes through it, and it used to
    /// be File.AppendAllText.
    /// </remarks>
    [TestFixture]
    public class OwnerOnlyFileTests
    {

        #region Data

        private String root = "";

        private const UnixFileMode OwnerOnlyFileMode       = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        private const UnixFileMode OwnerOnlyDirectoryMode  = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        #endregion

        #region Setup / Teardown

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-owner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (Exception)
            { }
        }

        #endregion

        #region AnAppend_AddsRatherThanReplaces()

        /// <summary>
        /// Every line of every conversation goes through this. It used to be
        /// File.AppendAllText and is now a stream opened with a mode, and the
        /// one thing that must not have changed is the appending.
        /// </summary>
        [Test]
        public void AnAppend_AddsRatherThanReplaces()
        {

            var path = Path.Combine(root, "month.jsonl");

            OwnerOnlyFile.Append(path, "{\"a\":1}\n");
            OwnerOnlyFile.Append(path, "{\"b\":2}\n");
            OwnerOnlyFile.Append(path, "{\"c\":\"äöü 🙂\"}\n");

            var lines = File.ReadAllLines(path);

            Assert.Multiple(() =>
            {
                Assert.That(lines,     Has.Length.EqualTo(3));
                Assert.That(lines[0],  Is.EqualTo("{\"a\":1}"));
                Assert.That(lines[2],  Is.EqualTo("{\"c\":\"äöü 🙂\"}"), "and UTF-8 survived the change of writer");
            });

        }

        #endregion

        #region OnUnix_AWrittenFile_IsOwnerOnly()

        [Test]
        public void OnUnix_AWrittenFile_IsOwnerOnly()
        {

            // The guard is written out in every test rather than called from a
            // helper, because CA1416 has to see it: an analyser cannot know
            // that Assert.Ignore never returns, and File.GetUnixFileMode is
            // unsupported on Windows.
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix file modes. On Windows the directory decides, not the file - see PrivatePathsTests.");
                return;
            }

            var path = Path.Combine(root, "xmpp-account.json");

            OwnerOnlyFile.Write(path, "{}");

            Assert.That(File.GetUnixFileMode(path), Is.EqualTo(OwnerOnlyFileMode), "0600");

        }

        #endregion

        #region OnUnix_AnAppendedFile_IsOwnerOnly()

        /// <summary>
        /// The archive, which is the part that was not going through here at
        /// all: the credentials were 0600 on a server while the conversations
        /// beside them took whatever the umask happened to be.
        /// </summary>
        [Test]
        public void OnUnix_AnAppendedFile_IsOwnerOnly()
        {

            // The guard is written out in every test rather than called from a
            // helper, because CA1416 has to see it: an analyser cannot know
            // that Assert.Ignore never returns, and File.GetUnixFileMode is
            // unsupported on Windows.
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix file modes. On Windows the directory decides, not the file - see PrivatePathsTests.");
                return;
            }

            var path = Path.Combine(root, "alice@example.org_2026-09.jsonl");

            OwnerOnlyFile.Append(path, "{\"a\":1}\n");
            OwnerOnlyFile.Append(path, "{\"b\":2}\n");

            Assert.That(File.GetUnixFileMode(path), Is.EqualTo(OwnerOnlyFileMode), "0600, and the second append did not widen it");

        }

        #endregion

        #region OnUnix_ADirectory_IsOwnerOnly()

        [Test]
        public void OnUnix_ADirectory_IsOwnerOnly()
        {

            // The guard is written out in every test rather than called from a
            // helper, because CA1416 has to see it: an analyser cannot know
            // that Assert.Ignore never returns, and File.GetUnixFileMode is
            // unsupported on Windows.
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix file modes. On Windows the directory decides, not the file - see PrivatePathsTests.");
                return;
            }

            // Three segments at once, which is what the archive does:
            // root/account/conversation/media.
            var media         = Path.Combine(root, "me@example.org", "alice@example.org", "media");
            var conversation  = Path.GetDirectoryName(media)!;
            var account       = Path.GetDirectoryName(conversation)!;

            OwnerOnlyFile.CreateDirectory(media);

            // Read out here and not inside the Assert.Multiple below: a lambda
            // is analysed on its own, so the platform guard above does not
            // reach into it and CA1416 would have something to say.
            var modes = new[] {
                            File.GetUnixFileMode(media),
                            File.GetUnixFileMode(conversation),
                            File.GetUnixFileMode(account)
                        };

            Assert.Multiple(() =>
            {
                Assert.That(modes[0],  Is.EqualTo(OwnerOnlyDirectoryMode), "media, the one the call names");
                Assert.That(modes[1],  Is.EqualTo(OwnerOnlyDirectoryMode), "the conversation it had to make on the way");
                Assert.That(modes[2],  Is.EqualTo(OwnerOnlyDirectoryMode), "and the account above it - 0755 here would list every JID this account talks to");
            });

        }

        #endregion

        #region OnUnix_WrittenBytes_AreOwnerOnly()

        [Test]
        public async Task OnUnix_WrittenBytes_AreOwnerOnly()
        {

            // The guard is written out in every test rather than called from a
            // helper, because CA1416 has to see it: an analyser cannot know
            // that Assert.Ignore never returns, and File.GetUnixFileMode is
            // unsupported on Windows.
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix file modes. On Windows the directory decides, not the file - see PrivatePathsTests.");
                return;
            }

            var path = Path.Combine(root, "20260914T120000Z_photo.jpg");

            await OwnerOnlyFile.WriteAllBytesAsync(path, [1, 2, 3]);

            var mode = File.GetUnixFileMode(path);

            Assert.Multiple(() =>
            {
                Assert.That(mode,                     Is.EqualTo(OwnerOnlyFileMode), "0600");
                Assert.That(File.ReadAllBytes(path),  Is.EqualTo(new Byte[] { 1, 2, 3 }));
            });

        }

        #endregion

    }

}
