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

using org.GraphDefined.Vanaheimr.Ratatoskr;

using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// What may become a picture at this program's own origin, and what may
    /// not.
    /// </summary>
    /// <remarks>
    /// Every one of these is about the same sentence: <b>an avatar is bytes
    /// from somebody in the roster, and the page they end up on is this one.</b>
    /// A contact chooses the bytes, the claimed type and how often they change -
    /// so what is checked here is that the store's answer to each of those is
    /// not "whatever they said".
    /// </remarks>
    [TestFixture]
    public class AvatarStoreTests
    {

        #region Data

        private String        root   = "";
        private AvatarStore?  store;

        private static readonly JID  Me     = JID.Parse("me@example.org");
        private static readonly JID  Alice  = JID.Parse("alice@example.org");
        private static readonly JID  Bob    = JID.Parse("bob@example.org");

        #endregion

        #region Setup / Teardown

        [SetUp]
        public void MakeAStore()
        {

            root   = Path.Combine(Path.GetTempPath(), "XMPPWebApp-avatars-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            store  = new AvatarStore(root);

            store.UseAccount(Me);

        }

        [TearDown]
        public void RemoveIt()
        {

            store = null;

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (Exception)
            { }

        }

        /// <summary>
        /// Bytes that begin like a PNG. Not a PNG - nothing here decodes one -
        /// but a file that says the same thing about itself that its type does,
        /// which is all the store asks.
        /// </summary>
        private static Byte[] APicture(String Content = "a picture")

            => [.. new Byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                .. Encoding.UTF8.GetBytes(Content)];

        private static AvatarInfo Describe(Byte[] Image, String Type = "image/png")

            => UserAvatar.Describe(Image, Type);

        #endregion


        #region APictureIsKeptUnderTheHashOfItsBytes()

        /// <summary>
        /// The happy path, and the shape everything else depends on.
        /// </summary>
        [Test]
        public async Task APictureIsKeptUnderTheHashOfItsBytes()
        {

            var image          = APicture();
            var info           = Describe(image);

            var (id, problem)  = await store!.StoreAsync(Alice, info, image);

            Assert.Multiple(() =>
            {

                Assert.That(id,      Is.EqualTo(UserAvatar.IdOf(image)), $"Not kept: {problem}");
                Assert.That(problem, Is.Null);

            });

            Assert.Multiple(() =>
            {

                Assert.That(store.IdOf(Alice), Is.EqualTo(id),      "The contact does not point at the picture.");
                Assert.That(store.Has(id!),    Is.True,             "The picture is not there.");

            });

            Assert.That(store.TryGet(id!, out var path, out var type), Is.True,
                        "What was stored cannot be read back.");

            Assert.Multiple(() =>
            {

                Assert.That(File.ReadAllBytes(path!), Is.EqualTo(image),
                            "The bytes on the disk are not the bytes that were stored.");

                Assert.That(type, Is.EqualTo("image/png"),
                            "The type it is served as is not the type it was stored as.");

            });

        }

        #endregion

        #region BytesThatAreNotTheOnesAnnouncedAreRefused()

        /// <summary>
        /// The id is the name everything downstream caches by.
        /// </summary>
        /// <remarks>
        /// <b>Which is why this is refused and not merely re-filed.</b> Bytes
        /// stored under an id they do not hash to are shown for every later
        /// avatar that really has that id - by this browser, for a year, since
        /// the route tells it the address is immutable - and go on being shown
        /// after the mistake has been corrected everywhere else.
        ///
        /// The library checks this too, in FetchAvatarAsync. It is checked here
        /// as well because this is the layer whose whole contract is "filed by
        /// the hash of its content".
        /// </remarks>
        [Test]
        public async Task BytesThatAreNotTheOnesAnnouncedAreRefused()
        {

            var announced      = Describe(APicture("what was announced"));
            var different      = APicture("what arrived");

            var (id, problem)  = await store!.StoreAsync(Alice, announced, different);

            Assert.Multiple(() =>
            {

                Assert.That(id,      Is.Null,                  "Bytes were filed under an id they do not hash to.");
                Assert.That(problem, Is.Not.Null.And.Not.Empty, "They were refused and nothing said why.");

                Assert.That(store.IdOf(Alice), Is.Null,
                            "The contact was pointed at a picture that was not kept.");

            });

        }

        #endregion

        #region ADocumentCalledAPictureIsRefused()

        /// <summary>
        /// The type is a claim by a contact, so it is checked against the bytes.
        /// </summary>
        /// <remarks>
        /// This is the check <see cref="MediaStore"/> has no equivalent for, and
        /// the difference is where the type comes from: there it is a
        /// Content-Type from an HTTP server, here it is a string somebody in the
        /// roster typed into a stanza. Saying "image/png" over an HTML document
        /// costs them nothing, and what they would get for it is a document at
        /// this program's own origin.
        ///
        /// nosniff on the route is what actually stops such a file from being
        /// run, and would be enough on any browser of the last decade. This is
        /// the second lock: a file that is not what it says it is is not kept at
        /// all.
        /// </remarks>
        [Test]
        public async Task ADocumentCalledAPictureIsRefused()
        {

            var document       = Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>");
            var info           = Describe(document);

            var (id, problem)  = await store!.StoreAsync(Alice, info, document);

            Assert.Multiple(() =>
            {

                Assert.That(id,      Is.Null,
                            "A document was kept as a picture, and would be served from this origin.");

                Assert.That(problem, Does.Contain("image/png"),
                            "It was refused and the reason does not name the type it claimed.");

            });

        }

        #endregion

        #region ASvgIsNotAnImageHere()

        /// <summary>
        /// An SVG is a document with scripts in it.
        /// </summary>
        /// <remarks>
        /// Opening one in a tab of this origin runs them with this session. It
        /// is refused by not being in the list at all, which is why this asks
        /// the list rather than storing one: there is no signature to check
        /// against for a type nothing will ever serve.
        /// </remarks>
        [Test]
        public void ASvgIsNotAnImageHere()
        {

            Assert.Multiple(() =>
            {

                Assert.That(AvatarStore.AllowedTypes.ContainsKey("image/svg+xml"), Is.False,
                            "image/svg+xml is servable from this origin - an SVG is a document with scripts in it.");

                Assert.That(AvatarStore.Acceptable(new AvatarInfo("0", 100, "image/svg+xml")), Is.False,
                            "An SVG avatar would be fetched.");

            });

        }

        #endregion

        #region APictureOfferedOnlyOverHttpIsNotFetched()

        /// <summary>
        /// XEP-0084 lets the metadata carry a url instead of the bytes.
        /// </summary>
        /// <remarks>
        /// Following it would mean a contact chooses an address this machine
        /// fetches - which is the whole of MediaStore's problem arriving through
        /// a door that has none of its locks. The library reads the url and
        /// never follows it; this is the client saying the same.
        /// </remarks>
        [Test]
        public void APictureOfferedOnlyOverHttpIsNotFetched()
        {

            var overHttp = new AvatarInfo("0123456789abcdef0123456789abcdef01234567",
                                          1024,
                                          "image/png",
                                          Url: new Uri("https://somewhere.example/face.png"));

            Assert.That(AvatarStore.Acceptable(overHttp), Is.False,
                        "An avatar offered at an address a contact chose would be fetched from there.");

        }

        #endregion

        #region APictureTooLargeToTravelIsNotAskedFor()

        /// <summary>
        /// The size is read out of the announcement, before the round trip.
        /// </summary>
        /// <remarks>
        /// Two checks and not one, on purpose: this one saves the round trip,
        /// and <see cref="StoreAsync"/> checks again because what comes back is
        /// not bound by what was announced.
        /// </remarks>
        [Test]
        public async Task APictureTooLargeToTravelIsNotAskedFor()
        {

            Assert.That(AvatarStore.Acceptable(new AvatarInfo("0", AvatarStore.MaxBytes + 1, "image/png")),
                        Is.False,
                        "A picture larger than anything that can travel in a stanza would be fetched.");

            // And the second lock: an announcement that lied about the size.
            var big            = new Byte[AvatarStore.MaxBytes + 1];

            new Byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(big, 0);

            var (id, problem)  = await store!.StoreAsync(Alice, Describe(big), big);

            Assert.Multiple(() =>
            {

                Assert.That(id,      Is.Null,
                            "A picture past the limit was kept because its announcement understated it.");

                Assert.That(problem, Is.Not.Null.And.Not.Empty);

            });

        }

        #endregion

        #region OneFileForTwoContactsWithTheSameFace()

        /// <summary>
        /// The id is the content, so the same picture is the same file.
        /// </summary>
        [Test]
        public async Task OneFileForTwoContactsWithTheSameFace()
        {

            var image      = APicture("the same face");
            var info       = Describe(image);

            await store!.StoreAsync(Alice, info, image);
            await store. StoreAsync(Bob,   info, image);

            Assert.Multiple(() =>
            {

                Assert.That(store.IdOf(Alice), Is.EqualTo(info.Id));
                Assert.That(store.IdOf(Bob),   Is.EqualTo(info.Id));

            });

            Assert.That(Directory.GetFiles(Path.Combine(root, AvatarStore.DirectoryName),
                                           "*.png",
                                           SearchOption.AllDirectories),
                        Has.Length.EqualTo(1),
                        "The same picture was written twice.");

        }

        #endregion

        #region TakingAPictureDownLeavesTheFile()

        /// <summary>
        /// Removal drops the pointer and keeps the bytes (XEP-0084, section 4).
        /// </summary>
        /// <remarks>
        /// Deleting them would be tidier and is wrong twice over: the file may
        /// be somebody else's face as well - the test above is exactly that
        /// case - and a picture that comes back is one that need not be fetched
        /// again.
        /// </remarks>
        [Test]
        public async Task TakingAPictureDownLeavesTheFile()
        {

            var image  = APicture();
            var info   = Describe(image);

            await store!.StoreAsync(Alice, info, image);

            Assert.That(store.Forget(Alice), Is.True);

            Assert.Multiple(() =>
            {

                Assert.That(store.IdOf(Alice), Is.Null, "The contact still has a picture after it was taken down.");
                Assert.That(store.Has(info.Id), Is.True, "The bytes were deleted, so a picture that comes back is fetched again.");

            });

        }

        #endregion

        #region TheFacesSurviveARestart()

        /// <summary>
        /// Which is what makes the list look right at a start rather than one
        /// announcement later.
        /// </summary>
        /// <remarks>
        /// An announcement arrives when a contact is online and something
        /// changes. Without the index, somebody signing in before their contacts
        /// do would watch a list of blanks fill in over the following minutes,
        /// having had every one of those pictures on the disk the whole time.
        /// </remarks>
        [Test]
        public async Task TheFacesSurviveARestart()
        {

            var image  = APicture();
            var info   = Describe(image);

            await store!.StoreAsync(Alice, info, image);

            var second = new AvatarStore(root);

            second.UseAccount(Me);

            Assert.That(second.IdOf(Alice), Is.EqualTo(info.Id),
                        "A fresh store does not know whose face is which.");

        }

        #endregion

        #region AnotherAccountSeesNoneOfThem()

        /// <summary>
        /// The route that serves these has no account in it.
        /// </summary>
        /// <remarks>
        /// It is keyed by the id alone, which is what makes it cacheable - so
        /// the separation has to be in the store. A process pointed at a second
        /// account must not answer that account's browser out of the first
        /// one's pictures: whose face is in whose roster is the roster.
        /// </remarks>
        [Test]
        public async Task AnotherAccountSeesNoneOfThem()
        {

            var image  = APicture();
            var info   = Describe(image);

            await store!.StoreAsync(Alice, info, image);

            store.UseAccount(JID.Parse("somebody-else@example.org"));

            Assert.Multiple(() =>
            {

                Assert.That(store.Has(info.Id), Is.False,
                            "A second account can read the first one's pictures.");

                Assert.That(store.TryGet(info.Id, out _, out _), Is.False,
                            "The route would serve a picture belonging to another account.");

                Assert.That(store.IdOf(Alice), Is.Null,
                            "A second account sees whose face the first one kept.");

            });

        }

        #endregion

        #region AnIdThatIsAPathDoesNotReachAFileOutside()

        /// <summary>
        /// The id arrives in a URL and becomes a file name.
        /// </summary>
        /// <remarks>
        /// <b>There is a real file at the other end of this path</b>, and that is
        /// the whole point of the round. The first version of it asked for
        /// <c>../../../../windows/win.ini</c> and was satisfied when the answer
        /// was no - but the answer would have been no with the check removed
        /// too, because there is no <c>win.ini.png</c> anywhere. A mutation that
        /// deleted the check survived it, which is how it came out.
        ///
        /// So a picture is put where the traversal lands, one directory above
        /// the store, and the assertion is that it is still not reachable. Now
        /// the only thing standing between the request and the file is the
        /// check.
        /// </remarks>
        [Test]
        public void AnIdThatIsAPathDoesNotReachAFileOutside()
        {

            // <root>/avatars/<account>/ is where ids are resolved, so two levels
            // up is <root> - outside the store and outside the account.
            var outside = Path.Combine(root, "outside.png");

            File.WriteAllBytes(outside, APicture("not yours"));

            Assert.That(store!.TryGet("../../outside", out var path, out _), Is.False,
                        $"An id that is a path reached {outside}, which the store never wrote.");

            Assert.That(path, Is.Null);

            Assert.That(store.Has("../../outside"), Is.False,
                        "The same path passes for a picture this store holds.");

        }

        #endregion

        #region OnlyTheShapeOfASha1IsAnId()

        /// <summary>
        /// And the shape itself, which is what the check actually is.
        /// </summary>
        /// <remarks>
        /// Checked against what is allowed rather than against a list of
        /// forbidden characters: the shape is known exactly here - forty
        /// lower-case hex digits - and a rule that names what is allowed cannot
        /// be short by one character the way a list of what is not always can.
        /// </remarks>
        [Test]
        public void OnlyTheShapeOfASha1IsAnId()
        {

            foreach (var attempt in new[] {
                         "..",
                         "0123456789abcdef0123456789abcdef0123456789",   // too long
                         "0123456789abcdef0123456789abcdef0123456",      // too short
                         "0123456789ABCDEF0123456789abcdef01234567",     // upper case
                         "0123456789abcdef0123456789abcdef0123456g",     // not hex
                         ""
                     })
            {

                Assert.That(store!.TryGet(attempt, out _, out _), Is.False,
                            $"'{attempt}' was taken for a picture id.");

            }

        }

        #endregion

        #region WebPIsCheckedAtBothEnds()

        /// <summary>
        /// RIFF is a container; "RIFF....WEBP" is a WebP.
        /// </summary>
        /// <remarks>
        /// Without the second half every AVI and every WAV passes for one -
        /// which would not be a way in by itself, but it would mean a file
        /// served as image/webp that is not one, and the whole point of the
        /// signature check is that those two agree.
        /// </remarks>
        [Test]
        public void WebPIsCheckedAtBothEnds()
        {

            Byte[] With(String Tag)
            {

                var bytes = new Byte[16];

                Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
                Encoding.ASCII.GetBytes(Tag).   CopyTo(bytes, 8);

                return bytes;

            }

            Assert.Multiple(() =>
            {

                Assert.That(AvatarStore.LooksLike("image/webp", With("WEBP")), Is.True,
                            "A WebP is not recognised as one.");

                Assert.That(AvatarStore.LooksLike("image/webp", With("AVI ")), Is.False,
                            "Anything beginning with RIFF passes for a WebP.");

                Assert.That(AvatarStore.LooksLike("image/webp", "RIFF"u8.ToArray()), Is.False,
                            "Four bytes pass for a WebP.");

            });

        }

        #endregion

    }

}
