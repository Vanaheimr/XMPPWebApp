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

using System.Text.RegularExpressions;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// Which link in a message body is a shared file, and which is merely a
    /// link.
    /// </summary>
    /// <remarks>
    /// XMPP has an element for this - <c>&lt;x xmlns='jabber:x:oob'&gt;</c>,
    /// XEP-0066 - and Ratatoskr does not read it. So the body is all there is,
    /// and the body does not say what it means. Two rules, and the reason for
    /// each:
    ///
    /// <b>aesgcm:// always.</b> That scheme exists for nothing but a shared
    /// file (XEP-0454): the URL carries the key to decrypt it. Nobody writes
    /// one in passing - and a browser cannot show one at all, so fetching it
    /// is the only way it is ever seen.
    ///
    /// <b>https:// only when the body is the URL and nothing else.</b> That is
    /// how a client sends an upload per XEP-0363 - the body repeats the URL
    /// precisely because the receiver may not read the OOB element. A link
    /// inside a sentence is a different act: somebody is pointing at something,
    /// not handing it over. Fetching those would turn every news article
    /// anybody mentions into a download, and would let whoever may write to us
    /// decide what this machine fetches.
    ///
    /// It is the same rule the browser side applies in chat/links.ts when it
    /// decides whether a body is a picture, and deliberately so: what is shown
    /// inline and what is kept are the same thing.
    ///
    /// At most one file per message is taken, the first. A body is one URL or
    /// it is not; a body with several is a sentence.
    /// </remarks>
    public static class MediaLinks
    {

        #region Data

        /// <summary>
        /// Recognises a URL of one of the two schemes. Deliberately not a
        /// general URL pattern: what is not http, https or aesgcm is not
        /// fetched anyway.
        /// </summary>
        private static readonly Regex urlPattern =
            new (@"\b(?:aesgcm|https?)://[^\s<>""]+",
                 RegexOptions.Compiled | RegexOptions.IgnoreCase,
                 TimeSpan.FromSeconds(1));

        #endregion


        #region Detect(Body)

        /// <summary>
        /// The file this body hands over, or null when it hands over none.
        /// </summary>
        public static Uri? Detect(String? Body)
        {

            if (Body is null)
                return null;

            var trimmed = Body.Trim();

            if (trimmed.Length == 0)
                return null;

            MatchCollection matches;

            try
            {
                matches = urlPattern.Matches(trimmed);
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }

            if (matches.Count == 0)
                return null;

            // The whole body is one URL and nothing else: a handed-over file.
            var bodyIsNothingButOneUrl = matches.Count == 1 &&
                                         matches[0].Length == trimmed.Length;

            foreach (Match match in matches)
            {

                var text = TrimTrailingPunctuation(match.Value);

                if (text.Length == 0 ||
                    !Uri.TryCreate(text, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                if (AesGcmUrl.IsAesGcmUrl(uri))
                    return uri;

                if (!bodyIsNothingButOneUrl)
                    continue;

                // Plain http is not fetched. The file would travel readable,
                // and a shared file is nothing this side wants to make more
                // public than the sender did.
                if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    return uri;

            }

            return null;

        }

        #endregion

        #region TrimTrailingPunctuation(Text)

        /// <summary>
        /// Drops the punctuation a sentence leaves clinging to its last URL.
        /// </summary>
        /// <remarks>
        /// "look at https://example.org/a.jpg." ends in a full stop that is not
        /// part of the address. A closing bracket only counts as punctuation
        /// when no opening one precedes it in the URL, because
        /// "https://example.org/a_(b).jpg" is a perfectly ordinary address.
        /// </remarks>
        internal static String TrimTrailingPunctuation(String Text)
        {

            var end = Text.Length;

            while (end > 0)
            {

                var last = Text[end - 1];

                if (last is '.' or ',' or ';' or ':' or '!' or '?' or '"' or '\'')
                    end--;

                else if (last == ')' && Text[..(end - 1)].Count(c => c == '(') <
                                        Text[..end].     Count(c => c == ')'))
                    end--;

                else
                    break;

            }

            return Text[..end];

        }

        #endregion

    }

}
