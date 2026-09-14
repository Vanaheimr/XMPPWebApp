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
using System.Globalization;
using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// Where a conversation and its files go on disk, and what a JID is allowed
    /// to look like once it has become a directory name.
    /// </summary>
    /// <remarks>
    /// The layout, from the archive root down:
    ///
    /// <code>
    /// chats/
    ///   me@example.org/                             one directory per account
    ///     alice@example.org/                        one per conversation
    ///       alice@example.org_2026-09.jsonl         one per month
    ///       alice@example.org_2026-10.jsonl
    ///       media/
    ///         20260913T071848Z_photo.jpg            what arrived, when it arrived
    /// </code>
    ///
    /// The account is the top level because the account can be changed while
    /// the program runs. Without that level, switching accounts would show
    /// the conversations of the old one under the new.
    ///
    /// <b>A JID is not a file name, and the difference is not academic.</b> The
    /// localpart of a JID forbids <c>"&amp;'/:&lt;&gt;@</c> (RFC 7622, section
    /// 3.3), but the domain and the resource forbid almost nothing: a resource
    /// is any sequence that survives the PRECIS OpaqueString profile, which
    /// includes <c>\</c>, <c>..</c> and a leading dot. That is a path traversal
    /// waiting for whoever names their resource <c>..\..\..\Windows</c>, and a
    /// JID is a stranger's text before it is anything else.
    ///
    /// Everything outside the permitted set therefore becomes '_'. What that
    /// costs is honest and small: two JIDs that differ only in a forbidden
    /// character share a directory. What it saves is a class of bug in which
    /// the answer to "where did the file go" is "anywhere". The same rule, and
    /// the same reasoning, as ChatLogPaths in XMPPConsole.
    /// </remarks>
    public static class ChatArchivePaths
    {

        #region Data

        /// <summary>
        /// The directory the files of a conversation are put in, below the
        /// directory of the conversation itself.
        /// </summary>
        public const String  MediaDirectoryName  = "media";

        /// <summary>
        /// What a month's file is called after the name and the month.
        /// </summary>
        public const String  LogFileExtension    = ".jsonl";

        /// <summary>
        /// A file name may not grow past what the file system takes. 120 leaves
        /// room for the "_yyyy-MM.jsonl" that follows and stays well inside the
        /// 255 bytes the usual file systems allow.
        /// </summary>
        private const Int32  MaxNameLength       = 120;

        /// <summary>
        /// Reserved device names on Windows. A file called <c>NUL</c> cannot be
        /// created there, and a JID <c>nul@example.org</c> is nothing unusual -
        /// but only the part before the '@' is the danger, so it is the whole
        /// name that is checked, after shortening.
        /// </summary>
        private static readonly String[] reservedNames = [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];

        #endregion


        #region SafeName(Text)

        /// <summary>
        /// The text as a single path segment - never empty, never a traversal,
        /// never a reserved name.
        /// </summary>
        /// <remarks>
        /// Letters and digits survive as they are, Unicode included: a chat
        /// with 日本 belongs in a directory that says so.
        /// </remarks>
        public static String SafeName(String Text)
        {

            var builder = new StringBuilder(Text.Length);

            foreach (var character in Text)
            {

                // Letters, digits and the four that occur in a JID and are
                // harmless in a path. Everything else, including every control
                // character and every separator, becomes '_'.
                if (Char.IsLetterOrDigit(character) ||
                    character == '@' || character == '.' ||
                    character == '-' || character == '_')
                {
                    builder.Append(character);
                }

                else
                    builder.Append('_');

            }

            var name = builder.ToString();

            // '.' and '..' survive the filter above - both consist of permitted
            // characters and both mean something else entirely to a path. A
            // name of dots alone is therefore not a name.
            if (name.Trim('.').Length == 0)
                name = "_";

            // A trailing dot or space is dropped silently by Windows, so
            // "a." and "a" would be the same directory while looking different
            // in the archive.
            name = name.TrimEnd('.', ' ');

            if (name.Length == 0)
                name = "_";

            if (name.Length > MaxNameLength)
                name = name[..MaxNameLength];

            if (reservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                name = "_" + name;

            return name;

        }

        #endregion

        #region IsSafeName(Text)

        /// <summary>
        /// Whether this text is already a name <see cref="SafeName"/> would
        /// leave alone - which is what a name that arrived in a URL has to be
        /// before it names a file.
        /// </summary>
        public static Boolean IsSafeName([NotNullWhen(true)] String? Text)

            => Text is not null &&
               Text.Length > 0  &&
               Text.Length <= MaxNameLength &&
               SafeName(Text) == Text;

        #endregion


        #region AccountDirectory     (Root, Account)

        /// <summary>
        /// Everything one account ever said or heard.
        /// </summary>
        public static String AccountDirectory(String  Root,
                                              String  Account)

            => Path.Combine(Root,
                            SafeName(Account));

        #endregion

        #region ConversationDirectory(Root, Account, Peer)

        /// <summary>
        /// Everything of one conversation: its months and its media.
        /// </summary>
        public static String ConversationDirectory(String  Root,
                                                   String  Account,
                                                   String  Peer)

            => Path.Combine(Root,
                            SafeName(Account),
                            SafeName(Peer));

        #endregion

        #region LogFile              (Root, Account, Peer, When)

        /// <summary>
        /// The file of one conversation for the month of <paramref name="When"/>.
        /// </summary>
        /// <remarks>
        /// One file per month, because a chat log is read at the far end of a
        /// year and grep does not care, but an editor does. And because a month
        /// is the unit this program itself reads in: the last one is loaded at
        /// every start, the ones before it when somebody scrolls up.
        /// </remarks>
        public static String LogFile(String          Root,
                                     String          Account,
                                     String          Peer,
                                     DateTimeOffset  When)
        {

            var name = SafeName(Peer);

            return Path.Combine(Root,
                                SafeName(Account),
                                name,
                                LogFileName(name, When));

        }

        #endregion

        #region LogFileName          (SafePeerName, When)

        /// <summary>
        /// What a month's file is called: the conversation, then the month.
        /// </summary>
        /// <remarks>
        /// The month is written the way ISO 8601 writes one, with the hyphen:
        /// "2026-09", not "202609". The files beside it in media/ use the basic
        /// form - "20260913T071848Z" - and that is not an inconsistency but the
        /// only way round: a time needs a separator between its parts, ISO 8601
        /// spells that ':', and a colon cannot be in a file name on Windows. So
        /// the basic form is used exactly where a colon would otherwise be, and
        /// the readable one everywhere else.
        /// </remarks>
        public static String LogFileName(String          SafePeerName,
                                         DateTimeOffset  When)

            => String.Concat(SafePeerName,
                             "_",
                             When.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                             LogFileExtension);

        #endregion

        #region TryParseMonth        (LogFileName, out Month)

        /// <summary>
        /// The month a file name ends in, as its first day, UTC. False for
        /// anything that is not one of these files.
        /// </summary>
        /// <remarks>
        /// The month is read off the name rather than the content so that a
        /// directory can be walked backwards in time without opening anything.
        /// </remarks>
        public static Boolean TryParseMonth(String              LogFileName,
                                            out DateTimeOffset  Month)
        {

            Month = default;

            var name = Path.GetFileName(LogFileName);

            if (!name.EndsWith(LogFileExtension, StringComparison.OrdinalIgnoreCase))
                return false;

            var stem       = name[..^LogFileExtension.Length];
            var underscore = stem.LastIndexOf('_');

            if (underscore < 0 || stem.Length - underscore - 1 != 7)
                return false;

            if (!DateTime.TryParseExact(stem[(underscore + 1)..],
                                        "yyyy-MM",
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                        out var parsed))
            {
                return false;
            }

            Month = new DateTimeOffset(parsed.Year, parsed.Month, 1, 0, 0, 0, TimeSpan.Zero);
            return true;

        }

        #endregion

        #region MediaDirectory       (Root, Account, Peer)

        /// <summary>
        /// Where the files of one conversation are put.
        /// </summary>
        public static String MediaDirectory(String  Root,
                                            String  Account,
                                            String  Peer)

            => Path.Combine(Root,
                            SafeName(Account),
                            SafeName(Peer),
                            MediaDirectoryName);

        #endregion

        #region MediaFileName        (ReceivedAt, SuggestedName, Extension = null)

        /// <summary>
        /// The name a stored file gets: when it arrived, then what it was
        /// called.
        /// </summary>
        /// <remarks>
        /// The timestamp goes first, in the basic ISO 8601 form
        /// <c>20260913T071848Z</c>: the directory then sorts by time on its own,
        /// and it is the one part of the name this side is sure of. The extended
        /// form is not an option - a colon cannot be in a file name on Windows -
        /// and the basic form is no less ISO 8601 for it.
        ///
        /// The rest comes from the URL and is therefore a stranger's text: it
        /// goes through <see cref="SafeName"/> like a JID does.
        ///
        /// The name is not made unique beyond the second. Two files arriving
        /// within the same second under the same name are the price; the
        /// alternative is a counter in the name, which makes every name a
        /// little unreadable to catch a case that needs two uploads inside one
        /// second.
        /// </remarks>
        /// <param name="ReceivedAt">When the file was fetched.</param>
        /// <param name="SuggestedName">What the URL called it, if anything.</param>
        /// <param name="Extension">
        /// The extension to append, e.g. ".jpg", or null to leave the suggested
        /// name as it is. A stored file is served by its extension, so whoever
        /// stores it passes the extension of what it actually is whenever the
        /// suggested name does not already carry one that says the same.
        /// </param>
        public static String MediaFileName(DateTimeOffset  ReceivedAt,
                                           String?         SuggestedName,
                                           String?         Extension   = null)
        {

            var name = SuggestedName is null || SuggestedName.Trim().Length == 0
                           ? "file"
                           : SafeName(SuggestedName);

            if (Extension is { Length: > 1 } extension)
                name += SafeName(extension.StartsWith('.') ? extension : "." + extension);

            return String.Concat(ReceivedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
                                 "_",
                                 name);

        }

        #endregion

    }

}
