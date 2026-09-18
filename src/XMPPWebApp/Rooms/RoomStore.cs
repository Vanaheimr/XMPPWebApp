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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Rooms
{

    /// <summary>
    /// The rooms this web app is in, as the browser gets to see them.
    /// </summary>
    /// <remarks>
    /// <b>Beside <see cref="Chats.ChatStore"/> and not inside it</b>, which is
    /// the same decision Ratatoskr made in D116 and for the same reason: a room
    /// looks like a conversation and is not one. What is different is not the
    /// look but every rule underneath.
    ///
    /// <list type="bullet">
    /// <item><b>Who is there is a list, not a state.</b> A conversation has one
    ///       far end whose presence is a field; a room has occupants who come
    ///       and go, under names that mean nothing outside it.</item>
    /// <item><b>Nobody is a contact.</b> A roster is people one has a
    ///       subscription with. Filing occupants there would fill it with
    ///       nicknames that stop existing when somebody leaves.</item>
    /// <item><b>Receipts and markers have no meaning.</b> Asking twenty people
    ///       to confirm delivery produces twenty stanzas everybody present
    ///       sees.</item>
    /// <item><b>Encryption is a property of the room, not of the
    ///       conversation.</b> A chat is encrypted when the far end has a device
    ///       that can read it; a room can be encrypted in only when it is
    ///       non-anonymous, because one encrypts to real addresses and a room
    ///       hands out nicknames (D125).</item>
    /// </list>
    ///
    /// Merging the two would mean carrying four sets of rules in one object and
    /// a flag saying which apply.
    ///
    /// <b>Nothing here is archived.</b> The conversations are written to disk;
    /// the rooms are not, and that is a decision rather than an omission - a
    /// room keeps its own history on the server (XEP-0313) and the one place it
    /// belongs is there, where somebody joining later gets it too. What this
    /// holds is what has been seen since the app started, bounded like the chat
    /// store.
    /// </remarks>
    public sealed class RoomStore
    {

        #region (class) Room

        /// <summary>
        /// The mutable side of a room; only ever touched under the lock.
        /// </summary>
        private sealed class Room(JID Jid, String Nick)
        {

            public JID                        Jid             { get; }      = Jid;
            public String                     Nick            { get; set; } = Nick;
            public RoomState                  State           { get; set; } = RoomState.Joining;
            public String?                    Subject         { get; set; }
            public String?                    SubjectBy       { get; set; }
            public Boolean                    NonAnonymous    { get; set; }
            public Boolean                    Owner           { get; set; }
            public String?                    CannotEncrypt   { get; set; } = "the room has not been entered";
            public Int32                      Unread          { get; set; }
            public DateTimeOffset?            LastActivity    { get; set; }
            public String?                    Refusal         { get; set; }

            public Dictionary<String, RoomOccupant>  Occupants  { get; } =
                new (StringComparer.Ordinal);

            public List<RoomMessage>          Messages        { get; }      = [];

            public RoomSummary Summary()

                => new (Jid,
                        Nick,
                        State,
                        Subject,
                        SubjectBy,
                        NonAnonymous,
                        Owner,
                        CannotEncrypt,
                        [.. Occupants.Values.OrderBy(o => o.Nick, StringComparer.OrdinalIgnoreCase)],
                        Unread,
                        Messages.Count > 0 ? Messages[^1] : null,
                        LastActivity,
                        Refusal);

        }

        #endregion

        #region Data

        private readonly Dictionary<JID, Room>  rooms  = [];
        private readonly Lock                   @lock  = new();
        private          Int64                  sequence;

        #endregion

        #region Properties

        /// <summary>
        /// How many messages one room keeps here.
        /// </summary>
        public Int32  MaxMessagesPerRoom  { get; }

        /// <summary>
        /// The sequence number of the last change.
        /// </summary>
        public Int64  Sequence
        {
            get
            {
                lock (@lock)
                    return sequence;
            }
        }

        /// <summary>
        /// How many rooms this app is in or walking into.
        /// </summary>
        public Int32  Count
        {
            get
            {
                lock (@lock)
                    return rooms.Count;
            }
        }

        /// <summary>
        /// Everything unread across all rooms.
        /// </summary>
        public Int32  Unread
        {
            get
            {
                lock (@lock)
                    return rooms.Values.Sum(room => room.Unread);
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// A room changed: somebody came or went, the subject moved, the state
        /// did.
        /// </summary>
        public event Action<Int64, RoomSummary>?  OnRoomChanged;

        /// <summary>
        /// A line was said in a room.
        /// </summary>
        public event Action<Int64, RoomMessage>?  OnRoomMessage;

        #endregion

        #region Constructor(s)

        public RoomStore(Int32 MaxMessagesPerRoom = 500)
        {
            this.MaxMessagesPerRoom = MaxMessagesPerRoom;
        }

        #endregion


        #region Snapshot(out Rooms) / TrySnapshot(Room, ...)

        /// <summary>
        /// Every room, and the sequence number the list is good up to.
        /// </summary>
        public Int64 Snapshot(out IReadOnlyList<RoomSummary> Rooms)
        {
            lock (@lock)
            {

                Rooms = [.. rooms.Values
                                 .Select(room => room.Summary())
                                 .OrderByDescending(room => room.LastActivity ?? DateTimeOffset.MinValue)];

                return sequence;

            }
        }

        public Boolean TrySnapshot(JID                            Jid,
                                   out Int64                      Sequence,
                                   out RoomSummary?               Summary,
                                   out IReadOnlyList<RoomMessage> Messages)
        {
            lock (@lock)
            {

                Sequence = sequence;

                if (!rooms.TryGetValue(Jid.Bare, out var room))
                {
                    Summary   = null;
                    Messages  = [];
                    return false;
                }

                Summary   = room.Summary();
                Messages  = [.. room.Messages];

                return true;

            }
        }

        #endregion

        #region Joining(Jid, Nick) / Joined(Room, ...) / Refused(Jid, Why) / Left(Jid)

        /// <summary>
        /// A room this app is walking into.
        /// </summary>
        /// <remarks>
        /// Put in the list before the service has answered, so that the page can
        /// show it as being entered. A join that is refused stays visible with
        /// its refusal on it - a row that vanishes tells nobody why.
        /// </remarks>
        public RoomSummary Joining(JID Jid, String Nick)
        {
            lock (@lock)
            {

                var room = GetOrAdd(Jid.Bare, Nick);

                room.State    = RoomState.Joining;
                room.Nick     = Nick;
                room.Refusal  = null;

                return Changed(room);

            }
        }

        /// <summary>
        /// The service let us in. Everything about the room comes from the
        /// library's own picture of it rather than being tracked twice.
        /// </summary>
        public RoomSummary? Joined(MucRoom  Room,
                                   String?  CannotEncrypt)
        {
            lock (@lock)
            {

                var room = GetOrAdd(Room.Address.Bare, Room.Nick);

                room.State          = RoomState.Joined;
                room.Nick           = Room.Nick;
                room.Refusal        = null;
                room.Subject        = Room.Subject;
                room.SubjectBy      = Room.SubjectBy?.Resourcepart;
                room.NonAnonymous   = Room.IsNonAnonymous;
                room.Owner          = Room.Me?.Affiliation == MucAffiliation.Owner;
                room.CannotEncrypt  = CannotEncrypt;
                room.LastActivity ??= DateTimeOffset.UtcNow;

                room.Occupants.Clear();

                foreach (var (nick, occupant) in Room.Occupants)
                    room.Occupants[nick] = RoomOccupant.Of(occupant, nick == Room.Nick);

                return Changed(room);

            }
        }

        /// <summary>
        /// The service said no. <b>The room stays in the list</b> with the
        /// reason on it.
        /// </summary>
        public RoomSummary? Refused(JID Jid, String Why)
        {
            lock (@lock)
            {

                if (!rooms.TryGetValue(Jid.Bare, out var room))
                    return null;

                room.State    = RoomState.Refused;
                room.Refusal  = Why;

                room.Occupants.Clear();

                return Changed(room);

            }
        }

        /// <summary>
        /// Out of the room - by choice or not.
        /// </summary>
        /// <remarks>
        /// The row goes, and so does what was said in it. Keeping the lines of a
        /// room nobody is in any more would be an archive, and this is not one:
        /// what a room holds lives on the server, where whoever comes back gets
        /// it along with everything said while they were away.
        /// </remarks>
        public Boolean Left(JID Jid)
        {
            lock (@lock)
            {

                if (!rooms.Remove(Jid.Bare, out var room))
                    return false;

                sequence++;

                OnRoomChanged?.Invoke(sequence, room.Summary() with { State = RoomState.Left });

                return true;

            }
        }

        #endregion

        #region SetOccupants(Jid, Room, CannotEncrypt) / SetSubject(...)

        /// <summary>
        /// Who is in the room now, taken from the library's picture whole.
        /// </summary>
        /// <remarks>
        /// <b>Replaced rather than patched.</b> Occupant events arrive as
        /// "somebody joined", "somebody changed", "somebody left" and could be
        /// applied one by one - and then a missed event leaves a person in the
        /// list for ever, which in an encrypted room is not cosmetic: the count
        /// of who can read this would be wrong.
        ///
        /// The library holds the list anyway and holds it correctly; copying it
        /// whole is cheaper than keeping a second one in step.
        /// </remarks>
        public RoomSummary? SetOccupants(MucRoom  Room,
                                         String?  CannotEncrypt)
        {
            lock (@lock)
            {

                if (!rooms.TryGetValue(Room.Address.Bare, out var room))
                    return null;

                room.Occupants.Clear();

                foreach (var (nick, occupant) in Room.Occupants)
                    room.Occupants[nick] = RoomOccupant.Of(occupant, nick == Room.Nick);

                room.Nick           = Room.Nick;
                room.NonAnonymous   = Room.IsNonAnonymous;
                room.Owner          = Room.Me?.Affiliation == MucAffiliation.Owner;
                room.CannotEncrypt  = CannotEncrypt;

                return Changed(room);

            }
        }

        public RoomSummary? SetSubject(JID      Jid,
                                       String?  Subject,
                                       String?  By)
        {
            lock (@lock)
            {

                if (!rooms.TryGetValue(Jid.Bare, out var room))
                    return null;

                room.Subject    = Subject;
                room.SubjectBy  = By;

                return Changed(room);

            }
        }

        #endregion

        #region AddIncoming(...) / AddOutgoing(...)

        /// <summary>
        /// Somebody said something in a room.
        /// </summary>
        /// <param name="Nick">Their name in this room - all that everybody present shares.</param>
        /// <param name="Delayed">
        /// Whether it was held on the way, or came out of the room's history on
        /// entering. <b>A delayed line does not count as unread</b>: what a room
        /// sends when one walks in is not news.
        /// </param>
        /// <summary>
        /// XEP-0308: replaces the text of the last line this app said in a room.
        /// </summary>
        /// <returns>
        /// The corrected line, or null when nothing has been said here yet.
        /// </returns>
        /// <remarks>
        /// <b>Ours, by the nickname this app holds.</b> The same rule the
        /// incoming side follows and for the same reason: a correction belongs
        /// to the occupant who wrote the line, and in a room that is all there
        /// is to go by.
        /// </remarks>
        /// <summary>
        /// XEP-0424: takes back a line in a room, on its author's request.
        /// </summary>
        /// <remarks>
        /// <b>Only that occupant's own line.</b> XEP-0424 has a retraction
        /// processed only when it and the message come from the same
        /// address, and in a room that is the full one - the room plus the
        /// nickname. Without the comparison anybody standing there could
        /// empty anybody else's words by naming their id, and the line would
        /// keep the name of the person who never took it back.
        ///
        /// The name looked for is the one the room gave, which is why a
        /// line carries it (<c>RetractableId</c>) beside the id on its
        /// stanza.
        /// </remarks>
        public RoomMessage? Retract(JID     Jid,
                                    String  Nick,
                                    String  RetractedId)
        {
            lock (@lock)
            {

                if (!rooms.TryGetValue(Jid.Bare, out var room))
                    return null;

                var index = room.Messages.FindIndex(line => line.RetractableId == RetractedId &&
                                                            line.Nick          == Nick);

                if (index < 0)
                    return null;

                var taken = room.Messages[index] with {
                                Body       = "",
                                Retracted  = true
                            };

                room.Messages[index] = taken;

                sequence++;
                OnRoomMessage?.Invoke(sequence, taken);

                return taken;

            }
        }

        public RoomMessage? CorrectLastOwn(JID     Jid,
                                           String  MessageId,
                                           String  Body)
        {
            lock (@lock)
            {

                if (!rooms.TryGetValue(Jid.Bare, out var room))
                    return null;

                var index = room.Messages.FindLastIndex(line => line.Nick == room.Nick);

                if (index < 0)
                    return null;

                var corrected = room.Messages[index] with {
                                    Id         = MessageId,
                                    Body       = Body,
                                    Corrected  = true
                                };

                room.Messages[index] = corrected;

                sequence++;
                OnRoomMessage?.Invoke(sequence, corrected);

                return corrected;

            }
        }

        public RoomMessage AddIncoming(JID             Jid,
                                       String          Nick,
                                       String?         MessageId,
                                       String          Body,
                                       DateTimeOffset  Timestamp,
                                       Boolean         Delayed    = false,
                                       Boolean         Encrypted  = false,
                                       String?         RepliesTo  = null,
                                       String?         Quote      = null,
                                       Boolean         Private    = false,
                                       String?         Corrects   = null,
                                       String?         RetractableId = null)
        {
            lock (@lock)
            {

                var room     = GetOrAdd(Jid.Bare, Nick);

                // XEP-0308: a correction replaces the text of the line it
                // names and nothing else - the line keeps its place and its
                // time, which in a room is what keeps a conversation
                // readable.
                //
                // <b>And only that occupant’s own line.</b> Section 5:
                // a correction MUST only be allowed when both the original
                // and the correction come from the same sender, and in a room
                // that is the full address - which is the room plus the
                // nickname, so the nickname is the comparison. Without it
                // anybody in the room could rewrite anybody else’s words by
                // naming their id, and the line would keep the name of the
                // person who never wrote it.
                //
                // What the nickname cannot catch is somebody leaving and
                // another taking the name; the section says a correction
                // across a rejoin SHOULD be refused and there is nothing in a
                // semi-anonymous room to tell them apart with. Named rather
                // than guessed at.
                if (Corrects is not null)
                {

                    var index = room.Messages.FindIndex(line => line.Id   == Corrects &&
                                                                line.Nick == Nick);

                    if (index >= 0)
                    {

                        var corrected = room.Messages[index] with {
                                            Body       = Body,
                                            Corrected  = true
                                        };

                        room.Messages[index]  = corrected;
                        room.LastActivity     = Max(room.LastActivity, Timestamp);

                        sequence++;
                        OnRoomMessage?.Invoke(sequence, corrected);

                        return corrected;

                    }

                }

                var message  = new RoomMessage(
                                   MessageId ?? NewId(),
                                   Jid.Bare,
                                   Nick,
                                   Nick == room.Nick,
                                   Body,
                                   Timestamp,
                                   Delayed,
                                   Encrypted,
                                   RepliesTo,
                                   Quote,
                                   Private,
                                   Corrected:      false,
                                   Retracted:      false,
                                   RetractableId:  RetractableId
                               );

                Insert(room, message);

                // Our own line coming back is not unread, and neither is
                // history. Everything else is.
                if (!Delayed && Nick != room.Nick)
                    room.Unread++;

                room.LastActivity = Max(room.LastActivity, Timestamp);

                sequence++;

                OnRoomMessage?.Invoke(sequence, message);
                OnRoomChanged?.Invoke(sequence, room.Summary());

                return message;

            }
        }

        /// <summary>
        /// A line this app sent.
        /// </summary>
        /// <remarks>
        /// <b>Put in on sending and not on the reflection coming back</b>,
        /// which is the opposite of what a room would suggest - the service
        /// hands every message to everybody, the sender included, so waiting
        /// would look tidier.
        ///
        /// It cannot be done for an encrypted room: an OMEMO element carries no
        /// key for the device that made it, so our own line comes back as one
        /// we cannot read. Doing the same for both keeps one path instead of
        /// two, and the reflection is recognised and dropped (see
        /// <see cref="Has"/>).
        /// </remarks>
        public RoomMessage AddOutgoing(JID             Jid,
                                       String          MessageId,
                                       String          Body,
                                       DateTimeOffset  Timestamp,
                                       Boolean         Encrypted)
        {
            lock (@lock)
            {

                var room     = GetOrAdd(Jid.Bare, "");

                var message  = new RoomMessage(
                                   MessageId,
                                   Jid.Bare,
                                   room.Nick,
                                   true,
                                   Body,
                                   Timestamp,
                                   false,
                                   Encrypted,
                                   null,
                                   null
                               );

                Insert(room, message);

                room.LastActivity = Max(room.LastActivity, Timestamp);

                sequence++;

                OnRoomMessage?.Invoke(sequence, message);
                OnRoomChanged?.Invoke(sequence, room.Summary());

                return message;

            }
        }

        #endregion

        #region Has(Jid, MessageId) / MarkRead(Jid) / Clear()

        /// <summary>
        /// Is this line already here?
        /// </summary>
        /// <remarks>
        /// <b>What keeps a room from showing everything twice.</b> A service
        /// hands every message to everybody including the sender, so the line
        /// this app just wrote comes straight back with the same id.
        /// </remarks>
        public Boolean Has(JID Jid, String? MessageId)
        {

            if (MessageId is null)
                return false;

            lock (@lock)
                return rooms.TryGetValue(Jid.Bare, out var room) &&
                       room.Messages.Any(message => message.Id == MessageId);

        }

        public RoomSummary? MarkRead(JID Jid)
        {
            lock (@lock)
            {

                if (!rooms.TryGetValue(Jid.Bare, out var room) || room.Unread == 0)
                    return null;

                room.Unread = 0;

                return Changed(room);

            }
        }

        /// <summary>
        /// Everything gone - at a sign-out, or when the account changes.
        /// </summary>
        public void Clear()
        {
            lock (@lock)
            {
                rooms.Clear();
                sequence++;
            }
        }

        #endregion


        #region (private) GetOrAdd / Insert / Changed / Max / NewId

        private Room GetOrAdd(JID Jid, String Nick)
        {

            if (!rooms.TryGetValue(Jid, out var room))
                rooms[Jid] = room = new Room(Jid, Nick);

            return room;

        }

        /// <summary>
        /// By time, and a bounded number kept.
        /// </summary>
        /// <remarks>
        /// Inserted in order rather than appended, because a room sends its
        /// history on entering: those lines arrive after the ones already here
        /// and belong before them.
        /// </remarks>
        private void Insert(Room room, RoomMessage message)
        {

            var at = room.Messages.FindLastIndex(existing => existing.Timestamp <= message.Timestamp);

            room.Messages.Insert(at + 1, message);

            while (room.Messages.Count > MaxMessagesPerRoom)
                room.Messages.RemoveAt(0);

        }

        private RoomSummary Changed(Room room)
        {

            sequence++;

            var summary = room.Summary();

            OnRoomChanged?.Invoke(sequence, summary);

            return summary;

        }

        private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b)
            => a is null || b > a ? b : a.Value;

        private static String NewId()
            => Guid.NewGuid().ToString("N")[..16];

        #endregion

    }

}
