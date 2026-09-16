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

using org.GraphDefined.Vanaheimr.XMPPWebApp.Rooms;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// What the room store does that the chat store does not have to.
    /// </summary>
    /// <remarks>
    /// Every round here is about a rule that has no counterpart in a
    /// conversation, which is the whole argument for the two being separate
    /// objects rather than one with a flag.
    /// </remarks>
    [TestFixture]
    public class RoomStoreTests
    {

        #region Data

        private static readonly JID  TheRoom  = JID.Parse("chat@conference.example.org");
        private static readonly JID  Other    = JID.Parse("other@conference.example.org");

        private static readonly DateTimeOffset  Noon = new (2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

        #endregion


        #region ARoomIsNotAConversation()

        /// <summary>
        /// The two stores do not know about each other, and the counters do not
        /// add up into one number.
        /// </summary>
        [Test]
        public void ARoomIsNotAConversation()
        {

            var rooms = new RoomStore();

            rooms.Joining(TheRoom, "me");
            rooms.AddIncoming(TheRoom, "alice", "m1", "hello", Noon);

            Assert.Multiple(() =>
            {

                Assert.That(rooms.Count,   Is.EqualTo(1));
                Assert.That(rooms.Unread,  Is.EqualTo(1));

            });

        }

        #endregion

        #region OurOwnLineComingBackIsNotASecondLine()

        /// <summary>
        /// <b>What keeps a room from showing everything twice.</b>
        /// </summary>
        /// <remarks>
        /// A service hands every message to everybody including the sender, so
        /// the line this app wrote a moment ago comes straight back with the
        /// same id. It is already in the store, put there on sending - which it
        /// has to be for an encrypted room, where the reflection cannot be read
        /// at all: an OMEMO element carries no key for the device that made it.
        ///
        /// So the id is what recognises it, and that is what
        /// <see cref="RoomStore.Has"/> is for.
        /// </remarks>
        [Test]
        public void OurOwnLineComingBackIsNotASecondLine()
        {

            var rooms = new RoomStore();

            rooms.Joining(TheRoom, "me");
            rooms.AddOutgoing(TheRoom, "m1", "said once", Noon, Encrypted: false);

            Assert.That(rooms.Has(TheRoom, "m1"), Is.True,
                        "The line this app sent is not recognised when it comes back, so every room " +
                        "shows everything said in it twice.");

            Assert.Multiple(() =>
            {

                Assert.That(rooms.Has(TheRoom, "m2"),  Is.False);
                Assert.That(rooms.Has(TheRoom, null),  Is.False, "A message with no id matched something.");
                Assert.That(rooms.Has(Other,   "m1"),  Is.False, "An id matched in a room it was never said in.");

            });

        }

        #endregion

        #region OurOwnLineAndTheHistoryAreNotUnread()

        /// <summary>
        /// What a room sends when one walks in is not news.
        /// </summary>
        /// <remarks>
        /// Two cases and both easy to get wrong in the same visible way: a room
        /// one has just entered showing forty unread lines, and one's own
        /// sentence counting as something one has not read.
        /// </remarks>
        [Test]
        public void OurOwnLineAndTheHistoryAreNotUnread()
        {

            var rooms = new RoomStore();

            rooms.Joining(TheRoom, "me");

            rooms.AddIncoming(TheRoom, "alice", "h1", "said before we came",  Noon.AddMinutes(-10), Delayed: true);
            rooms.AddIncoming(TheRoom, "me",    "r1", "our own, reflected",   Noon);
            rooms.AddIncoming(TheRoom, "alice", "m1", "said to us",           Noon.AddMinutes(1));

            Assert.That(rooms.Unread, Is.EqualTo(1),
                        "The history of a room, or this app's own line coming back, counted as unread.");

        }

        #endregion

        #region TheHistoryGoesBeforeWhatIsAlreadyThere()

        /// <summary>
        /// A room sends what was said before <i>after</i> one is in it.
        /// </summary>
        /// <remarks>
        /// So the lines arrive later than the ones already on screen and belong
        /// above them. Appending would put yesterday underneath today.
        /// </remarks>
        [Test]
        public void TheHistoryGoesBeforeWhatIsAlreadyThere()
        {

            var rooms = new RoomStore();

            rooms.Joining(TheRoom, "me");

            rooms.AddIncoming(TheRoom, "alice", "now",       "now",       Noon);
            rooms.AddIncoming(TheRoom, "alice", "yesterday", "yesterday", Noon.AddDays(-1), Delayed: true);

            rooms.TrySnapshot(TheRoom, out _, out _, out var messages);

            Assert.That(messages.Select(m => m.Id), Is.EqualTo(new[] { "yesterday", "now" }),
                        "The room's history was put underneath what was said since.");

        }

        #endregion

        #region OnlySoManyLinesAreKept()

        [Test]
        public void OnlySoManyLinesAreKept()
        {

            var rooms = new RoomStore(MaxMessagesPerRoom: 3);

            rooms.Joining(TheRoom, "me");

            for (var i = 0; i < 10; i++)
                rooms.AddIncoming(TheRoom, "alice", $"m{i}", $"line {i}", Noon.AddSeconds(i));

            rooms.TrySnapshot(TheRoom, out _, out _, out var messages);

            Assert.Multiple(() =>
            {

                Assert.That(messages,                 Has.Count.EqualTo(3));
                Assert.That(messages.Select(m => m.Id), Is.EqualTo(new[] { "m7", "m8", "m9" }),
                            "The wrong end was dropped.");

            });

        }

        #endregion

        #region LeavingTakesTheLinesWithIt()

        /// <summary>
        /// Out of the room, out of the list - and what was said goes too.
        /// </summary>
        /// <remarks>
        /// Keeping the lines of a room nobody is in any more would be an
        /// archive, and this is not one: what a room holds lives on the server,
        /// where whoever comes back gets it along with everything said while
        /// they were away.
        ///
        /// The event still goes out, carrying <c>left</c> - a row that vanishes
        /// without a word leaves every browser showing it.
        /// </remarks>
        [Test]
        public void LeavingTakesTheLinesWithIt()
        {

            var rooms = new RoomStore();
            var told  = new List<RoomSummary>();

            rooms.OnRoomChanged += (seq, room) => told.Add(room);

            rooms.Joining(TheRoom, "me");
            rooms.AddIncoming(TheRoom, "alice", "m1", "hello", Noon);

            Assert.That(rooms.Left(TheRoom), Is.True);

            Assert.Multiple(() =>
            {

                Assert.That(rooms.Count, Is.EqualTo(0));

                Assert.That(rooms.TrySnapshot(TheRoom, out _, out _, out _), Is.False,
                            "The room is still readable after it was left.");

                Assert.That(told[^1].State, Is.EqualTo(RoomState.Left),
                            "Nobody was told the row is gone, so every browser goes on showing it.");

                Assert.That(rooms.Left(TheRoom), Is.False, "Leaving twice reported twice.");

            });

        }

        #endregion

        #region ARefusedRoomStaysWithItsReason()

        /// <summary>
        /// A join the service said no to.
        /// </summary>
        /// <remarks>
        /// <b>The row stays.</b> A nickname already taken, a members-only room,
        /// a ban - each is something a person can do something about, and a row
        /// that simply never appears tells them nothing at all.
        /// </remarks>
        [Test]
        public void ARefusedRoomStaysWithItsReason()
        {

            var rooms = new RoomStore();

            rooms.Joining(TheRoom, "me");
            rooms.Refused(TheRoom, "The service refused: conflict.");

            rooms.TrySnapshot(TheRoom, out _, out var summary, out _);

            Assert.Multiple(() =>
            {

                Assert.That(summary,          Is.Not.Null, "The refused room disappeared.");
                Assert.That(summary!.State,   Is.EqualTo(RoomState.Refused));
                Assert.That(summary.Refusal,  Does.Contain("conflict"));

            });

        }

        #endregion

        #region AnAccountChangeTakesEveryRoom()

        /// <summary>
        /// A room is a place one is in while a connection holds it open.
        /// </summary>
        /// <remarks>
        /// Unlike the conversations, which are a list of addresses and survive.
        /// Leaving the rooms standing after the account changed would show
        /// somebody rooms they are not in, under a nickname that is not theirs.
        /// </remarks>
        [Test]
        public void AnAccountChangeTakesEveryRoom()
        {

            var rooms = new RoomStore();

            rooms.Joining(TheRoom, "me");
            rooms.Joining(Other,   "me");

            rooms.Clear();

            Assert.That(rooms.Count, Is.EqualTo(0),
                        "A room outlived the connection that was in it.");

        }

        #endregion

        #region MarkingReadIsOnlyEverAStepDown()

        [Test]
        public void MarkingReadIsOnlyEverAStepDown()
        {

            var rooms = new RoomStore();

            rooms.Joining(TheRoom, "me");
            rooms.AddIncoming(TheRoom, "alice", "m1", "hello", Noon);

            Assert.Multiple(() =>
            {

                Assert.That(rooms.MarkRead(TheRoom)?.Unread, Is.EqualTo(0));

                // A second call changes nothing and must not therefore tell
                // every browser that something changed.
                Assert.That(rooms.MarkRead(TheRoom), Is.Null,
                            "Marking an already-read room produced an event.");

                Assert.That(rooms.MarkRead(Other),   Is.Null,
                            "A room nobody is in was marked read.");

            });

        }

        #endregion

        #region TheSummaryCarriesTheReasonAndNotABoolean()

        /// <summary>
        /// Why a room cannot be encrypted in travels as the sentence.
        /// </summary>
        /// <remarks>
        /// A lock that is greyed out and says nothing tells somebody encryption
        /// is impossible, when it is usually one setting away. The page shows
        /// this text; the test is here so that a change to a Boolean has to
        /// break something.
        /// </remarks>
        [Test]
        public void TheSummaryCarriesTheReasonAndNotABoolean()
        {

            var rooms = new RoomStore();

            var summary = rooms.Joining(TheRoom, "me");

            Assert.That(summary.CannotEncrypt, Is.Not.Null.And.Not.Empty,
                        "A room nobody has entered yet reported that it can be encrypted in.");

            Assert.That(summary.ToJSON().Value<String>("cannotEncrypt"),
                        Is.EqualTo(summary.CannotEncrypt),
                        "The reason does not reach the browser, so the page can only draw a lock " +
                        "and no explanation.");

        }

        #endregion

    }

}
