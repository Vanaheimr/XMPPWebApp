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

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// What a conversation keeps, in which order, and what it tells a browser
    /// about it.
    /// </summary>
    /// <remarks>
    /// The store is the one place where the XMPP side and the browser side
    /// meet, and its decisions are the ones a user sees: whether a corrected
    /// message replaces the old text or stands beside it, whether a late
    /// message lands where it was written, whether the unread badge is right.
    /// The sequence numbers are checked as well - they are what lets a browser
    /// tell a replayed event from a new one.
    /// </remarks>
    [TestFixture]
    public class ChatStoreTests
    {

        private static readonly JID             alice  = JID.Parse("alice@example.org");
        private static readonly JID             bob    = JID.Parse("bob@example.org");
        private static readonly DateTimeOffset  noon   = new (2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

        #region AnIncomingMessage_OpensTheChatAndCountsAsUnread()

        [Test]
        public void AnIncomingMessage_OpensTheChatAndCountsAsUnread()
        {

            var store    = new ChatStore();
            var message  = store.AddIncoming(alice, "alice@example.org/phone", "m1", "Hi 👋", noon);

            store.Snapshot(out var chats);

            Assert.Multiple(() =>
            {
                Assert.That(message.Direction,          Is.EqualTo(MessageDirection.Incoming));
                Assert.That(message.Id,                 Is.EqualTo("m1"));
                Assert.That(chats,                      Has.Count.EqualTo(1));
                Assert.That(chats[0].Jid,               Is.EqualTo(alice));
                Assert.That(chats[0].Unread,            Is.EqualTo(1));
                Assert.That(chats[0].LastMessage?.Body, Is.EqualTo("Hi 👋"));
                Assert.That(chats[0].LastActivity,      Is.EqualTo(noon));
                Assert.That(store.Unread,               Is.EqualTo(1));
            });

        }

        #endregion

        #region AMessageFromAFullJid_LandsInTheChatOfTheBareJid()

        [Test]
        public void AMessageFromAFullJid_LandsInTheChatOfTheBareJid()
        {

            var store = new ChatStore();

            store.AddIncoming(JID.Parse("alice@example.org/phone"),  "alice@example.org/phone",  "m1", "one", noon);
            store.AddIncoming(JID.Parse("alice@example.org/laptop"), "alice@example.org/laptop", "m2", "two", noon.AddMinutes(1));

            Assert.That(store.Count, Is.EqualTo(1));
            Assert.That(store.TrySnapshot(alice, out _, out _, out var messages), Is.True);
            Assert.That(messages, Has.Count.EqualTo(2));

        }

        #endregion

        #region MarkRead_ResetsTheUnreadCount()

        [Test]
        public void MarkRead_ResetsTheUnreadCount()
        {

            var store = new ChatStore();

            store.AddIncoming(alice, "alice@example.org/phone", "m1", "one", noon);
            store.AddIncoming(alice, "alice@example.org/phone", "m2", "two", noon.AddSeconds(1));

            Assert.That(store.MarkRead(alice)?.Unread, Is.EqualTo(0));
            Assert.That(store.MarkRead(bob),           Is.Null, "no chat with Bob, nothing to mark");

        }

        #endregion

        #region TheSameStanzaTwice_IsKeptOnce()

        /// <summary>
        /// A resumed stream can deliver a stanza again.
        /// </summary>
        [Test]
        public void TheSameStanzaTwice_IsKeptOnce()
        {

            var store = new ChatStore();

            store.AddIncoming(alice, "alice@example.org/phone", "m1", "one", noon);
            store.AddIncoming(alice, "alice@example.org/phone", "m1", "one", noon);
            store.AddOutgoing(alice, "o1", "reply", noon.AddSeconds(1));
            store.AddOutgoing(alice, "o1", "reply", noon.AddSeconds(1), Carbon: true);

            store.TrySnapshot(alice, out _, out var chat, out var messages);

            Assert.Multiple(() =>
            {
                Assert.That(messages,     Has.Count.EqualTo(2));
                Assert.That(chat!.Unread, Is.EqualTo(1));
            });

        }

        #endregion

        #region ACorrection_ReplacesTheTextAndKeepsThePlace()

        /// <summary>
        /// XEP-0308: the correction names the message it replaces; the line
        /// keeps its position and its time and says that it was edited.
        /// </summary>
        [Test]
        public void ACorrection_ReplacesTheTextAndKeepsThePlace()
        {

            var store = new ChatStore();

            store.AddIncoming(alice, "alice@example.org/phone", "m1", "Hi 4!", noon);
            store.AddIncoming(alice, "alice@example.org/phone", "m2", "second", noon.AddSeconds(5));

            var corrected = store.AddIncoming(alice, "alice@example.org/phone", "m3", "Hi 5!", noon.AddSeconds(10), Corrects: "m1");

            store.TrySnapshot(alice, out _, out var chat, out var messages);

            Assert.Multiple(() =>
            {
                Assert.That(corrected.Id,          Is.EqualTo("m1"));
                Assert.That(corrected.Corrected,   Is.True);
                Assert.That(messages,              Has.Count.EqualTo(2));
                Assert.That(messages[0].Body,      Is.EqualTo("Hi 5!"));
                Assert.That(messages[0].Timestamp, Is.EqualTo(noon));
                Assert.That(chat!.Unread,          Is.EqualTo(2), "a correction is not a new message");
            });

        }

        #endregion

        #region ACorrectionOfAnUnknownMessage_IsShownAsAMessage()

        [Test]
        public void ACorrectionOfAnUnknownMessage_IsShownAsAMessage()
        {

            var store  = new ChatStore();
            var shown  = store.AddIncoming(alice, "alice@example.org/phone", "m3", "Hi 5!", noon, Corrects: "never-seen");

            Assert.Multiple(() =>
            {
                Assert.That(shown.Id,        Is.EqualTo("m3"));
                Assert.That(shown.Corrects,  Is.EqualTo("never-seen"));
                Assert.That(shown.Corrected, Is.False);
            });

        }

        #endregion

        #region ALateMessage_LandsWhereItWasWritten()

        /// <summary>
        /// XEP-0203: a message handed in late carries the time it was written,
        /// and that is where it belongs in the conversation.
        /// </summary>
        [Test]
        public void ALateMessage_LandsWhereItWasWritten()
        {

            var store = new ChatStore();

            store.AddIncoming(alice, "alice@example.org/phone", "m2", "second", noon.AddMinutes(2));
            store.AddIncoming(alice, "alice@example.org/phone", "m1", "first",  noon,               Delayed: true);

            store.TrySnapshot(alice, out _, out var chat, out var messages);

            Assert.Multiple(() =>
            {
                Assert.That(messages.Select(m => m.Id), Is.EqualTo(new[] { "m1", "m2" }));
                Assert.That(chat!.LastMessage?.Id,       Is.EqualTo("m2"));
                Assert.That(chat.LastActivity,           Is.EqualTo(noon.AddMinutes(2)), "the newest time, not the last arrival");
            });

        }

        #endregion

        #region ReceiptsAndMarkers_OnlyConfirmSentMessages()

        [Test]
        public void ReceiptsAndMarkers_OnlyConfirmSentMessages()
        {

            var store = new ChatStore();

            store.AddIncoming(alice, "alice@example.org/phone", "in1",  "hello", noon);
            store.AddOutgoing(alice, "out1", "hi", noon.AddSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(store.MarkDelivered(alice, "in1"),             Is.Null, "a receipt is about what we sent");
                Assert.That(store.MarkDelivered(alice, "nope"),            Is.Null);
                Assert.That(store.MarkDelivered(alice, "out1")?.Delivered, Is.True);
                Assert.That(store.MarkDisplayed(alice, "out1")?.Displayed, Is.True);
                Assert.That(store.MarkDelivered(bob,   "out1"),            Is.Null, "the confirmation has to come from the far end");
            });

        }

        #endregion

        #region AChatKeepsAtMost_MaxMessagesPerChat()

        [Test]
        public void AChatKeepsAtMost_MaxMessagesPerChat()
        {

            var store = new ChatStore(MaxMessagesPerChat: 3);

            for (var i = 0; i < 5; i++)
                store.AddIncoming(alice, "alice@example.org/phone", $"m{i}", $"{i}", noon.AddSeconds(i));

            store.TrySnapshot(alice, out _, out _, out var messages);

            Assert.That(messages.Select(m => m.Id), Is.EqualTo(new[] { "m2", "m3", "m4" }));

        }

        #endregion

        #region ContactsAppearBeforeAWordIsSaid_AndStayAfterTheyLeave()

        [Test]
        public void ContactsAppearBeforeAWordIsSaid_AndStayAfterTheyLeave()
        {

            var store = new ChatStore();

            var contact = store.SetContact(bob, "Bob", SubscriptionState.Both);

            Assert.Multiple(() =>
            {
                Assert.That(contact.InRoster,    Is.True);
                Assert.That(contact.DisplayName, Is.EqualTo("Bob"));
                Assert.That(contact.Presence,    Is.EqualTo(PresenceState.Offline));
            });

            store.SetPresence(bob, PresenceState.Away, "Lunch");
            store.AddIncoming(bob, "bob@example.org/phone", "m1", "back soon", noon);

            var removed = store.RemoveContact(bob);
            store.TrySnapshot(bob, out _, out _, out var messages);

            Assert.Multiple(() =>
            {
                Assert.That(removed!.InRoster,    Is.False);
                Assert.That(removed.DisplayName,  Is.EqualTo("bob@example.org"));
                Assert.That(removed.Presence,     Is.EqualTo(PresenceState.Offline));
                Assert.That(messages,             Has.Count.EqualTo(1), "what was said was said");
            });

        }

        #endregion

        #region PresenceAndTyping_OnlyForChatsThatExist()

        /// <summary>
        /// A stranger's presence is nothing the list has to show; a stranger
        /// who writes opens a chat, and from then on is somebody.
        /// </summary>
        [Test]
        public void PresenceAndTyping_OnlyForChatsThatExist()
        {

            var store = new ChatStore();

            Assert.Multiple(() =>
            {
                Assert.That(store.SetPresence(alice, PresenceState.Available, null), Is.Null);
                Assert.That(store.SetPeerChatState(alice, ChatState.Composing),      Is.Null);
                Assert.That(store.Count,                                             Is.EqualTo(0));
            });

            store.Open(alice);

            Assert.Multiple(() =>
            {
                Assert.That(store.SetPeerChatState(alice, ChatState.Composing)?.PeerChatState, Is.EqualTo(ChatState.Composing));
                Assert.That(store.AddIncoming(alice, "alice@example.org/phone", "m1", "there", noon), Is.Not.Null);
                Assert.That(store.SetPresence(alice, PresenceState.Available, "hi")?.PeerChatState, Is.Null, "a message ends the typing");
            });

        }

        #endregion

        #region AnAvatar_OnlyForChatsThatExist()

        /// <summary>
        /// XEP-0084: a face, like presence, does not open a conversation.
        /// </summary>
        /// <remarks>
        /// Every contact has one already - <see cref="ChatStore.SetContact"/>
        /// makes it as the roster arrives - so what this refuses is a stranger.
        /// An announcement is supposed to follow a presence subscription, but
        /// that is the far server's discipline and not ours, and a picture from
        /// somebody nobody knows must not put a row in the list.
        ///
        /// The second lock, and the second only: what stops this program from
        /// <i>fetching</i> a stranger's picture is the roster check in the
        /// handler, which is where it costs a round trip. This is what stops it
        /// from being shown if that ever gives way.
        /// </remarks>
        [Test]
        public void AnAvatar_OnlyForChatsThatExist()
        {

            var store = new ChatStore();
            var id    = new String('a', 40);

            Assert.Multiple(() =>
            {
                Assert.That(store.SetAvatar(alice, id), Is.Null, "a stranger's picture was accepted");
                Assert.That(store.Count,                Is.EqualTo(0), "and it opened a conversation");
            });

            var contact = store.SetContact(alice, "Alice", SubscriptionState.Both);

            Assert.That(contact.Avatar, Is.Null, "a fresh contact starts with a face");

            Assert.Multiple(() =>
            {

                Assert.That(store.SetAvatar(alice, id)?.Avatar, Is.EqualTo(id));

                // And the removal, which is a different thing from never having
                // heard of one: a node left alone goes on announcing the old
                // picture, so taking one down has to arrive as something.
                Assert.That(store.SetAvatar(alice, null)?.Avatar, Is.Null);

            });

        }

        #endregion

        #region APendingRequest_OpensTheChat()

        [Test]
        public void APendingRequest_OpensTheChat()
        {

            var store = new ChatStore();

            Assert.Multiple(() =>
            {
                Assert.That(store.SetPendingRequest(alice, false),                 Is.Null);
                Assert.That(store.SetPendingRequest(alice, true)?.PendingRequest,  Is.True);
                Assert.That(store.Count,                                           Is.EqualTo(1));
                Assert.That(store.SetPendingRequest(alice, false)?.PendingRequest, Is.False);
            });

        }

        #endregion

        #region TheList_IsSortedByActivity()

        [Test]
        public void TheList_IsSortedByActivity()
        {

            var store = new ChatStore();

            store.SetContact(JID.Parse("carol@example.org"), "Carol", SubscriptionState.Both);
            store.SetContact(bob,   "Bob",   SubscriptionState.Both);
            store.AddIncoming(alice, "alice@example.org/phone", "m1", "old", noon);
            store.AddIncoming(bob,   "bob@example.org/phone",   "m2", "new", noon.AddHours(1));

            store.Snapshot(out var chats);

            Assert.That(chats.Select(c => c.DisplayName), Is.EqualTo(new[] { "Bob", "alice@example.org", "Carol" }),
                        "the most recent activity first, then by name");

        }

        #endregion

        #region EveryChange_HasAHigherSequenceThanTheOneBefore()

        /// <summary>
        /// The browser applies an event only when it is newer than the
        /// snapshot it loaded - which is only right when the numbers grow with
        /// every change and a snapshot carries the number of the last one.
        /// </summary>
        [Test]
        public void EveryChange_HasAHigherSequenceThanTheOneBefore()
        {

            var store  = new ChatStore();
            var seen   = new List<Int64>();

            store.OnChatChanged     += (sequence, _) => seen.Add(sequence);
            store.OnMessageChanged  += (sequence, _) => seen.Add(sequence);

            store.SetContact(bob, "Bob", SubscriptionState.Both);
            store.AddIncoming(bob, "bob@example.org/phone", "m1", "hello", noon);
            store.MarkRead(bob);
            store.MarkRead(bob);                                  // nothing changes, nothing is raised

            var snapshot = store.Snapshot(out _);

            Assert.Multiple(() =>
            {
                Assert.That(seen,               Is.EqualTo(new Int64[] { 1, 2, 3, 4 }));
                Assert.That(snapshot,           Is.EqualTo(4));
                Assert.That(store.Sequence,     Is.EqualTo(4));
            });

            store.AddOutgoing(bob, "o1", "hi", noon.AddSeconds(1));

            Assert.That(store.Sequence, Is.EqualTo(6), "a message is two changes: the chat's summary and the line itself");

        }

        #endregion

        #region TheJSON_CarriesWhatTheBrowserShows()

        [Test]
        public void TheJSON_CarriesWhatTheBrowserShows()
        {

            var store = new ChatStore();

            store.SetContact(bob, "Bob", SubscriptionState.Both);
            store.SetPresence(bob, PresenceState.Dnd, "busy");
            store.AddIncoming(bob, "bob@example.org/phone", "m1", "<b>&</b>", noon);

            store.Snapshot(out var chats);

            var json = chats[0].ToJSON();

            Assert.Multiple(() =>
            {
                Assert.That(json["jid"]?.ToString(),                   Is.EqualTo("bob@example.org"));
                Assert.That(json["displayName"]?.ToString(),           Is.EqualTo("Bob"));
                Assert.That(json["presence"]?.ToString(),              Is.EqualTo("dnd"));
                Assert.That(json["subscription"]?.ToString(),          Is.EqualTo("both"));
                Assert.That(json.Value<Int32>("unread"),                  Is.EqualTo(1));
                Assert.That(json["lastMessage"]?["body"]?.ToString(),  Is.EqualTo("<b>&</b>"), "the text travels as it is; the browser escapes it");
                Assert.That(json["lastMessage"]?["direction"]?.ToString(), Is.EqualTo("in"));
            });

        }

        #endregion


        #region LoadedHistory_IsNotUnreadAndIsNotAnEvent()

        /// <summary>
        /// What comes back out of the archive at a start.
        /// </summary>
        /// <remarks>
        /// Two things it must not do. A badge saying "412 new" after a restart
        /// is worse than no badge - so nothing loaded counts as unread. And a
        /// message event per loaded line would travel to every browser and,
        /// through the handler that feeds the archive, straight back into the
        /// archive it came from - so there is one event for the conversation
        /// and none for the messages.
        /// </remarks>
        [Test]
        public void LoadedHistory_IsNotUnreadAndIsNotAnEvent()
        {

            var store     = new ChatStore();
            var messages  = 0;
            var chats     = 0;

            store.OnMessageChanged  += (_, _) => messages++;
            store.OnChatChanged     += (_, _) => chats++;

            store.LoadHistory(alice, [
                Archived(alice, "a1", "Vorgestern", noon.AddDays(-2)),
                Archived(alice, "a2", "Gestern",     noon.AddDays(-1))
            ]);

            store.TrySnapshot(alice, out _, out var summary, out var loaded);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.Select(message => message.Id),  Is.EqualTo(new[] { "a1", "a2" }));
                Assert.That(summary?.Unread,                        Is.EqualTo(0), "what was archived is not new");
                Assert.That(summary?.LastActivity,                  Is.EqualTo(noon.AddDays(-1)));
                Assert.That(store.Unread,                           Is.EqualTo(0));
                Assert.That(messages,                               Is.EqualTo(0), "no event per loaded message");
                Assert.That(chats,                                  Is.EqualTo(1), "one for the conversation");
            });

        }

        #endregion

        #region LoadedHistory_DoesNotRepeatWhatIsAlreadyThere()

        [Test]
        public void LoadedHistory_DoesNotRepeatWhatIsAlreadyThere()
        {

            var store = new ChatStore();

            store.AddIncoming(alice, "alice@example.org/phone", "m1", "Live", noon);

            store.LoadHistory(alice, [
                Archived(alice, "a1", "Älter", noon.AddHours(-1)),
                Archived(alice, "m1", "Live",  noon)
            ]);

            store.TrySnapshot(alice, out _, out var summary, out var messages);

            Assert.Multiple(() =>
            {
                Assert.That(messages.Select(message => message.Id),  Is.EqualTo(new[] { "a1", "m1" }), "the older one goes where it belongs");
                Assert.That(summary?.Unread,                          Is.EqualTo(1), "the live message stays unread, the archived ones do not become it");
            });

        }

        #endregion

        #region AFetchedFile_ReachesItsMessageWhicheverWayItWent()

        [Test]
        public void AFetchedFile_ReachesItsMessageWhicheverWayItWent()
        {

            var store  = new ChatStore();
            var media  = new MediaRef("20260912T120000Z_photo.jpg", "image/jpeg", 4711, "https://upload.example.org/x/photo.jpg");

            store.AddIncoming(alice, "alice@example.org/phone", "in1",  "https://upload.example.org/x/photo.jpg", noon);
            store.AddOutgoing(alice, "out1",                            "https://upload.example.org/y/photo.jpg", noon.AddMinutes(1));

            Assert.Multiple(() =>
            {

                Assert.That(store.AttachMedia(alice, "in1",  media)?.Media,  Is.EqualTo(media), "a received file, unlike a receipt");
                Assert.That(store.AttachMedia(alice, "out1", media)?.Media,  Is.EqualTo(media));
                Assert.That(store.AttachMedia(alice, "nope", media),          Is.Null);
                Assert.That(store.AttachMedia(bob,   "in1",  media),          Is.Null);

                Assert.That(store.AttachMedia(alice, "in1", media with { Name = "other.jpg" })?.Media,
                            Is.EqualTo(media),
                            "the first file wins; a second fetch does not replace it");

            });

        }

        #endregion

        #region CorrectingTheLastLine_ReplacesItAndKeepsItsPlace()

        /// <summary>
        /// XEP-0308 from this side: what this app said, said properly.
        /// </summary>
        /// <remarks>
        /// <b>The line keeps its place and its time.</b> A correction is not a
        /// new thing said later; it is the same thing said properly, and moving
        /// it to the bottom would put it after the answers to it.
        ///
        /// The id changes, because the correction is a stanza of its own and is
        /// what a further correction has to name - whoever mistypes also
        /// mistypes in the correction.
        /// </remarks>
        [Test]
        public void CorrectingTheLastLine_ReplacesItAndKeepsItsPlace()
        {

            var store = new ChatStore();

            store.AddOutgoing(alice, "m1", "the wrogn word", noon);
            store.AddIncoming(alice, "alice@example.org/phone", "m2", "the what?", noon.AddMinutes(1));

            var corrected = store.CorrectLastOutgoing(alice, "m3", "the right word");

            store.TrySnapshot(alice, out _, out _, out var lines);

            Assert.Multiple(() =>
            {

                Assert.That(corrected,       Is.Not.Null);
                Assert.That(corrected!.Body, Is.EqualTo("the right word"));
                Assert.That(corrected.Id,    Is.EqualTo("m3"),
                            "The correction kept the old id, so a further correction would name " +
                            "a stanza that no longer exists.");

                Assert.That(lines,           Has.Count.EqualTo(2),
                            "The correction came in as a third line, so the wrong word stands " +
                            "above the right one.");

                Assert.That(lines[0].Body,      Is.EqualTo("the right word"),
                            "The corrected line moved, so it now stands after the answer to it.");
                Assert.That(lines[0].Corrected, Is.True);
                Assert.That(lines[1].Body,      Is.EqualTo("the what?"));

            });

        }

        #endregion

        #region CorrectingWithNothingSent_IsNotACorrection()

        /// <summary>
        /// A conversation nothing has gone out in.
        /// </summary>
        /// <remarks>
        /// Null and not a new line: a correction of nothing is a mistake, and
        /// turning it into an ordinary message would put words on screen that
        /// the person meant as a replacement for something else.
        /// </remarks>
        [Test]
        public void CorrectingWithNothingSent_IsNotACorrection()
        {

            var store = new ChatStore();

            Assert.That(store.CorrectLastOutgoing(alice, "m1", "never mind"), Is.Null,
                        "A conversation that has never been written in accepted a correction.");

            store.AddIncoming(alice, "alice@example.org/phone", "m2", "Hello?", noon);

            Assert.That(store.CorrectLastOutgoing(alice, "m3", "not mine to fix"), Is.Null,
                        "Somebody else's line was corrected as though this app had written it.");

        }

        #endregion

        #region (private static) Archived(Chat, Id, Body, When)

        /// <summary>
        /// A message as it comes out of the archive.
        /// </summary>
        private static ChatMessage Archived(JID Chat, String Id, String Body, DateTimeOffset When)

            => new (Id,
                    Chat,
                    MessageDirection.Incoming,
                    $"{Chat}/phone",
                    Body,
                    When);

        #endregion

    }

}
