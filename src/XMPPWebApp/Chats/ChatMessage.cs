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

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// Which way a message went.
    /// </summary>
    public enum MessageDirection
    {
        Incoming,
        Outgoing
    }


    /// <summary>
    /// A file that was shared in a conversation and now lies beside it.
    /// </summary>
    /// <remarks>
    /// The URL is not part of this: it is built by whoever shows the file, from
    /// the conversation and the name. The archive would otherwise carry an API
    /// path of this program in every line, which is the one thing in here that
    /// is nobody's business a year from now.
    /// </remarks>
    /// <param name="Name">The file name below the media directory of the conversation.</param>
    /// <param name="ContentType">What the server said it is - and what it is served as.</param>
    /// <param name="Size">Its size in bytes.</param>
    /// <param name="Source">Where it came from; for an encrypted upload the aesgcm:// URL it was named by.</param>
    public sealed record MediaRef(String   Name,
                                  String   ContentType,
                                  Int64    Size,
                                  String?  Source   = null)
    {

        public JObject ToJSON()

            => new (
                   new JProperty("name",         Name),
                   new JProperty("contentType",  ContentType),
                   new JProperty("size",         Size),
                   new JProperty("source",       Source)
               );


        /// <summary>
        /// A media reference from an archive line, or null when the line has
        /// none or an unusable one. A name that is not a name is dropped rather
        /// than refused: the message around it is still worth showing.
        /// </summary>
        public static MediaRef? TryParse(JToken? JSON)
        {

            if (JSON is not JObject json)
                return null;

            var name = json.Value<String>("name");

            if (!ChatArchivePaths.IsSafeName(name))
                return null;

            return new MediaRef(
                       name,
                       json.Value<String>("contentType") ?? "application/octet-stream",
                       json.Value<Int64?>("size")        ?? 0,
                       json.Value<String>("source")
                   );

        }

    }


    /// <summary>
    /// One line of a conversation, as the browser gets to see it.
    /// </summary>
    /// <param name="Id">
    /// The stanza id when the sender set one, otherwise one made up here. Unique
    /// within the chat; the browser uses it to replace a line rather than to
    /// show it twice.
    /// </param>
    /// <param name="Chat">The far end of the conversation, a bare JID.</param>
    /// <param name="Direction">Received or sent.</param>
    /// <param name="From">Who wrote it: the full JID of the sender, or "me".</param>
    /// <param name="Body">
    /// The text, as the browser gets it to escape.
    ///
    /// <b>Without the quoted lines of a reply</b> (XEP-0461/XEP-0428). An
    /// answer carries the text it answers a second time, as <c>&gt; </c> lines,
    /// so that a client which cannot follow the reference still shows what it
    /// is about. This one can follow it, and storing both would put the same
    /// sentence in the archive twice - once as somebody's line and once inside
    /// the answer to it.
    /// </param>
    /// <param name="Timestamp">When it was written, per XEP-0203 when it was handed in late.</param>
    /// <param name="Delayed">Whether it was held somewhere on the way.</param>
    /// <param name="Carbon">Whether it was mirrored from another device of our own (XEP-0280).</param>
    /// <param name="Corrects">XEP-0308: the id of the message this one replaces, when it is a correction that found nothing to replace.</param>
    /// <param name="RepliesTo">
    /// XEP-0461: the id of the message this one answers, or null.
    /// </param>
    /// <param name="Quote">
    /// The quoted lines that came with it, taken out of <paramref name="Body"/>
    /// and kept here.
    ///
    /// <b>Kept rather than dropped</b>, because they are sometimes the only
    /// copy of what is being answered: the message they point at may have been
    /// written before this archive existed, or on a device whose history never
    /// reached here.
    /// </param>
    public sealed record ChatMessage(String            Id,
                                     JID               Chat,
                                     MessageDirection  Direction,
                                     String            From,
                                     String            Body,
                                     DateTimeOffset    Timestamp,
                                     Boolean           Delayed    = false,
                                     Boolean           Carbon     = false,
                                     String?           Corrects   = null,
                                     String?           RepliesTo  = null,
                                     String?           Quote      = null)
    {

        /// <summary>
        /// Whether a later correction replaced the body (XEP-0308).
        /// </summary>
        public Boolean  Corrected    { get; init; }

        /// <summary>
        /// Whether the far end confirmed the delivery (XEP-0184) or the
        /// reception (XEP-0333) of a sent message.
        /// </summary>
        public Boolean  Delivered    { get; init; }

        /// <summary>
        /// Whether the far end displayed a sent message (XEP-0333).
        /// </summary>
        public Boolean   Displayed    { get; init; }

        /// <summary>
        /// The file this message handed over, once it has been fetched and put
        /// beside the conversation; null while it has not been, and for every
        /// message that is only text.
        /// </summary>
        public MediaRef? Media        { get; init; }

        /// <summary>
        /// XEP-0384: how the sending device's identity key stood when this
        /// line arrived - null for everything that did not arrive encrypted.
        /// </summary>
        /// <remarks>
        /// <b>This is the whole of what blind trust leaves to look at.</b> The
        /// first message from a device is accepted without anybody having
        /// compared a fingerprint, which is what makes the encryption get used
        /// at all; the price is that the first one could always have been
        /// somebody else.
        ///
        /// Kept per message and not per conversation, because that is where it
        /// is true: the state of a chat says nothing about the line above it,
        /// and a year later the archive still knows how this particular
        /// sentence arrived.
        ///
        /// <b>Only two of the three ever stand here.</b>
        /// <see cref="OmemoIdentityCheck.Changed"/> belongs to a message that
        /// was refused - a device reporting with a second key does not get a
        /// session, and a program cannot tell a new installation from somebody
        /// pushing in between - so there is no line for it to be on. That case
        /// arrives as a notice of its own, carrying both fingerprints. The
        /// value is still read and written, because an archive is read by
        /// whatever comes later and quietly dropping a value is how a format
        /// stops being one.
        /// </remarks>
        public OmemoIdentityCheck? Identity { get; init; }

        /// <summary>
        /// Whether this line travelled encrypted (XEP-0384).
        /// </summary>
        /// <remarks>
        /// Derived and not stored, so that the lock and the reason for it
        /// cannot come apart: a line claiming to be encrypted while naming no
        /// device key would be a lock drawn over nothing.
        /// </remarks>
        public Boolean Encrypted
            => Identity.HasValue;


        public JObject ToJSON()

            => new (
                   new JProperty("id",         Id),
                   new JProperty("chat",       Chat.ToString()),
                   new JProperty("direction",  Direction == MessageDirection.Incoming ? "in" : "out"),
                   new JProperty("from",       From),
                   new JProperty("body",       Body),
                   new JProperty("timestamp",  Timestamp.ToString("o")),
                   new JProperty("delayed",    Delayed),
                   new JProperty("carbon",     Carbon),
                   new JProperty("corrects",   Corrects),
                   new JProperty("corrected",  Corrected),
                   new JProperty("delivered",  Delivered),
                   new JProperty("displayed",  Displayed),
                   new JProperty("encrypted",  Encrypted),
                   new JProperty("identity",   Identity?.ToString().ToLowerInvariant()),
                   new JProperty("media",      Media?.ToJSON())
               );


        #region TryParse(JSON, Chat, out Message)

        /// <summary>
        /// One line of the archive, read back. The same JSON the browser gets:
        /// what is written and what is shown are one format, so that a message
        /// read from disk a year later is the message that was shown.
        /// </summary>
        /// <param name="JSON">The parsed line.</param>
        /// <param name="Chat">
        /// The conversation the file belongs to. It is used when the line does
        /// not name one, and it wins over a line that names a different one:
        /// the directory is this side's own doing, the content of the line was
        /// once a stranger's.
        /// </param>
        public static Boolean TryParse(JObject                           JSON,
                                       JID                               Chat,
                                       [NotNullWhen(true)] out ChatMessage?  Message)
        {

            Message = null;

            var id = JSON.Value<String>("id");

            if (String.IsNullOrEmpty(id))
                return false;

            if (!DateTimeOffset.TryParse(JSON.Value<String>("timestamp"),
                                         System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.RoundtripKind,
                                         out var timestamp))
            {
                return false;
            }

            Message = new ChatMessage(
                          id,
                          Chat.Bare,
                          JSON.Value<String>("direction") == "out"
                              ? MessageDirection.Outgoing
                              : MessageDirection.Incoming,
                          JSON.Value<String>("from")     ?? "",
                          JSON.Value<String>("body")     ?? "",
                          timestamp,
                          JSON.Value<Boolean?>("delayed")   ?? false,
                          JSON.Value<Boolean?>("carbon")    ?? false,
                          JSON.Value<String>  ("corrects")
                      ) {
                          Corrected  = JSON.Value<Boolean?>("corrected") ?? false,
                          Delivered  = JSON.Value<Boolean?>("delivered") ?? false,
                          Displayed  = JSON.Value<Boolean?>("displayed") ?? false,
                          Identity   = TryParseIdentity(JSON.Value<String>("identity")),
                          Media      = MediaRef.TryParse(JSON["media"])
                      };

            return true;

        }

        #endregion

        #region (private, static) TryParseIdentity(Text)

        /// <summary>
        /// The identity check of an archive line, or null when the line names
        /// none, or names something this version does not know.
        /// </summary>
        /// <remarks>
        /// An unreadable value becomes "was not encrypted" rather than a guess.
        /// Both ways of being wrong are wrong, and only one of them tells
        /// somebody their conversation was protected when it was not.
        /// </remarks>
        private static OmemoIdentityCheck? TryParseIdentity(String? Text)

            => Enum.TryParse<OmemoIdentityCheck>(Text, ignoreCase: true, out var check) &&
               Enum.IsDefined(check)
                   ? check
                   : null;

        #endregion

    }


    /// <summary>
    /// A conversation as the list on the left shows it: who, how they are, and
    /// what was said last.
    /// </summary>
    /// <param name="Jid">The far end, a bare JID.</param>
    /// <param name="Name">The name from the roster, if the contact has one.</param>
    /// <param name="InRoster">Whether the far end is a contact at all.</param>
    /// <param name="Subscription">The subscription state of a contact (RFC 6121, section 2.1.2.5).</param>
    /// <param name="PendingRequest">Whether the far end asked to become a contact and nobody has answered yet.</param>
    /// <param name="Presence">How the contact is, offline for anybody outside the roster.</param>
    /// <param name="Status">The status text of the contact.</param>
    /// <param name="PeerChatState">XEP-0085: what the far end is doing right now, e.g. typing.</param>
    /// <param name="Unread">The number of received messages nobody has looked at yet.</param>
    /// <param name="LastMessage">The most recent line, for the preview.</param>
    /// <param name="LastActivity">When something last happened here; the list is sorted by it.</param>
    /// <param name="EncryptionOn">
    /// XEP-0384: whether this program may encrypt to this conversation at all -
    /// false when somebody turned it off here.
    /// </param>
    /// <param name="Avatar">
    /// XEP-0084: the id of this contact's picture, or null when they have none
    /// that this client kept.
    /// </param>
    public sealed record ChatSummary(JID                Jid,
                                     String?            Name,
                                     Boolean            InRoster,
                                     SubscriptionState  Subscription,
                                     Boolean            PendingRequest,
                                     PresenceState      Presence,
                                     String?            Status,
                                     ChatState?         PeerChatState,
                                     Int32              Unread,
                                     ChatMessage?       LastMessage,
                                     DateTimeOffset?    LastActivity,
                                     Boolean            EncryptionOn   = true,
                                     String?            Avatar         = null)
    {

        /// <summary>
        /// What to call the far end.
        /// </summary>
        public String DisplayName
            => Name ?? Jid.ToString();


        public JObject ToJSON()

            => new (
                   new JProperty("jid",             Jid.ToString()),
                   new JProperty("name",            Name),
                   new JProperty("displayName",     DisplayName),
                   new JProperty("inRoster",        InRoster),
                   new JProperty("subscription",    Subscription.ToString().ToLowerInvariant()),
                   new JProperty("pendingRequest",  PendingRequest),
                   new JProperty("presence",        Presence.    ToString().ToLowerInvariant()),
                   new JProperty("status",          Status),
                   new JProperty("chatState",       PeerChatState?.ToString().ToLowerInvariant()),
                   new JProperty("unread",          Unread),
                   new JProperty("lastMessage",     LastMessage?.ToJSON()),
                   new JProperty("lastActivity",    LastActivity?.ToString("o")),

                   // "auto" and not "on", because on is not what it does: it
                   // encrypts when the far end can read it and says per line
                   // what happened. Off is the only state somebody chose.
                   new JProperty("encryption",      EncryptionOn ? "auto" : "off"),

                   // XEP-0084: the id and not the picture. It is the SHA-1 of
                   // the bytes, so the address the browser builds out of it
                   // changes exactly when the face does - which is what lets
                   // that address be cached forever without ever being stale.
                   new JProperty("avatar",          Avatar)
               );

    }

}
