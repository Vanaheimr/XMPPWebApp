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

using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// Which link in a message is a handed-over file, and which is merely a
    /// link.
    /// </summary>
    /// <remarks>
    /// <b>This decision is the only thing between a message and an outgoing
    /// request to a stranger's server</b>, and it had no test at all. What it
    /// decides is not whether a file is kept - that is decided afterwards, from
    /// what the server answers - but whether this side asks. A request is not
    /// undone by throwing the answer away: it has already told the host that
    /// the message arrived, and from which address.
    ///
    /// Nothing here reaches the network. Detect is a pure function of the body,
    /// which is what makes it worth testing this closely.
    /// </remarks>
    [TestFixture]
    public class MediaLinksTests
    {

        #region APastedLink_IsNotFetched()

        /// <summary>
        /// A link somebody pasted is not a handed-over file, however alone it
        /// stands in its line.
        /// </summary>
        /// <remarks>
        /// Both addresses are out of a real log: both were fetched in full,
        /// found to be text/html and thrown away. People paste links on a line
        /// of their own - that is the normal way to paste one - so "the body is
        /// nothing but a URL" does not tell a file from a link, and was the
        /// whole of the rule.
        /// </remarks>
        [Test]
        public void APastedLink_IsNotFetched()
        {

            Assert.Multiple(() => {

                Assert.That(MediaLinks.Detect("https://www.youtube.com/shorts/ws8Or027EWs"),
                            Is.Null,
                            "a video link is not a file somebody handed over");

                Assert.That(MediaLinks.Detect("https://www.klett-cotta.de/produkt/carla-hinrichs-meine-verletzte-generation-9783608505252-t-9377"),
                            Is.Null,
                            "a product page is not a file somebody handed over");

                Assert.That(MediaLinks.Detect("https://example.org/"),
                            Is.Null,
                            "a bare host names no file");

                Assert.That(MediaLinks.Detect("https://example.org/download.php?id=7"),
                            Is.Null,
                            "a script is not a kind of file this archive keeps");

            });

        }

        #endregion

        #region AHandedOverFile_IsFetched()

        /// <summary>
        /// An upload per XEP-0363 still is one. This is the case the rule
        /// exists for, and the one that must not be lost in narrowing it.
        /// </summary>
        /// <remarks>
        /// The URL of an upload ends in the name of the file, which is what
        /// Conversations, Dino and Gajim all send - and an expiring one hangs a
        /// query string on it, which is no part of the name.
        /// </remarks>
        [Test]
        public void AHandedOverFile_IsFetched()
        {

            Assert.Multiple(() => {

                Assert.That(MediaLinks.Detect("https://upload.example.org/a1b2/IMG_1234.jpg")?.AbsolutePath,
                            Is.EqualTo("/a1b2/IMG_1234.jpg"),
                            "a picture is handed over");

                Assert.That(MediaLinks.Detect("https://upload.example.org/a1b2/clip.mp4"),
                            Is.Not.Null,
                            "video is kept here too");

                Assert.That(MediaLinks.Detect("https://upload.example.org/a1b2/notes.pdf"),
                            Is.Not.Null,
                            "and a PDF");

                Assert.That(MediaLinks.Detect("https://upload.example.org/a1b2/IMG_1234.JPG"),
                            Is.Not.Null,
                            "the extension is a name, not a spelling");

                Assert.That(MediaLinks.Detect("https://upload.example.org/a1b2/IMG_1234.jpg?token=abc&expires=99"),
                            Is.Not.Null,
                            "an expiring upload carries a query string");

                Assert.That(MediaLinks.Detect("https://upload.example.org/a1b2/photo.jpeg"),
                            Is.Not.Null,
                            "the name people's files carry, not the one this program would store it under");

                Assert.That(MediaLinks.Detect("https://upload.example.org/a1b2/voice.ogg"),
                            Is.Not.Null,
                            "a voice message really arrives under this name");

            });

        }

        #endregion

        #region AnAesGcmUrl_IsFetchedWhereverItStands()

        /// <summary>
        /// An aesgcm:// URL is a shared file and nothing else, so it is taken
        /// even out of the middle of a sentence.
        /// </summary>
        /// <remarks>
        /// XEP-0454: the URL carries the key to decrypt the file. Nobody writes
        /// one in passing, and no browser can open one - fetching it is the
        /// only way it is ever seen. Its name has an extension as well, so this
        /// test says nothing about the gate; what it holds is that the scheme
        /// keeps its own rule.
        /// </remarks>
        [Test]
        public void AnAesGcmUrl_IsFetchedWhereverItStands()
        {

            Assert.That(MediaLinks.Detect("look at this aesgcm://upload.example.org/a1b2/IMG_1234.jpg#0123456789abcdef please"),
                        Is.Not.Null,
                        "an aesgcm URL is a file wherever it stands");

        }

        #endregion

        #region ALinkInASentence_IsNotFetched()

        /// <summary>
        /// Somebody pointing at something is not somebody handing it over -
        /// even when what they point at is a picture.
        /// </summary>
        [Test]
        public void ALinkInASentence_IsNotFetched()
        {

            Assert.Multiple(() => {

                Assert.That(MediaLinks.Detect("have a look at https://example.org/IMG_1234.jpg"),
                            Is.Null,
                            "a picture inside a sentence stays a link");

                Assert.That(MediaLinks.Detect("https://example.org/a.jpg https://example.org/b.jpg"),
                            Is.Null,
                            "a body with several URLs is a sentence");

            });

        }

        #endregion

        #region PlainHttp_IsNotFetched()

        /// <summary>
        /// Only https. The file would travel readable, and a shared file is
        /// nothing this side makes more public than the sender did.
        /// </summary>
        [Test]
        public void PlainHttp_IsNotFetched()
        {

            Assert.That(MediaLinks.Detect("http://upload.example.org/a1b2/IMG_1234.jpg"),
                        Is.Null,
                        "plain http is not fetched");

        }

        #endregion

    }

}
