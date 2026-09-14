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

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// Where the account, the login and the archive live when nobody said.
    /// </summary>
    /// <remarks>
    /// They used to default to the repository root, which on Windows is often
    /// not a private place at all - a checkout on a data drive inherits that
    /// drive's rights. The default moved to the per-user directory, and what is
    /// tested here is the part of that move which could quietly cost somebody
    /// their conversations: what happens to what is still lying in the old
    /// place.
    /// </remarks>
    [TestFixture]
    public class PrivatePathsTests
    {

        #region Data

        private String was = "";
        private String @is = "";

        #endregion

        #region Setup / Teardown

        [SetUp]
        public void SetUp()
        {

            var root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-paths-" + Guid.NewGuid().ToString("N"));

            was  = Path.Combine(root, "repository");
            @is  = Path.Combine(root, "private");

            Directory.CreateDirectory(was);
            Directory.CreateDirectory(@is);

        }

        [TearDown]
        public void TearDown()
        {
            try
            {

                var root = Path.GetDirectoryName(was);

                if (root is not null && Directory.Exists(root))
                    Directory.Delete(root, true);

            }
            catch (Exception)
            { }
        }

        #endregion


        #region NothingAnywhere_IsTheNewPlace()

        [Test]
        public void NothingAnywhere_IsTheNewPlace()
        {

            var moved = new List<String>();

            Assert.Multiple(() =>
            {
                Assert.That(PrivatePaths.For("xmpp-account.json", was, @is, moved),
                            Is.EqualTo(Path.Combine(@is, "xmpp-account.json")));
                Assert.That(moved, Is.Empty, "nothing to say when there is nothing to move");
            });

        }

        #endregion

        #region SomethingInTheOldPlace_IsUsedAndSaidSo()

        /// <summary>
        /// The one that matters. Starting fresh in silence would leave somebody
        /// looking for conversations that were on the disk the whole time.
        /// </summary>
        [Test]
        public void SomethingInTheOldPlace_IsUsedAndSaidSo()
        {

            File.WriteAllText(Path.Combine(was, "xmpp-account.json"), "{}");

            var moved = new List<String>();
            var path  = PrivatePaths.For("xmpp-account.json", was, @is, moved);

            Assert.Multiple(() =>
            {
                Assert.That(path,      Is.EqualTo(Path.Combine(was, "xmpp-account.json")), "the account is not lost");
                Assert.That(moved,     Has.Count.EqualTo(1));
                Assert.That(moved[0],  Does.Contain(@is), "and the sentence says where it should go");
            });

        }

        #endregion

        #region TheNewPlaceWins_OnceItIsThere()

        /// <summary>
        /// What makes this a move rather than a fork: after the file has been
        /// copied across, the old one is ignored and nothing is said about it
        /// any more.
        /// </summary>
        [Test]
        public void TheNewPlaceWins_OnceItIsThere()
        {

            File.WriteAllText(Path.Combine(was, "web-login.json"), "{}");
            File.WriteAllText(Path.Combine(@is, "web-login.json"), "{}");

            var moved = new List<String>();

            Assert.Multiple(() =>
            {
                Assert.That(PrivatePaths.For("web-login.json", was, @is, moved),
                            Is.EqualTo(Path.Combine(@is, "web-login.json")));
                Assert.That(moved, Is.Empty);
            });

        }

        #endregion

        #region ADirectoryCounts_LikeAFile()

        /// <summary>
        /// The archive is a directory, and it is the one whose silent
        /// disappearance would be felt most.
        /// </summary>
        [Test]
        public void ADirectoryCounts_LikeAFile()
        {

            Directory.CreateDirectory(Path.Combine(was, "chats"));

            var moved = new List<String>();

            Assert.Multiple(() =>
            {
                Assert.That(PrivatePaths.For("chats", was, @is, moved),
                            Is.EqualTo(Path.Combine(was, "chats")));
                Assert.That(moved, Has.Count.EqualTo(1));
            });

        }

        #endregion

    }

}
