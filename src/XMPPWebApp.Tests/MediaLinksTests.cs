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
    /// There are two signals and they are not equal. XEP-0066 is the sender
    /// saying "this message is about a file", which a client writes when it
    /// shares one and a person pasting a link never does; the extension in the
    /// address is what is left to go by when nobody said. The tests below hold
    /// both, and hold that neither overrides what the body says.
    ///
    /// Nothing here reaches the network. Detect is a pure function of the body
    /// and the declaration, which is what makes it worth testing this closely.
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


        #region ADeclaredFile_IsFetchedWhateverItIsCalled()

        /// <summary>
        /// XEP-0066: a file the sender declared is a file, even when its
        /// address names nothing.
        /// </summary>
        /// <remarks>
        /// <b>The one case the extension cannot decide.</b> An upload service
        /// is free to hand back an address that is an identifier and nothing
        /// else - XEP-0363 asks it to put the file somewhere and say where,
        /// not to keep the name. Conversations, Dino and Gajim all upload to
        /// services that keep it, which is why the extension carries as far as
        /// it does; a service that does not would have left every file it
        /// serves unfetched.
        ///
        /// The second half of this test is what makes the first half about
        /// anything: the same body without the declaration is not fetched, so
        /// what is being measured is the declaration and not the address.
        /// </remarks>
        [Test]
        public void ADeclaredFile_IsFetchedWhateverItIsCalled()
        {

            const String nameless = "https://upload.example.org/a1b2/8f3c91d7e4";

            Assert.Multiple(() => {

                Assert.That(MediaLinks.Detect(nameless, new Uri(nameless))?.AbsoluteUri,
                            Is.EqualTo(nameless),
                            "the sender said this is a file, which is better than this side guessing");

                Assert.That(MediaLinks.Detect(nameless),
                            Is.Null,
                            "and without the declaration the same address is just an address");

            });

        }

        #endregion

        #region ADeclarationDoesNotOverrideTheBody()

        /// <summary>
        /// The declaration decides whether an address is a file, and nothing
        /// else about it.
        /// </summary>
        /// <remarks>
        /// <b>Checked here although Ratatoskr checks it too.</b> The library
        /// hands over the declaration only when it names what the body names,
        /// because a message that shows one address and leads to another is the
        /// oldest trick there is. This side asks again, because this side is
        /// where the consequence lies: the request that leaves this machine
        /// goes to the address in it. Two checks of one rule, in the two places
        /// that each need it.
        ///
        /// A sentence stays a sentence too. XEP-0363, section 5 has the body be
        /// the address, so a declaration beside a sentence is not something the
        /// library would pass on either - and a rule that holds in one place
        /// only is a rule that stops holding when the other place changes.
        /// </remarks>
        [Test]
        public void ADeclarationDoesNotOverrideTheBody()
        {

            Assert.Multiple(() => {

                Assert.That(MediaLinks.Detect("https://upload.example.org/a1b2/8f3c91d7e4",
                                              new Uri("https://elsewhere.example/payload")),
                            Is.Null,
                            "a declaration pointing somewhere other than the body was followed");

                Assert.That(MediaLinks.Detect("have a look at https://upload.example.org/a1b2/8f3c91d7e4",
                                              new Uri("https://upload.example.org/a1b2/8f3c91d7e4")),
                            Is.Null,
                            "a declaration made a link inside a sentence into a handed-over file");

            });

        }

        #endregion

        #region ADeclaredPlainHttpUrl_IsStillNotFetched()

        /// <summary>
        /// The https rule is not the sender's to waive.
        /// </summary>
        /// <remarks>
        /// It is not a question about what the message means - which is all the
        /// declaration can answer - but about what this side is willing to do:
        /// fetch a file over a connection anybody on the way can read, and then
        /// keep it beside a conversation as though it had arrived privately.
        /// </remarks>
        [Test]
        public void ADeclaredPlainHttpUrl_IsStillNotFetched()
        {

            const String insecure = "http://upload.example.org/a1b2/IMG_1234.jpg";

            Assert.That(MediaLinks.Detect(insecure, new Uri(insecure)),
                        Is.Null,
                        "a declared file was fetched over plain http");

        }

        #endregion

    }

}
