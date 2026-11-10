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

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// Which messages this side fetches a file for, before anybody has asked
    /// to see it.
    /// </summary>
    /// <remarks>
    /// <b>Every one of these is a request a stranger can set off by writing to
    /// you.</b> That is the whole of the question: the fetch tells whoever
    /// hosts the address that the message arrived, roughly when, and from
    /// which address - before the reader has so much as looked at it. A
    /// one-pixel image is the oldest read receipt there is, and nobody has to
    /// consent to it.
    ///
    /// Against that stands what the fetching is for. A body that is nothing
    /// but a link is how most clients hand a file over, and plenty of upload
    /// services name the file nowhere in the address:
    /// <c>https://trillian.media/media/?q=2vhTXC...</c> answers
    /// <c>image/jpeg</c> and says so in no other way. Asking is the only way
    /// to find out, and not asking means a picture somebody sent shows up as a
    /// line of letters.
    ///
    /// So it is a setting and not a rule, and the middle one is the default
    /// because it separates the two cases by the only thing this side actually
    /// knows about them: whether the sender is somebody already written to.
    ///
    /// <see cref="MediaStore"/> checks what comes back whatever is set here,
    /// and a <c>.png</c> that arrives as <c>text/html</c> is still refused.
    /// None of these values is a decision about what is kept - only about what
    /// is asked for.
    /// </remarks>
    public enum MediaPrefetch
    {

        /// <summary>
        /// Nothing is fetched because a message arrived - not a declared file
        /// either.
        /// </summary>
        /// <remarks>
        /// <b>The price is named rather than hidden:</b> a file handed over
        /// per XEP-0363 stays a link, and one shared per XEP-0454 cannot be
        /// seen at all, because an <c>aesgcm://</c> address is encrypted and
        /// no browser shows one. What is gained is that an incoming message
        /// causes no traffic whatsoever.
        /// </remarks>
        Never,

        /// <summary>
        /// A declared file always, and a link that is a whole message only
        /// from a contact in the roster.
        /// </summary>
        /// <remarks>
        /// The default. Somebody in the roster already knows this address
        /// exists and already knows when it is written to - the fetch tells
        /// their upload service something they could have learnt from a
        /// delivery receipt. A stranger learns something new, and the first
        /// message of a conversation is exactly where a read receipt is worth
        /// forging.
        /// </remarks>
        RosterChats,

        /// <summary>
        /// Any message whose whole body is one https address is tried.
        /// </summary>
        /// <remarks>
        /// For whoever would rather see every picture than withhold the fact
        /// that a message arrived. It does not fetch links inside a sentence,
        /// which is the one line this never crosses: a link in a sentence is
        /// somebody pointing at something, not handing it over.
        /// </remarks>
        Always

    }

}
