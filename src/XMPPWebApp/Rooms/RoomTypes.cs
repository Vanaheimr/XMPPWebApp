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
    /// Where a room stands from this app's side.
    /// </summary>
    public enum RoomState
    {

        /// <summary>The join is out and nothing has come back.</summary>
        Joining,

        /// <summary>In.</summary>
        Joined,

        /// <summary>The service said no; <c>Refusal</c> says what it said.</summary>
        Refused,

        /// <summary>Out again. Only ever seen in the event that says so.</summary>
        Left

    }


    /// <summary>
    /// Somebody in a room.
    /// </summary>
    /// <param name="Nick">Their name in this room, and usually the only one there is.</param>
    /// <param name="Affiliation">What they are to the room beyond this visit.</param>
    /// <param name="Role">What they may do while they are in it.</param>
    /// <param name="Jid">
    /// Their real address, or null.
    /// <b>Null is the normal case</b>: a room is semi-anonymous unless somebody
    /// configured it otherwise, and then it tells nobody but its moderators who
    /// anybody is. It is also exactly what decides whether the room can be
    /// encrypted in - see D125.
    /// </param>
    /// <param name="Self">Whether this is us.</param>
    public sealed record RoomOccupant(String   Nick,
                                      String   Affiliation,
                                      String   Role,
                                      String?  Jid,
                                      Boolean  Self)
    {

        public static RoomOccupant Of(MucOccupant Occupant, Boolean Self)

            => new (Occupant.Nick,
                    Occupant.Affiliation.ToString().ToLowerInvariant(),
                    Occupant.Role.       ToString().ToLowerInvariant(),
                    Occupant.RealJid?.Bare.ToString(),
                    Self);

        public JObject ToJSON()

            => new (
                   new JProperty("nick",         Nick),
                   new JProperty("affiliation",  Affiliation),
                   new JProperty("role",         Role),
                   new JProperty("jid",          Jid),
                   new JProperty("self",         Self)
               );

    }


    /// <summary>
    /// One line said in a room.
    /// </summary>
    /// <param name="Nick">Who said it - their name in the room.</param>
    /// <param name="Mine">Whether this app said it.</param>
    /// <param name="Delayed">
    /// Whether it was held on the way, or came out of the room's history on
    /// entering.
    /// </param>
    /// <param name="Encrypted">XEP-0384: whether this line travelled encrypted.</param>
    /// <remarks>
    /// <b>A record of its own rather than <see cref="Chats.ChatMessage"/></b>,
    /// and the reason is what is missing from it: no delivery receipt, no chat
    /// marker, no carbon, no correction. None of the four means anything in a
    /// room - a receipt would be answered by everybody present - so carrying
    /// them would be four fields that are always false and a reader left
    /// wondering when they are not.
    ///
    /// What it has instead is the nickname, which a conversation has no place
    /// for: in a room somebody speaks from their place in it, and that place is
    /// all that everybody present shares.
    /// </remarks>
    public sealed record RoomMessage(String          Id,
                                     JID             Room,
                                     String          Nick,
                                     Boolean         Mine,
                                     String          Body,
                                     DateTimeOffset  Timestamp,
                                     Boolean         Delayed,
                                     Boolean         Encrypted,
                                     String?         RepliesTo,
                                     String?         Quote)
    {

        public JObject ToJSON()

            => new (
                   new JProperty("id",         Id),
                   new JProperty("room",       Room.ToString()),
                   new JProperty("nick",       Nick),
                   new JProperty("mine",       Mine),
                   new JProperty("body",       Body),
                   new JProperty("timestamp",  Timestamp.ToString("o")),
                   new JProperty("delayed",    Delayed),
                   new JProperty("encrypted",  Encrypted),
                   new JProperty("repliesTo",  RepliesTo),
                   new JProperty("quote",      Quote)
               );

    }


    /// <summary>
    /// A room as the list on the left of the room view shows it.
    /// </summary>
    /// <param name="Nick">What this app is called in there.</param>
    /// <param name="NonAnonymous">Whether the room shows everybody's real address.</param>
    /// <param name="Owner">Whether this app may configure it.</param>
    /// <param name="CannotEncrypt">
    /// Why nothing in this room can be encrypted, or null when it can.
    /// <b>The reason and not a Boolean</b>: a lock that is greyed out and says
    /// nothing tells somebody encryption is impossible, when it is usually one
    /// setting away.
    /// </param>
    /// <param name="Refusal">Why the service would not let us in, or null.</param>
    public sealed record RoomSummary(JID                           Jid,
                                     String                        Nick,
                                     RoomState                     State,
                                     String?                       Subject,
                                     String?                       SubjectBy,
                                     Boolean                       NonAnonymous,
                                     Boolean                       Owner,
                                     String?                       CannotEncrypt,
                                     IReadOnlyList<RoomOccupant>   Occupants,
                                     Int32                         Unread,
                                     RoomMessage?                  LastMessage,
                                     DateTimeOffset?               LastActivity,
                                     String?                       Refusal)
    {

        /// <summary>
        /// What to call the room: its local part, which is what people name.
        /// </summary>
        public String DisplayName
            => Jid.Localpart ?? Jid.ToString();


        public JObject ToJSON()

            => new (
                   new JProperty("jid",            Jid.ToString()),
                   new JProperty("displayName",    DisplayName),
                   new JProperty("nick",           Nick),
                   new JProperty("state",          State.ToString().ToLowerInvariant()),
                   new JProperty("subject",        Subject),
                   new JProperty("subjectBy",      SubjectBy),
                   new JProperty("nonAnonymous",   NonAnonymous),
                   new JProperty("owner",          Owner),

                   // Null means "it can be". Anything else is the sentence to
                   // show beside a lock that is not offered.
                   new JProperty("cannotEncrypt",  CannotEncrypt),

                   new JProperty("occupants",      new JArray(Occupants.Select(o => o.ToJSON()))),
                   new JProperty("unread",         Unread),
                   new JProperty("lastMessage",    LastMessage?.ToJSON()),
                   new JProperty("lastActivity",   LastActivity?.ToString("o")),
                   new JProperty("refusal",        Refusal)
               );

    }

}
