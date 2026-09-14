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

using System.Net;
using System.Net.Sockets;

using org.GraphDefined.Vanaheimr.Ratatoskr;

using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// Fetches the files shared in a conversation, puts them beside it - and
    /// refuses to fetch most other things.
    /// </summary>
    /// <remarks>
    /// <b>This turns a message into a network request, so the sender decides
    /// what this machine fetches.</b> Everybody who may write to us can put a
    /// URL in front of us, and only the rules below stop that URL from being an
    /// address inside the network this program runs in. Hence: https only, no
    /// address that belongs to this machine or to a private network, redirects
    /// followed by hand and checked at every hop, an upper size, and a timeout.
    /// The same reasoning, and largely the same code, as MediaStore in
    /// XMPPConsole.
    ///
    /// <b>And the file comes back out of this program's own origin,</b> which
    /// the console never had to think about. A stored file is served from the
    /// same site as the page, so a stored file that the browser treats as a
    /// document is script running as this page. Two rules follow, and neither
    /// is negotiable:
    ///
    ///   - only the content types in <see cref="AllowedTypes"/> are stored at
    ///     all, and image/svg+xml is not among them: an SVG is a document with
    ///     scripts in it, and opening one in a tab would run them here;
    ///
    ///   - what is stored is served with the type it was stored as, never a
    ///     type guessed from the file name, and with nosniff.
    ///
    /// One gap is named rather than papered over: the host is resolved, checked
    /// and then handed to the HTTP client, which resolves it a second time. A
    /// name that answers differently on the second query gets through
    /// (DNS rebinding). Closing it means connecting to the address that was
    /// checked and carrying the name only in SNI and the Host header, which is
    /// a socket handler of one's own. What stands here raises the cost; it does
    /// not make it impossible.
    /// </remarks>
    public sealed class MediaStore : IDisposable
    {

        #region Data

        /// <summary>
        /// The most that is fetched for a single file. A shared photo is a few
        /// megabytes; this leaves room for a short video and still says no to
        /// somebody pointing us at an installation image.
        /// </summary>
        public const           Int64     MaxBytes      = 64L * 1024 * 1024;

        /// <summary>
        /// How much of this disk all the files of one account may take up
        /// together.
        /// </summary>
        /// <remarks>
        /// <see cref="MaxBytes"/> bounds one file and bounds nothing else: the
        /// number of files is decided by whoever is sending them. Without a
        /// second number, anybody who may write to this account may write to
        /// this disk until it is full, and a full disk is not an archive that
        /// stopped growing - it is a machine that stopped.
        ///
        /// What happens at the line is that fetching stops, and nothing else.
        /// Nothing already written is deleted and nothing said is lost: the
        /// conversations keep being archived, the files simply stay where they
        /// were shared and are shown from there. An archive whose promise is
        /// that everything said is kept may not start deleting to make room.
        /// </remarks>
        public const           Int64     MaxTotalBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>
        /// The line in force for this store; <see cref="MaxTotalBytes"/> unless
        /// something else was asked for.
        /// </summary>
        public Int64 TotalBytesAllowed { get; }

        /// <summary>
        /// How long one file may take altogether.
        /// </summary>
        public static readonly TimeSpan  Timeout       = TimeSpan.FromMinutes(2);

        /// <summary>
        /// How often a redirect is followed. Every hop is checked like the
        /// first address.
        /// </summary>
        public const           Int32     MaxRedirects  = 5;

        /// <summary>
        /// What may be stored, and therefore what may later be served from this
        /// program's own origin. Everything a chat client shows or plays, and
        /// nothing a browser would treat as a document.
        /// </summary>
        /// <remarks>
        /// image/svg+xml is missing on purpose - see the remarks on the class.
        /// So is text/*, application/pdf and everything else that opens rather
        /// than displays.
        /// </remarks>
        public static readonly IReadOnlyDictionary<String, String> AllowedTypes =
            new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase) {
                { "image/png",       ".png"  },
                { "image/jpeg",      ".jpg"  },
                { "image/gif",       ".gif"  },
                { "image/webp",      ".webp" },
                { "image/avif",      ".avif" },
                { "image/bmp",       ".bmp"  },
                { "video/mp4",       ".mp4"  },
                { "video/webm",      ".webm" },
                { "video/ogg",       ".ogv"  },
                { "audio/mpeg",      ".mp3"  },
                { "audio/mp4",       ".m4a"  },
                { "audio/ogg",       ".oga"  },
                { "audio/opus",      ".opus" },
                { "audio/wav",       ".wav"  },
                { "audio/webm",      ".weba" }
            };

        /// <summary>
        /// The other direction: what a stored file is served as, by its
        /// extension. Every extension a name may end in and still be opened by
        /// a browser rather than downloaded.
        /// </summary>
        /// <remarks>
        /// ".ogg" is deliberately absent - it means audio to some and video to
        /// others, and a file this program stored carries ".oga" or ".ogv"
        /// because those say which. A name that ends in ".ogg" therefore
        /// arrived from somewhere else and is served as a download.
        /// </remarks>
        private static readonly IReadOnlyDictionary<String, String> typesByExtension =
            new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase) {
                { ".png",   "image/png"   },
                { ".jpg",   "image/jpeg"  },
                { ".jpeg",  "image/jpeg"  },
                { ".gif",   "image/gif"   },
                { ".webp",  "image/webp"  },
                { ".avif",  "image/avif"  },
                { ".bmp",   "image/bmp"   },
                { ".mp4",   "video/mp4"   },
                { ".m4v",   "video/mp4"   },
                { ".webm",  "video/webm"  },
                { ".ogv",   "video/ogg"   },
                { ".mp3",   "audio/mpeg"  },
                { ".m4a",   "audio/mp4"   },
                { ".oga",   "audio/ogg"   },
                { ".opus",  "audio/opus"  },
                { ".wav",   "audio/wav"   },
                { ".weba",  "audio/webm"  }
            };

        private readonly HttpClient                 httpClient;
        private readonly Lock                       @lock   = new();

        /// <summary>
        /// How many bytes of files one account already keeps here. Counted once
        /// by walking the directories, and kept up to date from then on - the
        /// walk is what a restart costs, once, and not what every shared file
        /// costs.
        /// </summary>
        private readonly Dictionary<String, Int64>  used    = [];

        #endregion

        #region Properties

        /// <summary>
        /// The archive root everything is written below.
        /// </summary>
        public String  Root    { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates a store below the archive root.
        /// </summary>
        /// <param name="Root">Where the archive lives.</param>
        /// <param name="TotalBytesAllowed">How much one account's files may take up together; <see cref="MaxTotalBytes"/> by default.</param>
        public MediaStore(String  Root,
                          Int64?  TotalBytesAllowed   = null)
        {

            this.Root               = Path.GetFullPath(Root);
            this.TotalBytesAllowed  = TotalBytesAllowed ?? MaxTotalBytes;

            // Redirects by hand: AllowAutoRedirect would follow a 302 into the
            // private network the first address was refused for.
            this.httpClient = new HttpClient(new HttpClientHandler {
                                  AllowAutoRedirect = false
                              }) {
                                  Timeout = Timeout
                              };

        }

        #endregion


        #region (static) ContentTypeForName(FileName)

        /// <summary>
        /// What a stored file is served as, or null when its name says nothing
        /// this program is willing to let a browser open.
        /// </summary>
        /// <remarks>
        /// Not <c>HTTPContentType.ForFileName</c>, which knows .html and .svg
        /// and every other type a browser runs code from. A file in here came
        /// from a stranger, and this program serves it from its own origin -
        /// so the question is not "what is this file" but "what is this file
        /// allowed to be".
        /// </remarks>
        public static String? ContentTypeForName(String FileName)
        {

            var dot = FileName.LastIndexOf('.');

            return dot >= 0 && typesByExtension.TryGetValue(FileName[dot..], out var type)
                       ? type
                       : null;

        }

        #endregion


        #region FetchAsync(Account, Peer, URL, ReceivedAt, CancellationToken = default)

        /// <summary>
        /// Fetches one file and puts it beside the conversation it belongs to.
        /// </summary>
        /// <param name="Account">Whose archive it goes into.</param>
        /// <param name="Peer">The conversation it belongs to.</param>
        /// <param name="URL">Where it lies; https, or an aesgcm URL carrying its key.</param>
        /// <param name="ReceivedAt">
        /// When the download happened. It goes into the file name, because it
        /// is the one time this side actually knows: the timestamp inside a
        /// message is what the sender claims.
        /// </param>
        /// <returns>
        /// The stored file, or the reason there is none. Never throws for an
        /// expected failure; a file that cannot be fetched is an ordinary
        /// outcome of talking to strangers.
        /// </returns>
        public async Task<(MediaRef? Media, String? Problem)> FetchAsync(JID                Account,
                                                                         JID                Peer,
                                                                         Uri                URL,
                                                                         DateTimeOffset     ReceivedAt,
                                                                         CancellationToken  CancellationToken = default)
        {

            // First of all, and before a single byte travels: a fetch that
            // could not be kept is bandwidth spent on nothing.
            if (!HasRoom(Account, out var alreadyUsed))
                return (null, $"the {TotalBytesAllowed} bytes kept for {Account} are full ({alreadyUsed} bytes); nothing further is fetched");

            Byte[]? key    = null;
            Byte[]? nonce  = null;

            var isEncrypted  = AesGcmUrl.IsAesGcmUrl(URL);
            var address      = URL;

            if (isEncrypted)
            {

                if (!AesGcmUrl.TryParse(URL, out key, out nonce, out var problem))
                    return (null, problem);

                address = AesGcmUrl.ToHttps(URL);

            }

            try
            {

                var (content, contentType, problem) = await DownloadAsync(address, CancellationToken);

                if (problem is not null)
                    return (null, problem);

                if (isEncrypted)
                {
                    try
                    {
                        content = AesGcmUrl.Decrypt(content!, key!, nonce!);
                    }
                    catch (Exception e)
                    {
                        // A failing tag is not a broken download but a file
                        // that is not what the sender's key says it is. It is
                        // not stored: a file that fails its own check has no
                        // business in an archive.
                        return (null, $"decryption failed ({e.Message})");
                    }
                }

                var directory = ChatArchivePaths.MediaDirectory(Root, Account.ToString(), Peer.ToString());

                OwnerOnlyFile.CreateDirectory(directory);

                // The name the URL suggests is kept where it already says what
                // the file is, and is given the extension of what the file is
                // where it does not. A name and a content type that disagree
                // are how a stored picture becomes a served document.
                var suggested = ChatArchivePaths.SafeName(SuggestedNameOf(address) ?? "file");

                var name      = ChatArchivePaths.MediaFileName(
                                    ReceivedAt,
                                    suggested,
                                    ContentTypeForName(suggested) == contentType
                                        ? null
                                        : AllowedTypes[contentType!]
                                );

                await OwnerOnlyFile.WriteAllBytesAsync(Path.Combine(directory, name), content!, CancellationToken);

                Remember(Account, content!.LongLength);

                return (new MediaRef(
                            name,
                            contentType!,
                            content!.LongLength,
                            // The aesgcm URL carries the key in its fragment,
                            // so what is written down is the address alone.
                            isEncrypted ? address.AbsoluteUri : URL.AbsoluteUri
                        ),
                        null);

            }
            catch (OperationCanceledException)
            {
                return (null, "cancelled");
            }
            catch (Exception e)
            {
                return (null, e.Message);
            }

        }

        #endregion


        #region (private) DownloadAsync(Address, CancellationToken)

        private async Task<(Byte[]? Content, String? ContentType, String? Problem)> DownloadAsync(Uri                Address,
                                                                                                  CancellationToken  CancellationToken)
        {

            var address = Address;

            for (var hop = 0; hop <= MaxRedirects; hop++)
            {

                if (!address.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    return (null, null, $"{address.Scheme} is not fetched, https only");

                if (await AddressIsRefusedAsync(address, CancellationToken) is String refusal)
                    return (null, null, refusal);

                using var request  = new HttpRequestMessage(HttpMethod.Get, address);
                var       response = await httpClient.SendAsync(request,
                                                                HttpCompletionOption.ResponseHeadersRead,
                                                                CancellationToken);

                using (response)
                {

                    if (response.StatusCode is HttpStatusCode.Moved
                                            or HttpStatusCode.Found
                                            or HttpStatusCode.SeeOther
                                            or HttpStatusCode.TemporaryRedirect
                                            or HttpStatusCode.PermanentRedirect)
                    {

                        var location = response.Headers.Location;

                        if (location is null)
                            return (null, null, $"redirect {(Int32) response.StatusCode} without a target");

                        address = location.IsAbsoluteUri
                                      ? location
                                      : new Uri(address, location);

                        continue;

                    }

                    if (!response.IsSuccessStatusCode)
                        return (null, null, $"HTTP {(Int32) response.StatusCode} {response.ReasonPhrase}");

                    // Lower case is the canonical spelling here: every key of
                    // AllowedTypes is lower case, so this is the name the file
                    // is stored and served under, whatever the server wrote.
                    var mediaType = (response.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();

                    if (!AllowedTypes.ContainsKey(mediaType))
                        return (null, null, mediaType.Length == 0
                                                ? "the server did not say what it is"
                                                : $"{mediaType} is not a kind of file this archive keeps");

                    // The announced length is a claim and is checked first only
                    // to save the transfer; the real limit is counted while
                    // reading, because the claim may be missing or wrong.
                    if (response.Content.Headers.ContentLength > MaxBytes)
                        return (null, null, $"{response.Content.Headers.ContentLength} bytes announced, " +
                                            $"more than the {MaxBytes} allowed");

                    var (content, problem) = await ReadAtMostAsync(response, CancellationToken);

                    return (content, mediaType, problem);

                }

            }

            return (null, null, $"more than {MaxRedirects} redirects");

        }

        #endregion

        #region (private) HasRoom(Account, out Used) / Remember(Account, Bytes)

        /// <summary>
        /// Whether this account is still below its line, and how much it keeps
        /// already.
        /// </summary>
        private Boolean HasRoom(JID        Account,
                                out Int64  Used)
        {

            var account = ChatArchivePaths.SafeName(Account.ToString());

            lock (@lock)
            {

                if (!used.TryGetValue(account, out var bytes))
                {
                    bytes           = Measure(ChatArchivePaths.AccountDirectory(Root, Account.ToString()));
                    used[account]   = bytes;
                }

                Used = bytes;
                return bytes < TotalBytesAllowed;

            }

        }

        /// <summary>
        /// Count a file that has just been written.
        /// </summary>
        private void Remember(JID    Account,
                              Int64  Bytes)
        {

            var account = ChatArchivePaths.SafeName(Account.ToString());

            lock (@lock)
            {
                used[account] = used.TryGetValue(account, out var bytes)
                                    ? bytes + Bytes
                                    : Bytes;
            }

        }

        /// <summary>
        /// What the files of one account take up on disk: the media directories
        /// of its conversations, and nothing else - the conversations
        /// themselves are lines of JSON written by this program and are not
        /// what anybody else decides the size of.
        /// </summary>
        private static Int64 Measure(String AccountDirectory)
        {

            if (!Directory.Exists(AccountDirectory))
                return 0;

            var total = 0L;

            try
            {

                foreach (var conversation in Directory.EnumerateDirectories(AccountDirectory))
                {

                    var directory = Path.Combine(conversation, ChatArchivePaths.MediaDirectoryName);

                    if (!Directory.Exists(directory))
                        continue;

                    foreach (var file in Directory.EnumerateFiles(directory))
                        total += new FileInfo(file).Length;

                }

            }
            catch (Exception)
            {
                // A directory that cannot be walked is one whose size is
                // unknown, and an unknown size is treated as none: refusing to
                // fetch anything ever again because of a permission error would
                // be the worse mistake of the two.
            }

            return total;

        }

        #endregion

        #region (private static) ReadAtMostAsync(Response, CancellationToken)

        private static async Task<(Byte[]? Content, String? Problem)> ReadAtMostAsync(HttpResponseMessage  Response,
                                                                                      CancellationToken    CancellationToken)
        {

            using var stream  = await Response.Content.ReadAsStreamAsync(CancellationToken);

            // Sized from what the server announced, where that is a size worth
            // believing: a MemoryStream that has to grow doubles its buffer, so
            // the last doubling of a 64 MiB file asks for 64 MiB more than the
            // file needs. The announcement is not trusted as a limit - the loop
            // below still counts every byte - only as a guess at the shape of
            // the allocation.
            var announced     = Response.Content.Headers.ContentLength ?? 0;

            using var buffer  = new MemoryStream(announced > 0 && announced <= MaxBytes
                                                     ? (Int32) announced
                                                     : 0);

            var chunk  = new Byte[81920];
            var total  = 0L;

            while (true)
            {

                var read = await stream.ReadAsync(chunk, CancellationToken);

                if (read == 0)
                    break;

                total += read;

                if (total > MaxBytes)
                    return (null, $"larger than the {MaxBytes} bytes allowed");

                buffer.Write(chunk, 0, read);

            }

            if (total == 0)
                return (null, "empty");

            return (buffer.ToArray(), null);

        }

        #endregion

        #region (private static) AddressIsRefusedAsync(Address, CancellationToken)

        /// <summary>
        /// Why this address is not fetched - or null when it may be.
        /// </summary>
        private static async Task<String?> AddressIsRefusedAsync(Uri                Address,
                                                                 CancellationToken  CancellationToken)
        {

            IPAddress[] addresses;

            if (IPAddress.TryParse(Address.Host.Trim('[', ']'), out var literal))
                addresses = [literal];

            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(Address.Host, CancellationToken);
                }
                catch (Exception e)
                {
                    return $"{Address.Host} did not resolve ({e.Message})";
                }
            }

            if (addresses.Length == 0)
                return $"{Address.Host} did not resolve";

            // Every address, not the first: a name pointing at one public and
            // one internal address must not get through because the public one
            // was looked at first.
            foreach (var address in addresses)
                if (IsLocalOrPrivate(address))
                    return $"{Address.Host} resolves to {address}, which is not a public address";

            return null;

        }

        #endregion

        #region (private static) IsLocalOrPrivate(Address)

        private static Boolean IsLocalOrPrivate(IPAddress Address)
        {

            if (IPAddress.IsLoopback(Address))
                return true;

            if (Address.AddressFamily == AddressFamily.InterNetworkV6)
            {

                if (Address.IsIPv6LinkLocal || Address.IsIPv6SiteLocal || Address.IsIPv6Multicast)
                    return true;

                // Unique local addresses, fc00::/7.
                var v6 = Address.GetAddressBytes();
                if ((v6[0] & 0xFE) == 0xFC)
                    return true;

                // An IPv4 address in v6 clothing is still that address.
                if (Address.IsIPv4MappedToIPv6)
                    return IsLocalOrPrivate(Address.MapToIPv4());

                return Address.Equals(IPAddress.IPv6Any);

            }

            var v4 = Address.GetAddressBytes();

            return v4[0] switch {
                       0    => true,                                   // this network
                       10   => true,                                   // RFC 1918
                       127  => true,                                   // loopback
                       169  => v4[1] == 254,                           // link local
                       172  => v4[1] >= 16 && v4[1] <= 31,             // RFC 1918
                       192  => (v4[1] == 168) ||                       // RFC 1918
                               (v4[1] == 0 && v4[2] == 0),             // IETF protocol assignments
                       198  => v4[1] == 18 || v4[1] == 19,             // benchmarking
                       _    => v4[0] >= 224                            // multicast and reserved
                   };

        }

        #endregion

        #region (private static) SuggestedNameOf(Address)

        /// <summary>
        /// What the URL calls the file. A suggestion and nothing more - it is a
        /// stranger's text and goes through the same sanitising as a JID.
        /// </summary>
        private static String? SuggestedNameOf(Uri Address)
        {

            var last   = Address.AbsolutePath.TrimEnd('/');
            var slash  = last.LastIndexOf('/');

            if (slash >= 0 && slash < last.Length - 1)
                last = last[(slash + 1)..];

            try
            {
                last = Uri.UnescapeDataString(last);
            }
            catch (UriFormatException)
            { }

            return last.Length == 0 ? null : last;

        }

        #endregion


        #region Dispose()

        public void Dispose()
            => httpClient.Dispose();

        #endregion

    }

}
