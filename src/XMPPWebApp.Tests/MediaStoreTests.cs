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

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// How much of this disk somebody else's files may take up.
    /// </summary>
    /// <remarks>
    /// MaxBytes bounds one file and bounds nothing else - how many files there
    /// are is decided by whoever is sending them. What is tested here is the
    /// second number, the one that makes "a peer may write to this disk" stop
    /// short of "a peer may fill this disk".
    ///
    /// No test here reaches the network, and the addresses are loopback ones so
    /// that they could not even if the order of the checks were wrong: what
    /// tells the two apart is which sentence comes back. A refusal that names
    /// the quota proves the quota was asked first; a refusal that names the
    /// address proves it was not asked at all.
    /// </remarks>
    [TestFixture]
    public class MediaStoreTests
    {

        #region Data

        private static readonly JID  Me     = JID.Parse("me@example.org");
        private static readonly JID  Alice  = JID.Parse("alice@example.org");

        private String root = "";

        #endregion

        #region Setup / Teardown

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-media-" + Guid.NewGuid().ToString("N"));
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

        #region (private) Fill(Bytes)

        /// <summary>
        /// Put a file of the given size where a fetched one would lie.
        /// </summary>
        private void Fill(Int32 Bytes)
        {

            var directory = ChatArchivePaths.MediaDirectory(root, Me.ToString(), Alice.ToString());

            Directory.CreateDirectory(directory);

            File.WriteAllBytes(Path.Combine(directory, "20260914T120000Z_old.png"), new Byte[Bytes]);

        }

        #endregion


        #region AFullStore_FetchesNothingFurther()

        [Test]
        public async Task AFullStore_FetchesNothingFurther()
        {

            Fill(2000);

            using var store = new MediaStore(root, TotalBytesAllowed: 1000);

            var (media, problem) = await store.FetchAsync(Me, Alice,
                                                          new Uri("https://127.0.0.1/x.png"),
                                                          DateTimeOffset.UtcNow);

            Assert.Multiple(() =>
            {
                Assert.That(media,    Is.Null);
                Assert.That(problem,  Does.Contain("full"), "and it is the quota that says so, not the address check behind it");
            });

        }

        #endregion

        #region AStoreWithRoom_GetsPastTheQuota()

        /// <summary>
        /// The other half, or a quota that refuses everything would pass the
        /// test above without being a quota at all. Past the line this one
        /// reaches the address check, which refuses a loopback address - so the
        /// sentence changes, and that change is the evidence.
        /// </summary>
        [Test]
        public async Task AStoreWithRoom_GetsPastTheQuota()
        {

            Fill(200);

            using var store = new MediaStore(root, TotalBytesAllowed: 100_000);

            var (media, problem) = await store.FetchAsync(Me, Alice,
                                                          new Uri("https://127.0.0.1/x.png"),
                                                          DateTimeOffset.UtcNow);

            Assert.Multiple(() =>
            {
                Assert.That(media,    Is.Null,                   "a loopback address is still refused");
                Assert.That(problem,  Does.Not.Contain("full"),  "but not for being out of room");
            });

        }

        #endregion

        #region TheConversationsThemselves_DoNotCountAgainstTheQuota()

        /// <summary>
        /// What the quota is for is the part somebody else decides the size of.
        /// The monthly files are lines this program writes, and an archive that
        /// stopped fetching because the conversations grew would have the wrong
        /// thing wrong.
        /// </summary>
        [Test]
        public async Task TheConversationsThemselves_DoNotCountAgainstTheQuota()
        {

            var conversation = ChatArchivePaths.ConversationDirectory(root, Me.ToString(), Alice.ToString());

            Directory.CreateDirectory(conversation);

            File.WriteAllBytes(Path.Combine(conversation, "alice@example.org_2026-09.jsonl"), new Byte[50_000]);

            using var store = new MediaStore(root, TotalBytesAllowed: 1000);

            var (_, problem) = await store.FetchAsync(Me, Alice,
                                                      new Uri("https://127.0.0.1/x.png"),
                                                      DateTimeOffset.UtcNow);

            Assert.That(problem, Does.Not.Contain("full"),
                        "fifty kilobytes of conversation against a thousand bytes of quota, and still room for files");

        }

        #endregion

        #region WhatWasStored_CountsFromThenOn()

        /// <summary>
        /// The walk happens once; after that the store keeps its own tally. A
        /// tally that did not go up would let the same full store fetch for
        /// ever.
        /// </summary>
        [Test]
        public async Task WhatWasStored_CountsFromThenOn()
        {

            using var store = new MediaStore(root, TotalBytesAllowed: 1000);

            var (_, first) = await store.FetchAsync(Me, Alice,
                                                    new Uri("https://127.0.0.1/x.png"),
                                                    DateTimeOffset.UtcNow);

            Assert.That(first, Does.Not.Contain("full"), "an empty store has room");

            // Nothing was stored - the address was refused - so the tally has
            // not moved and the answer must not have either.
            var (_, second) = await store.FetchAsync(Me, Alice,
                                                     new Uri("https://127.0.0.1/y.png"),
                                                     DateTimeOffset.UtcNow);

            Assert.That(second, Does.Not.Contain("full"), "and it still has room, because nothing was written");

        }

        #endregion

    }

}
