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

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// What a browser may type and an XML stream may not carry.
    /// </summary>
    /// <remarks>
    /// Ratatoskr escapes the five characters XML gives a meaning to, and that
    /// is all a library should do with a body. But a text field in a browser
    /// accepts more than XML 1.0 allows in a document: a pasted NUL, a form
    /// feed, a stray C0 control character. Sent as they are they do not become
    /// an escaped character, they become a stream error - and a stream error
    /// ends the connection for everybody, not only for the line that carried
    /// it. So they are dropped here, before the body is handed down.
    ///
    /// Everything else stays: every letter of every script, every emoji, the
    /// line break, the tab. Unicode is the point of a chat, not a risk to it.
    /// </remarks>
    public static class XmlText
    {

        #region Sanitize(Text)

        /// <summary>
        /// The text without the characters XML 1.0 forbids (section 2.2):
        /// everything below U+0020 except tab, line feed and carriage return,
        /// the surrogate halves without a partner, and U+FFFE and U+FFFF.
        /// </summary>
        public static String Sanitize(String Text)
        {

            if (Text.All(IsAllowedUtf16Unit))
                return Text;

            var builder = new StringBuilder(Text.Length);

            for (var i = 0; i < Text.Length; i++)
            {

                var c = Text[i];

                if (Char.IsHighSurrogate(c) && i + 1 < Text.Length && Char.IsLowSurrogate(Text[i + 1]))
                {
                    builder.Append(c).Append(Text[i + 1]);
                    i++;
                }

                else if (!Char.IsSurrogate(c) && IsAllowedUtf16Unit(c))
                    builder.Append(c);

            }

            return builder.ToString();

        }

        #endregion

        #region (private) IsAllowedUtf16Unit(Character)

        /// <summary>
        /// A quick first pass over single code units: surrogates count as
        /// allowed here and are paired up properly in the slow pass.
        /// </summary>
        private static Boolean IsAllowedUtf16Unit(Char Character)

            => Character switch {
                   '\t' or '\n' or '\r'  => true,
                   < ' '                 => false,
                   '￾' or '￿'  => false,
                   _                     => true
               };

        #endregion

    }

}
