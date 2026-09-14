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

        #region (private) OnlyOnUnix()

        private static void OnlyOnUnix()
        {
            if (OperatingSystem.IsWindows())
                Assert.Ignore("Unix file modes. On Windows the directory decides, not the file - see PrivatePathsTests.");
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

            OnlyOnUnix();

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

            OnlyOnUnix();

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

            OnlyOnUnix();

            var path = Path.Combine(root, "me@example.org", "alice@example.org", "media");

            OwnerOnlyFile.CreateDirectory(path);

            Assert.Multiple(() =>
            {
                Assert.That(File.GetUnixFileMode(path),                               Is.EqualTo(OwnerOnlyDirectoryMode), "0700");
                Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(path)!),       Is.EqualTo(OwnerOnlyDirectoryMode), "and the parents it had to make on the way");
            });

        }

        #endregion

        #region OnUnix_WrittenBytes_AreOwnerOnly()

        [Test]
        public async Task OnUnix_WrittenBytes_AreOwnerOnly()
        {

            OnlyOnUnix();

            var path = Path.Combine(root, "20260914T120000Z_photo.jpg");

            await OwnerOnlyFile.WriteAllBytesAsync(path, [1, 2, 3]);

            Assert.Multiple(() =>
            {
                Assert.That(File.GetUnixFileMode(path),  Is.EqualTo(OwnerOnlyFileMode), "0600");
                Assert.That(File.ReadAllBytes(path),     Is.EqualTo(new Byte[] { 1, 2, 3 }));
            });

        }

        #endregion

    }

}
