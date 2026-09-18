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

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// The conversations of this account, in memory: one per far end, with the
    /// messages that went back and forth since the process started, the
    /// presence of the contact and how many lines nobody has read yet.
    /// </summary>
    /// <remarks>
    /// Nothing here survives a restart, and that is not a gap but the same
    /// choice XMPPConsole makes: the archive lives on the server (XEP-0313),
    /// which neither client speaks yet. What this store answers is the browser's
    /// question - what happened since the page was opened, or before, while the
    /// browser was away and the web app was not.
    ///
    /// Every change bumps <see cref="Sequence"/> and is raised as an event
    /// while the lock is still held, so that whoever forwards the events to a
    /// browser forwards them in the order they happened. A browser that loads a
    /// snapshot remembers its sequence and ignores every event that is older -
    /// which is what makes a replayed event stream harmless.
    /// </remarks>
    public sealed class ChatStore
    {

        #region (class) Conversation

        /// <summary>
        /// The mutable side of a chat; only ever touched under the lock.
        /// </summary>
        private sealed class Conversation(JID Jid)
        {

            public JID                 Jid              { get; }      = Jid;
            public String?             Name             { get; set; }
            public Boolean             InRoster         { get; set; }
            public SubscriptionState   Subscription     { get; set; } = SubscriptionState.None;
            public Boolean             PendingRequest   { get; set; }
            public PresenceState       Presence         { get; set; } = PresenceState.Offline;
            public String?             Status           { get; set; }
            public ChatState?          PeerChatState    { get; set; }
            public Int32               Unread           { get; set; }
            public DateTimeOffset?     LastActivity     { get; set; }
            public Boolean             EncryptionOn     { get; set; } = true;
            public String?             Avatar           { get; set; }
            public List<ChatMessage>   Messages         { get; }      = [];

            /// <summary>
            /// Whether anything in here ever travelled encrypted (XEP-0384).
            /// </summary>
            /// <remarks>
            /// Either direction counts. What this answers is "was this
            /// conversation ever protected", and a peer whose devices suddenly
            /// cannot be reached is the same suspicious event whichever way the
            /// last encrypted line went.
            /// </remarks>
            public Boolean WasEncrypted
                => Messages.Any(message => message.Encrypted);

            public ChatSummary Summary()

                => new (Jid,
                        Name,
                        InRoster,
                        Subscription,
                        PendingRequest,
                        Presence,
                        Status,
                        PeerChatState,
                        Unread,
                        Messages.Count > 0 ? Messages[^1] : null,
                        LastActivity,
                        EncryptionOn,
                        Avatar);

        }

        #endregion

        #region Data

        private readonly Dictionary<JID, Conversation>  chats  = [];

        /// <summary>
        /// The conversations somebody has expressly told this program to write
        /// in the clear.
        /// </summary>
        /// <remarks>
        /// Kept here rather than on the conversation, because it has to survive
        /// a conversation that does not exist yet: the setting is read off the
        /// disk at a start, and the chat it belongs to is only created when a
        /// message turns up in it.
        /// </remarks>
        private readonly HashSet<JID>                   plaintext  = [];

        private readonly Lock                           @lock  = new();
        private          Int64                          sequence;

        #endregion

        #region Properties

        /// <summary>
        /// How many messages a chat keeps before the oldest ones go.
        /// </summary>
        public Int32  MaxMessagesPerChat    { get; }

        /// <summary>
        /// Counts every change. A snapshot carries the value it was taken at,
        /// every event the value its change produced.
        /// </summary>
        public Int64  Sequence
            => Interlocked.Read(ref sequence);

        /// <summary>
        /// The number of conversations.
        /// </summary>
        public Int32  Count
        {
            get
            {
                lock (@lock)
                    return chats.Count;
            }
        }

        /// <summary>
        /// The number of received messages nobody has looked at yet, over all
        /// conversations.
        /// </summary>
        public Int32  Unread
        {
            get
            {
                lock (@lock)
                    return chats.Values.Sum(chat => chat.Unread);
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// A conversation changed: its contact, presence, unread count, or the
        /// far end started or stopped typing. Raised under the lock, in order.
        /// </summary>
        public event Action<Int64, ChatSummary>?  OnChatChanged;

        /// <summary>
        /// A message arrived, went out, was corrected or was confirmed. Raised
        /// under the lock, in order; the same id may come more than once.
        /// </summary>
        public event Action<Int64, ChatMessage>?  OnMessageChanged;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create a new, empty store.
        /// </summary>
        /// <param name="MaxMessagesPerChat">How many messages a chat keeps, 1000 by default.</param>
        public ChatStore(Int32 MaxMessagesPerChat = 1000)
        {

            if (MaxMessagesPerChat < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxMessagesPerChat), "A chat has to keep at least one message!");

            this.MaxMessagesPerChat = MaxMessagesPerChat;

        }

        #endregion


        #region Snapshot(out Chats)

        /// <summary>
        /// All conversations, the most recently active first, and the sequence
        /// number this list belongs to.
        /// </summary>
        public Int64 Snapshot(out IReadOnlyList<ChatSummary> Chats)
        {
            lock (@lock)
            {

                Chats = chats.Values.
                            Select (chat => chat.Summary()).
                            OrderByDescending(chat => chat.LastActivity ?? DateTimeOffset.MinValue).
                            ThenBy (chat => chat.DisplayName, StringComparer.OrdinalIgnoreCase).
                            ToList();

                return sequence;

            }
        }

        #endregion

        #region TrySnapshot(Chat, out Sequence, out Summary, out Messages)

        /// <summary>
        /// One conversation with its messages, oldest first, and the sequence
        /// number this snapshot belongs to.
        /// </summary>
        public Boolean TrySnapshot(JID                             Chat,
                                   out Int64                       Sequence,
                                   out ChatSummary?                Summary,
                                   out IReadOnlyList<ChatMessage>  Messages)
        {
            lock (@lock)
            {

                Sequence = sequence;

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                {
                    Summary   = null;
                    Messages  = [];
                    return false;
                }

                Summary   = conversation.Summary();
                Messages  = conversation.Messages.ToList();
                return true;

            }
        }

        #endregion

        #region Clear()

        /// <summary>
        /// Forget every conversation - the account changed, and what was said
        /// to the old one is nothing the new one should show. No event per
        /// chat; whoever forwards events tells the browsers to start over.
        /// </summary>
        public void Clear()
        {
            lock (@lock)
            {
                chats.Clear();
                sequence++;
            }
        }

        #endregion

        #region Open(Chat)

        /// <summary>
        /// The conversation with the given far end, started when there was
        /// none yet.
        /// </summary>
        public ChatSummary Open(JID Chat)
        {
            lock (@lock)
            {

                if (chats.TryGetValue(Chat.Bare, out var existing))
                    return existing.Summary();

                return Changed(GetOrAdd(Chat.Bare));

            }
        }

        #endregion


        #region AddIncoming(Chat, From, MessageId, Body, Timestamp, ...)

        /// <summary>
        /// A message that arrived - or a correction of one that did (XEP-0308).
        /// </summary>
        /// <param name="Chat">The far end of the conversation.</param>
        /// <param name="From">The full JID it came from.</param>
        /// <param name="MessageId">The stanza id, if the sender set one.</param>
        /// <param name="Body">The text.</param>
        /// <param name="Timestamp">When it was written.</param>
        /// <param name="Delayed">Whether it was handed in late (XEP-0203).</param>
        /// <param name="Carbon">Whether another device of our own received it (XEP-0280).</param>
        /// <param name="Corrects">XEP-0308: the id of the message this one replaces.</param>
        /// <param name="Identity">XEP-0384: how the sending device's key stood, for a line that arrived encrypted; null for one that did not.</param>
        /// <returns>The line as it now stands - the corrected one when there was something to correct.</returns>
        public ChatMessage AddIncoming(JID                  Chat,
                                       String               From,
                                       String?              MessageId,
                                       String               Body,
                                       DateTimeOffset       Timestamp,
                                       Boolean              Delayed    = false,
                                       Boolean              Carbon     = false,
                                       String?              Corrects   = null,
                                       OmemoIdentityCheck?  Identity   = null,
                                       String?              RepliesTo  = null,
                                       String?              Quote      = null,
                                       Boolean              Archived   = false)
        {
            lock (@lock)
            {

                var conversation = GetOrAdd(Chat.Bare);

                // A correction replaces the text of the line it names and
                // nothing else: the line keeps its place and its time.
                if (Corrects is not null)
                {

                    var index = conversation.Messages.FindIndex(message => message.Id        == Corrects &&
                                                                           message.Direction == MessageDirection.Incoming);

                    if (index >= 0)
                    {

                        // The identity travels with the text and not with the
                        // line: what is displayed now arrived now. A plaintext
                        // correction of an encrypted message that kept the lock
                        // would draw it over words that came in the clear.
                        var corrected = conversation.Messages[index] with {
                                            Body       = Body,
                                            Corrected  = true,
                                            Identity   = Identity
                                        };

                        conversation.Messages[index]  = corrected;
                        conversation.LastActivity     = Max(conversation.LastActivity, Timestamp);
                        conversation.PeerChatState    = null;

                        Changed(conversation);
                        return Changed(corrected);

                    }

                }

                // The same stanza twice - a stream that was resumed can do that.
                if (MessageId is not null &&
                    conversation.Messages.FirstOrDefault(message => message.Id == MessageId) is ChatMessage known)
                {
                    return known;
                }

                var message = new ChatMessage(
                                  MessageId ?? NewId(),
                                  conversation.Jid,
                                  MessageDirection.Incoming,
                                  From,
                                  Body,
                                  Timestamp,
                                  Delayed,
                                  Carbon,
                                  Corrects,
                                  RepliesTo,
                                  Quote
                              ) {
                                  Identity = Identity
                              };

                Insert(conversation, message);

                // XEP-0313: something filled in from an archive is not something
                // that just arrived. Counting it unread would put a badge on a
                // conversation for messages somebody read a year ago on another
                // device - which is the same mistake as letting a result travel
                // as a message, one layer up and in front of a person.
                if (!Archived)
                {
                    conversation.Unread++;
                    conversation.PeerChatState = null;
                }

                conversation.LastActivity   = Max(conversation.LastActivity, Timestamp);

                Changed(conversation);
                return Changed(message);

            }
        }

        #endregion

        #region AddOutgoing(Chat, MessageId, Body, Timestamp, Carbon = false, Identity = null)

        /// <summary>
        /// A message that went out - from here, or from another device of our
        /// own whose carbon we received.
        /// </summary>
        /// <remarks>
        /// <paramref name="Identity"/> is not this application encrypting: it
        /// sends in the clear. It is the carbon of another device of our own
        /// that did, and the line says so rather than looking like one of ours.
        /// </remarks>
        /// <summary>
        /// XEP-0308: replaces the text of the last line this app sent here.
        /// </summary>
        /// <returns>
        /// The corrected line, or null when nothing has gone out here yet.
        /// </returns>
        /// <remarks>
        /// <b>The line keeps its place and its time.</b> A correction is not a
        /// new thing said later; it is the same thing said properly, and moving
        /// it to the bottom would put it after answers to it.
        ///
        /// The id changes, because the correction is a stanza of its own and is
        /// what a further correction has to name. Whoever mistypes also
        /// mistypes in the correction.
        /// </remarks>
        public ChatMessage? CorrectLastOutgoing(JID     Chat,
                                                String  MessageId,
                                                String  Body)
        {
            lock (@lock)
            {

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                    return null;

                var index = conversation.Messages.FindLastIndex(
                                message => message.Direction == MessageDirection.Outgoing);

                if (index < 0)
                    return null;

                var corrected = conversation.Messages[index] with {
                                    Id         = MessageId,
                                    Body       = Body,
                                    Corrected  = true
                                };

                conversation.Messages[index] = corrected;

                Changed(conversation);
                return Changed(corrected);

            }
        }

        public ChatMessage AddOutgoing(JID                  Chat,
                                       String               MessageId,
                                       String               Body,
                                       DateTimeOffset       Timestamp,
                                       Boolean              Carbon     = false,
                                       OmemoIdentityCheck?  Identity   = null)
        {
            lock (@lock)
            {

                var conversation = GetOrAdd(Chat.Bare);

                if (conversation.Messages.FirstOrDefault(message => message.Id == MessageId) is ChatMessage known)
                    return known;

                var message = new ChatMessage(
                                  MessageId,
                                  conversation.Jid,
                                  MessageDirection.Outgoing,
                                  "me",
                                  Body,
                                  Timestamp,
                                  Carbon:  Carbon
                              ) {
                                  Identity = Identity
                              };

                Insert(conversation, message);

                conversation.LastActivity = Max(conversation.LastActivity, Timestamp);

                Changed(conversation);
                return Changed(message);

            }
        }

        #endregion

        #region SetEncryption(Chat, On) / EncryptionOn(Chat) / WasEncrypted(Chat)

        /// <summary>
        /// Turn OMEMO off for one conversation, or back on.
        /// </summary>
        /// <remarks>
        /// <b>Off is a decision somebody made, and it outranks everything this
        /// program would work out for itself</b> - including the refusal to
        /// fall back to plaintext in a conversation that has been encrypted
        /// before. There are clients out there whose OMEMO is broken in ways no
        /// amount of correctness on this side repairs, and the answer to those
        /// is a switch rather than an unreachable contact.
        /// </remarks>
        /// <returns>The state it now has.</returns>
        public Boolean SetEncryption(JID      Chat,
                                     Boolean  On)
        {
            lock (@lock)
            {

                if (On)
                    plaintext.Remove(Chat.Bare);
                else
                    plaintext.Add(Chat.Bare);

                if (chats.TryGetValue(Chat.Bare, out var conversation))
                {
                    conversation.EncryptionOn = On;
                    Changed(conversation);
                }

                return On;

            }
        }

        /// <summary>
        /// Whether this program may encrypt to this conversation at all.
        /// </summary>
        public Boolean EncryptionOn(JID Chat)
        {
            lock (@lock)
                return !plaintext.Contains(Chat.Bare);
        }

        /// <summary>
        /// The conversations encryption is currently turned off for.
        /// </summary>
        public IReadOnlyCollection<JID> PlaintextConversations()
        {
            lock (@lock)
                return [.. plaintext];
        }

        /// <summary>
        /// Whether anything in this conversation ever travelled encrypted.
        /// </summary>
        /// <remarks>
        /// What it is for: a conversation that was encrypted and suddenly
        /// cannot be any more is the shape of somebody having taken the
        /// recipient's device list out of the way. Falling back to plaintext
        /// there would hand them exactly what they were after.
        /// </remarks>
        public Boolean WasEncrypted(JID Chat)
        {
            lock (@lock)
                return chats.TryGetValue(Chat.Bare, out var conversation) &&
                       conversation.WasEncrypted;
        }

        #endregion

        #region LoadHistory(Chat, Messages)

        /// <summary>
        /// Puts what the archive kept back into a conversation: the messages of
        /// the recent past, at a start or after an account change.
        /// </summary>
        /// <remarks>
        /// Two things it deliberately does not do. It counts nothing as unread
        /// - what was archived was seen, or at least was not seen by this
        /// browser session, and a badge saying "412 new" after a restart is
        /// worse than no badge. And it raises no event per message: the one
        /// event for the conversation is enough, because a browser loads the
        /// messages of a chat as a snapshot anyway, and the message events
        /// would otherwise travel back into the archive they came from.
        /// </remarks>
        /// <param name="Chat">The far end of the conversation.</param>
        /// <param name="Messages">What was archived, oldest first.</param>
        /// <returns>The conversation as it now stands.</returns>
        public ChatSummary LoadHistory(JID                       Chat,
                                       IEnumerable<ChatMessage>  Messages)
        {
            lock (@lock)
            {

                var conversation  = GetOrAdd(Chat.Bare);
                var known         = conversation.Messages.Select(message => message.Id).ToHashSet();
                var added         = 0;

                foreach (var message in Messages)
                {

                    // What is already here came through the live connection and
                    // is the same message; the archive does not get to say it
                    // twice.
                    if (!known.Add(message.Id))
                        continue;

                    Insert(conversation, message with { Chat = conversation.Jid });
                    added++;

                }

                if (added > 0)
                    conversation.LastActivity = Max(conversation.LastActivity,
                                                    conversation.Messages[^1].Timestamp);

                return Changed(conversation);

            }
        }

        #endregion

        #region AttachMedia(Chat, MessageId, Media)

        /// <summary>
        /// The file a message handed over has been fetched and now lies beside
        /// the conversation.
        /// </summary>
        /// <returns>The message, or null when this conversation holds none with this id.</returns>
        public ChatMessage? AttachMedia(JID       Chat,
                                        String    MessageId,
                                        MediaRef  Media)

            => Mark(Chat,
                    MessageId,
                    message => message.Media is not null
                                   ? message
                                   : message with { Media = Media },
                    Direction: null);

        #endregion

        #region MarkDelivered(Chat, MessageId) / MarkDisplayed(Chat, MessageId)

        /// <summary>
        /// The far end confirmed a sent message (XEP-0184, or the "received"
        /// marker of XEP-0333).
        /// </summary>
        /// <returns>The message, or null when no sent message carries this id.</returns>
        public ChatMessage? MarkDelivered(JID Chat, String MessageId)
            => Mark(Chat, MessageId, message => message.Delivered ? message : message with { Delivered = true });

        /// <summary>
        /// The far end displayed a sent message (XEP-0333).
        /// </summary>
        /// <returns>The message, or null when no sent message carries this id.</returns>
        public ChatMessage? MarkDisplayed(JID Chat, String MessageId)
            => Mark(Chat, MessageId, message => message.Displayed ? message : message with { Delivered = true, Displayed = true });

        #endregion

        #region MarkRead(Chat)

        /// <summary>
        /// Somebody looked at the conversation: nothing in it is unread any more.
        /// </summary>
        /// <returns>The conversation, or null when there is none with this far end.</returns>
        public ChatSummary? MarkRead(JID Chat)
        {
            lock (@lock)
            {

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                    return null;

                if (conversation.Unread == 0)
                    return conversation.Summary();

                conversation.Unread = 0;

                return Changed(conversation);

            }
        }

        #endregion


        #region SetContact(Chat, Name, Subscription)

        /// <summary>
        /// A roster entry: the far end is a contact with this name and this
        /// subscription, and the list shows it even before a word was said.
        /// </summary>
        public ChatSummary SetContact(JID                Chat,
                                      String?            Name,
                                      SubscriptionState  Subscription)
        {
            lock (@lock)
            {

                var conversation = GetOrAdd(Chat.Bare);

                if (conversation.InRoster &&
                    conversation.Name         == Name &&
                    conversation.Subscription == Subscription)
                {
                    return conversation.Summary();
                }

                conversation.InRoster      = true;
                conversation.Name          = Name;
                conversation.Subscription  = Subscription;

                return Changed(conversation);

            }
        }

        #endregion

        #region RemoveContact(Chat)

        /// <summary>
        /// The roster entry is gone. The conversation stays - what was said
        /// was said - but the far end is offline as far as this side can tell.
        /// </summary>
        public ChatSummary? RemoveContact(JID Chat)
        {
            lock (@lock)
            {

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                    return null;

                conversation.InRoster      = false;
                conversation.Name          = null;
                conversation.Subscription  = SubscriptionState.None;
                conversation.Presence      = PresenceState.Offline;
                conversation.Status        = null;

                return Changed(conversation);

            }
        }

        #endregion

        #region SetPendingRequest(Chat, Pending)

        /// <summary>
        /// The far end asked to become a contact (or the request was answered).
        /// A new request opens the conversation, so that it shows up in the list.
        /// </summary>
        public ChatSummary? SetPendingRequest(JID      Chat,
                                              Boolean  Pending)
        {
            lock (@lock)
            {

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                {

                    if (!Pending)
                        return null;

                    conversation = GetOrAdd(Chat.Bare);

                }

                if (conversation.PendingRequest == Pending)
                    return conversation.Summary();

                conversation.PendingRequest = Pending;

                if (Pending)
                    conversation.LastActivity = Max(conversation.LastActivity, DateTimeOffset.UtcNow);

                return Changed(conversation);

            }
        }

        #endregion

        #region SetPresence(Chat, Presence, Status)

        /// <summary>
        /// How a contact is. Only for conversations that exist: a stranger's
        /// presence is nothing the list has to show.
        /// </summary>
        public ChatSummary? SetPresence(JID            Chat,
                                        PresenceState  Presence,
                                        String?        Status)
        {
            lock (@lock)
            {

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                    return null;

                if (conversation.Presence == Presence && conversation.Status == Status)
                    return conversation.Summary();

                conversation.Presence  = Presence;
                conversation.Status    = Status;

                if (Presence == PresenceState.Offline)
                    conversation.PeerChatState = null;

                return Changed(conversation);

            }
        }

        #endregion

        #region SetPeerChatState(Chat, State)

        /// <summary>
        /// XEP-0085: the far end is typing, paused, or gone. Only for
        /// conversations that exist.
        /// </summary>
        public ChatSummary? SetPeerChatState(JID         Chat,
                                             ChatState?  State)
        {
            lock (@lock)
            {

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                    return null;

                if (conversation.PeerChatState == State)
                    return conversation.Summary();

                conversation.PeerChatState = State;

                return Changed(conversation);

            }
        }

        #endregion

        #region SetAvatar(Chat, Id)

        /// <summary>
        /// XEP-0084: this contact's picture is the one with this id, or none.
        /// </summary>
        /// <remarks>
        /// <b>Only for conversations that exist</b>, like presence and the chat
        /// state above it. Every contact has one - <see cref="SetContact"/>
        /// makes it as the roster arrives - so what this refuses is a stranger:
        /// an announcement is supposed to follow a presence subscription, but
        /// that is the far server's discipline and not ours, and a picture from
        /// somebody nobody knows must not put a row in the list.
        /// </remarks>
        public ChatSummary? SetAvatar(JID      Chat,
                                      String?  Id)
        {
            lock (@lock)
            {

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                    return null;

                if (conversation.Avatar == Id)
                    return conversation.Summary();

                conversation.Avatar = Id;

                return Changed(conversation);

            }
        }

        #endregion


        #region (private) GetOrAdd(BareJid)

        private Conversation GetOrAdd(JID BareJid)
        {

            if (!chats.TryGetValue(BareJid, out var conversation))
            {

                conversation = new Conversation(BareJid) {
                                   EncryptionOn = !plaintext.Contains(BareJid)
                               };

                chats.Add(BareJid, conversation);

            }

            return conversation;

        }

        #endregion

        #region (private) Insert(Conversation, Message)

        /// <summary>
        /// Appends, unless the message is older than the last one - a message
        /// handed in late belongs where it was written, not where it arrived.
        /// </summary>
        private void Insert(Conversation  Conversation,
                            ChatMessage   Message)
        {

            var messages  = Conversation.Messages;
            var index     = messages.Count;

            while (index > 0 && messages[index - 1].Timestamp > Message.Timestamp)
                index--;

            messages.Insert(index, Message);

            while (messages.Count > MaxMessagesPerChat)
                messages.RemoveAt(0);

        }

        #endregion

        #region (private) Mark(Chat, MessageId, Update, Direction = Outgoing)

        /// <summary>
        /// Replaces one message of a conversation by what
        /// <paramref name="Update"/> makes of it.
        /// </summary>
        /// <param name="Direction">
        /// Which way the message went, or null for either. A receipt may only
        /// ever reach a sent message - the far end confirming what it sent
        /// itself would be nonsense - while a fetched file belongs to whichever
        /// message handed it over.
        /// </param>
        private ChatMessage? Mark(JID                             Chat,
                                  String                          MessageId,
                                  Func<ChatMessage, ChatMessage>  Update,
                                  MessageDirection?               Direction   = MessageDirection.Outgoing)
        {
            lock (@lock)
            {

                if (!chats.TryGetValue(Chat.Bare, out var conversation))
                    return null;

                var index = conversation.Messages.FindIndex(message => message.Id == MessageId &&
                                                                       (Direction is null || message.Direction == Direction));

                if (index < 0)
                    return null;

                var before  = conversation.Messages[index];
                var after   = Update(before);

                if (ReferenceEquals(before, after))
                    return before;

                conversation.Messages[index] = after;

                return Changed(after);

            }
        }

        #endregion

        #region (private) Changed(Conversation) / Changed(Message)

        private ChatSummary Changed(Conversation Conversation)
        {

            var summary = Conversation.Summary();

            OnChatChanged?.Invoke(++sequence, summary);

            return summary;

        }

        private ChatMessage Changed(ChatMessage Message)
        {

            OnMessageChanged?.Invoke(++sequence, Message);

            return Message;

        }

        #endregion

        #region (private static) Max(A, B) / NewId()

        private static DateTimeOffset Max(DateTimeOffset?  A,
                                          DateTimeOffset   B)

            => A.HasValue && A.Value > B
                   ? A.Value
                   : B;

        private static String NewId()
            => Guid.NewGuid().ToString("N");

        #endregion

    }

}
