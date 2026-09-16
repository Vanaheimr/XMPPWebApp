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

using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Ratatoskr;

using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// XEP-0084: the faces in the conversation list, kept on this disk and
    /// served from this program's own origin.
    /// </summary>
    /// <remarks>
    /// <b>Filed by the id, which is the SHA-1 of the bytes</b>, and that is the
    /// whole reason this is a store and not a field on the conversation. Two
    /// contacts with the same picture are one file; a picture that has not
    /// changed is not fetched again; and the URL a browser asks for changes
    /// exactly when the picture does, so it can be cached forever and never be
    /// stale. Everything below follows from that.
    ///
    /// <b>These bytes come from a contact and leave through this program's own
    /// origin</b>, which is the part worth being careful about. A stored file
    /// that a browser treats as a document is script running as this page, with
    /// this page's session. So, exactly as in <see cref="MediaStore"/>: a short
    /// list of image types, no <c>image/svg+xml</c> - an SVG is a document with
    /// scripts in it - and served as what it was stored as, never as what a
    /// name suggests, always with nosniff.
    ///
    /// <b>And one check <see cref="MediaStore"/> has no equivalent for: the
    /// claimed type has to match the bytes.</b> The type on an avatar is what
    /// the publisher says it is, and a publisher is somebody in the roster, not
    /// a server. Saying "image/png" over an HTML document costs them nothing.
    /// nosniff is what stops that from being run and would be enough on a
    /// current browser; the signature check is here because it is four bytes of
    /// work and because a file that is not what it says it is has no business
    /// being kept at all.
    ///
    /// What is deliberately <b>not</b> here: decoding. Nothing in this process
    /// parses these images. They are stored as they arrived and handed to the
    /// browser, which is the one program in this picture whose image decoders
    /// are sandboxed and updated weekly.
    /// </remarks>
    public sealed class AvatarStore
    {

        #region Data

        /// <summary>
        /// The directory below the data directory.
        /// </summary>
        public const String DirectoryName = "avatars";

        /// <summary>
        /// The file remembering whose picture is which, beside the pictures.
        /// </summary>
        public const String IndexFileName = "avatars.json";

        /// <summary>
        /// The largest picture that is kept.
        /// </summary>
        /// <remarks>
        /// <see cref="UserAvatar.MaxImageBytes"/>, and deliberately the same
        /// number rather than one of our own: the library refuses to read more
        /// than that out of a stanza, so a larger limit here would be a promise
        /// nothing can keep.
        /// </remarks>
        public const Int32  MaxBytes      = UserAvatar.MaxImageBytes;

        /// <summary>
        /// How much of this disk all the avatars of one account may take up
        /// together.
        /// </summary>
        /// <remarks>
        /// <see cref="MaxBytes"/> bounds one picture and bounds nothing else.
        /// A contact who publishes a new avatar every second publishes a new id
        /// every second, and every new id is a file: without a second number,
        /// anybody in the roster may write to this disk until it is full. At
        /// 256 KiB a picture this allows some two hundred of them, which is far
        /// more faces than a roster has and far less than a problem.
        ///
        /// What happens at the line is that storing stops and nothing else -
        /// nothing already kept is deleted. A face that cannot be stored is a
        /// missing picture, which is what the list shows anyway for everybody
        /// who never published one.
        /// </remarks>
        public const Int64  MaxTotalBytes = 64L * 1024 * 1024;

        /// <summary>
        /// What may be stored, and therefore what may later be served from this
        /// program's own origin - with, for each, the bytes a file of that type
        /// begins with.
        /// </summary>
        /// <remarks>
        /// The four types anybody actually publishes an avatar in. <c>image/svg+xml</c>
        /// is missing on purpose and <c>image/bmp</c> and the rest are missing
        /// only because nothing sends them: a shorter list is a smaller thing to
        /// be wrong about, and an avatar in an exotic format is a missing face
        /// rather than a broken program.
        ///
        /// WebP is the odd one: its signature is "RIFF", four bytes, then the
        /// size, then "WEBP" - so it is checked at two offsets. The entry below
        /// carries the first four; <see cref="LooksLike"/> knows about the rest.
        /// </remarks>
        public static readonly IReadOnlyDictionary<String, (String Extension, Byte[] Magic)> AllowedTypes =
            new Dictionary<String, (String, Byte[])>(StringComparer.OrdinalIgnoreCase) {
                { "image/png",  (".png",  [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) },
                { "image/jpeg", (".jpg",  [0xFF, 0xD8, 0xFF]) },
                { "image/gif",  (".gif",  [0x47, 0x49, 0x46, 0x38]) },
                { "image/webp", (".webp", [0x52, 0x49, 0x46, 0x46]) }
            };


        private readonly Lock                         @lock  = new();

        /// <summary>
        /// Whose picture is which: bare JID to id, for the account in use. It
        /// is what survives a restart - the pictures on disk without it are
        /// bytes nobody points at.
        /// </summary>
        private readonly Dictionary<String, String>   ids    = [];

        private          JID?                         account;
        private          Int64                        used   = -1;

        #endregion

        #region Properties

        /// <summary>
        /// The directory the pictures are kept in.
        /// </summary>
        public String   Root     { get; }

        /// <summary>
        /// Whose pictures are currently loaded, or null before an account is
        /// in use.
        /// </summary>
        public JID?     Account
        {
            get
            {
                lock (@lock)
                    return account;
            }
        }

        /// <summary>
        /// How many contacts currently have a face.
        /// </summary>
        public Int32    Count
        {
            get
            {
                lock (@lock)
                    return ids.Count;
            }
        }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// A store below the given data directory.
        /// </summary>
        public AvatarStore(String Root)
        {
            this.Root = Path.Combine(Root, DirectoryName);
        }

        #endregion


        #region (static) LooksLike(Type, Image)

        /// <summary>
        /// Do these bytes begin the way a file of this type does?
        /// </summary>
        /// <remarks>
        /// Not an image check and not meant as one: a PNG header in front of
        /// rubbish passes, and should - deciding whether the rest decodes is
        /// the browser's job and nothing in this process is going to try. What
        /// this answers is the narrower question that matters for serving it:
        /// is the type this will be labelled with the type the file actually
        /// claims to be, or did somebody hand us a document with a picture's
        /// name on it.
        /// </remarks>
        public static Boolean LooksLike(String Type, ReadOnlySpan<Byte> Image)
        {

            if (!AllowedTypes.TryGetValue(Type, out var expected))
                return false;

            if (Image.Length < expected.Magic.Length)
                return false;

            if (!Image[..expected.Magic.Length].SequenceEqual(expected.Magic))
                return false;

            // RIFF is a container; "RIFF....WEBP" is a WebP. Without the second
            // half, every AVI and every WAV is one.
            if (Type.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                return Image.Length >= 12 &&
                       Image[8..12].SequenceEqual("WEBP"u8);

            return true;

        }

        #endregion

        #region (static) Acceptable(Info)

        /// <summary>
        /// Is this one worth the round trip? Asked before anything is fetched.
        /// </summary>
        /// <remarks>
        /// <b>A picture offered only over HTTP is not one</b>, and that is the
        /// interesting case here rather than the size. XEP-0084 lets the
        /// metadata carry a <c>url</c> instead of the bytes; following it would
        /// mean a contact chooses an address this machine fetches, which is the
        /// whole of <see cref="MediaStore"/>'s problem arriving through a door
        /// that has none of its locks. The library reads the url and never
        /// follows it, and neither does this.
        /// </remarks>
        public static Boolean Acceptable(AvatarInfo Info)

            => Info.Url is null &&
               Info.Bytes > 0 &&
               Info.Bytes <= MaxBytes &&
               AllowedTypes.ContainsKey(Info.Type);

        #endregion


        #region UseAccount(Account)

        /// <summary>
        /// Whose pictures are being kept from now on.
        /// </summary>
        /// <remarks>
        /// The index of the account is read here, so the conversation list has
        /// faces before the first announcement arrives. Which matters more than
        /// it sounds: an announcement arrives when a contact is online and
        /// something changes, so a client that only knew what it had been told
        /// this session would show a blank list every morning.
        /// </remarks>
        public void UseAccount(JID? Account)
        {

            lock (@lock)
            {

                account = Account;
                used    = -1;

                ids.Clear();

                if (Account is not JID who)
                    return;

                var file = IndexFile(who);

                if (!File.Exists(file))
                    return;

                try
                {

                    var json = JObject.Parse(File.ReadAllText(file));

                    foreach (var property in json.Properties())
                    {

                        var id = property.Value.Value<String>();

                        // Only what is still on the disk. An index entry whose
                        // file is gone would be a URL the browser asks for and
                        // gets a 404 for, over and over.
                        if (id is not null && IsId(id) && Stored(who, id) is not null)
                            ids[property.Name] = id;

                    }

                }
                catch (Exception)
                {
                    // A broken index is a list without faces, which is what the
                    // very first start looks like too. The pictures stay; the
                    // next announcement writes a fresh index.
                    ids.Clear();
                }

            }

        }

        #endregion

        #region IdOf(Jid) / Has(Id) / Forget(Jid)

        /// <summary>
        /// The id of this contact's picture, or null when there is none.
        /// </summary>
        public String? IdOf(JID Jid)
        {
            lock (@lock)
                return ids.TryGetValue(Jid.ToString(), out var id) ? id : null;
        }

        /// <summary>
        /// Is this picture already on the disk? <b>What makes an announcement
        /// cost nothing</b>: a contact who comes online announces the picture
        /// this client has had for a month, and the answer is to do nothing.
        /// </summary>
        public Boolean Has(String Id)
        {

            if (Account is not JID who || !IsId(Id))
                return false;

            return Stored(who, Id) is not null;

        }

        /// <summary>
        /// The picture was taken down (XEP-0084, section 4).
        /// </summary>
        /// <remarks>
        /// The file is left where it is and only the pointer goes. It may well
        /// be somebody else's face too - the id is the bytes - and even where it
        /// is not, a picture that comes back is one that does not have to be
        /// fetched again.
        /// </remarks>
        public Boolean Forget(JID Jid)
        {

            lock (@lock)
            {

                if (!ids.Remove(Jid.ToString()))
                    return false;

                WriteIndex();
                return true;

            }

        }

        #endregion

        #region Remember(Jid, Id)

        /// <summary>
        /// This contact's picture is the one already stored under this id.
        /// </summary>
        /// <returns>Whether anything changed.</returns>
        public Boolean Remember(JID Jid, String Id)
        {

            lock (@lock)
            {

                var key = Jid.ToString();

                if (ids.TryGetValue(key, out var current) && current == Id)
                    return false;

                ids[key] = Id;

                WriteIndex();
                return true;

            }

        }

        #endregion

        #region StoreAsync(Jid, Info, Image, CancellationToken = default)

        /// <summary>
        /// Keeps a picture and points a contact at it.
        /// </summary>
        /// <returns>
        /// The id it was stored under, or the reason there is none. Never
        /// throws for an expected failure: a face that could not be kept is an
        /// ordinary outcome and the list shows what it shows for everybody
        /// without one.
        /// </returns>
        public async Task<(String? Id, String? Problem)> StoreAsync(JID                Jid,
                                                                    AvatarInfo         Info,
                                                                    Byte[]             Image,
                                                                    CancellationToken  CancellationToken = default)
        {

            if (Account is not JID who)
                return (null, "no account in use");

            if (Image.Length > MaxBytes)
                return (null, $"the picture is {Image.Length} bytes; at most {MaxBytes} are kept");

            if (!AllowedTypes.TryGetValue(Info.Type, out var allowed))
                return (null, $"{Info.Type} is not a type this serves");

            // The id is the name everything downstream caches by, so bytes that
            // do not hash to it must not be filed under it - they would be shown
            // for every later avatar that really has that id, and go on being
            // shown after the mistake was fixed everywhere else.
            //
            // The library checks this too, in FetchAvatarAsync. It is checked
            // again here because this is the layer whose entire contract is
            // "filed by the hash of its content", and a store that trusts its
            // caller's idea of the hash does not have that contract.
            if (!UserAvatar.Matches(Info, Image))
                return (null, "the bytes are not the ones that were announced");

            if (!LooksLike(Info.Type, Image))
                return (null, $"the bytes do not begin like {Info.Type}");

            var path = Path.Combine(Root, Folder(who), Info.Id + allowed.Extension);

            // Already here, which is the common case for a picture two contacts
            // share and for one that comes back after being taken down.
            if (File.Exists(path))
            {
                Remember(Jid, Info.Id);
                return (Info.Id, null);
            }

            lock (@lock)
            {

                if (used < 0)
                    used = Measure(Path.Combine(Root, Folder(who)));

                if (used + Image.LongLength > MaxTotalBytes)
                    return (null, $"the {MaxTotalBytes} bytes kept for the avatars of {who} are full " +
                                  $"({used} bytes); nothing further is stored");

                used += Image.LongLength;

            }

            try
            {

                OwnerOnlyFile.CreateDirectory(Path.Combine(Root, Folder(who)));

                await OwnerOnlyFile.WriteAllBytesAsync(path, Image, CancellationToken);

            }
            catch (Exception e)
            {

                lock (@lock)
                    used -= Image.LongLength;

                return (null, e.Message);

            }

            Remember(Jid, Info.Id);

            return (Info.Id, null);

        }

        #endregion

        #region TryGet(Id, out Path, out ContentType)

        /// <summary>
        /// Where a stored picture lies and what it is to be served as.
        /// </summary>
        /// <remarks>
        /// <b>The type comes from the extension this store itself wrote</b> and
        /// never from anything a request carried. The id is checked against the
        /// shape of a SHA-1 before it reaches the file system at all, which is
        /// what stops a path from being a request parameter.
        /// </remarks>
        public Boolean TryGet(String                        Id,
                              out String?                   Path,
                              [NotNullWhen(true)] out String?  ContentType)
        {

            Path         = null;
            ContentType  = null;

            if (Account is not JID who || !IsId(Id))
                return false;

            var found = Stored(who, Id);

            if (found is null)
                return false;

            Path         = found.Value.Path;
            ContentType  = found.Value.Type;

            return true;

        }

        #endregion


        #region (private) Stored(Account, Id) / IsId(Text) / Folder(Jid)

        /// <summary>
        /// The file of this id, whichever type it turned out to be, or null.
        /// </summary>
        private (String Path, String Type)? Stored(JID Account, String Id)
        {

            foreach (var (type, allowed) in AllowedTypes)
            {

                var path = Path.Combine(Root, Folder(Account), Id + allowed.Extension);

                if (File.Exists(path))
                    return (path, type);

            }

            return null;

        }

        /// <summary>
        /// Forty lower-case hex digits and nothing else.
        /// </summary>
        /// <remarks>
        /// This is the guard that lets an id out of a request and into a path.
        /// <see cref="ChatArchivePaths.IsSafeName"/> does the same job for the
        /// media route; here the shape is known exactly, so the check is the
        /// shape and not a list of forbidden characters.
        /// </remarks>
        private static Boolean IsId(String Text)

            => Text.Length == 40 &&
               Text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static String Folder(JID Account)

            => ChatArchivePaths.SafeName(Account.ToString());

        #endregion

        #region (private) IndexFile(Account) / WriteIndex() / Measure(Directory)

        private String IndexFile(JID Account)

            => Path.Combine(Root, Folder(Account), IndexFileName);

        /// <summary>
        /// Writes down whose picture is which. Called under the lock.
        /// </summary>
        private void WriteIndex()
        {

            if (account is not JID who)
                return;

            try
            {

                OwnerOnlyFile.CreateDirectory(Path.Combine(Root, Folder(who)));

                OwnerOnlyFile.Write(
                    IndexFile(who),
                    new JObject(ids.Select(entry => new JProperty(entry.Key, entry.Value))).ToString()
                );

            }
            catch (Exception)
            {
                // The pictures are on the disk either way; an index that could
                // not be written costs a fetch each after the next start.
            }

        }

        private static Int64 Measure(String Directory)
        {

            try
            {

                return System.IO.Directory.Exists(Directory)
                           ? new DirectoryInfo(Directory).EnumerateFiles().Sum(file => file.Length)
                           : 0;

            }
            catch (Exception)
            {
                // Unreadable is treated as full rather than as empty: the
                // number exists to stop a disk filling up, and a guess that
                // errs the other way is not a limit.
                return MaxTotalBytes;
            }

        }

        #endregion

    }

}
