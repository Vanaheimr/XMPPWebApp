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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// What this application does with a message that arrived encrypted
    /// (XEP-0384).
    /// </summary>
    /// <remarks>
    /// Nothing here encrypts or decrypts anything: the cryptography lives in
    /// Ratatoskr and is tested there, against a real server, with the check
    /// that matters most - that the plaintext stands in no stanza. What is
    /// tested here is everything this repository decides <i>afterwards</i>, and
    /// those decisions are the ones a reader of the page sees:
    ///
    /// <list type="bullet">
    /// <item>which conversation the message belongs in, and which way it went -
    ///       the case that goes wrong quietly is one's own second device,</item>
    /// <item>whether the line still says a year later how it arrived,</item>
    /// <item>and whether a correction can turn a locked line into an open one
    ///       without the lock coming off.</item>
    /// </list>
    /// </remarks>
    [TestFixture]
    public class OmemoTests
    {

        private static readonly JID             me     = JID.Parse("me@example.org");
        private static readonly JID             alice  = JID.Parse("alice@example.org");
        private static readonly DateTimeOffset  noon   = new (2026, 9, 12, 12, 0, 0, TimeSpan.Zero);


        #region AMessageFromAContact_IsIncomingIntoTheirChat()

        [Test]
        public void AMessageFromAContact_IsIncomingIntoTheirChat()
        {

            var message = new XMPPMessage(
                              JID.Parse("alice@example.org/phone"),
                              JID.Parse("me@example.org/web"),
                              "Shall we meet at eight?",
                              "m1",
                              noon.UtcDateTime,
                              MessageType.Chat
                          );

            var (chat, outgoing) = XMPPWebAPI.EncryptedBelongsTo(message, me);

            Assert.Multiple(() =>
            {
                Assert.That(chat,      Is.EqualTo(alice), "the conversation is with the sender");
                Assert.That(outgoing,  Is.False);
            });

        }

        #endregion

        #region ACarbonOfOurOwnDevice_IsOutgoingIntoTheChatItWasAddressedTo()

        /// <summary>
        /// The case the whole split exists for.
        /// </summary>
        /// <remarks>
        /// OMEMO encrypts to one's own further devices - that is what lets this
        /// device read what the telephone in one's pocket wrote. The carbon
        /// therefore arrives decryptable and with <b>our own account as the
        /// sender</b>. Filed by the sender alone it would open a conversation
        /// with oneself and put one's own sentence into it, attributed to a
        /// stranger; the person it was actually written to would see nothing.
        /// </remarks>
        [Test]
        public void ACarbonOfOurOwnDevice_IsOutgoingIntoTheChatItWasAddressedTo()
        {

            var message = new XMPPMessage(
                              JID.Parse("me@example.org/phone"),
                              JID.Parse("alice@example.org"),
                              "Eight suits me.",
                              "m2",
                              noon.UtcDateTime,
                              MessageType.Chat
                          );

            var (chat, outgoing) = XMPPWebAPI.EncryptedBelongsTo(message, me);

            Assert.Multiple(() =>
            {
                Assert.That(chat,      Is.EqualTo(alice), "the conversation is the one it was written to, not one with ourselves");
                Assert.That(outgoing,  Is.True);
            });

        }

        #endregion

        #region WithoutAnAccount_NothingIsOurs()

        /// <summary>
        /// No account configured: nothing can be our own, so everything that
        /// arrives is something somebody else wrote.
        /// </summary>
        /// <remarks>
        /// Hard to reach - a client without an account decrypts nothing - and
        /// the answer still has to be the safe one. Calling a stranger's
        /// message our own would show it as sent by this user.
        /// </remarks>
        [Test]
        public void WithoutAnAccount_NothingIsOurs()
        {

            var message = new XMPPMessage(
                              JID.Parse("me@example.org/phone"),
                              JID.Parse("alice@example.org"),
                              "Eight suits me.",
                              "m3",
                              noon.UtcDateTime,
                              MessageType.Chat
                          );

            var (chat, outgoing) = XMPPWebAPI.EncryptedBelongsTo(message, null);

            Assert.Multiple(() =>
            {
                Assert.That(chat,      Is.EqualTo(me));
                Assert.That(outgoing,  Is.False);
            });

        }

        #endregion


        #region AnEncryptedLine_SaysSoInTheArchiveAsWell()

        /// <summary>
        /// How a line arrived is written down and read back, because the
        /// archive is what is left of a conversation a year later.
        /// </summary>
        [Test]
        public void AnEncryptedLine_SaysSoInTheArchiveAsWell()
        {

            var store    = new ChatStore();
            var written  = store.AddIncoming(alice, "alice@example.org/phone", "m1", "Shall we meet at eight?", noon,
                                             Identity: OmemoIdentityCheck.Changed);

            Assert.Multiple(() =>
            {
                Assert.That(written.Identity,   Is.EqualTo(OmemoIdentityCheck.Changed));
                Assert.That(written.Encrypted,  Is.True);
            });

            var json = written.ToJSON();

            Assert.Multiple(() =>
            {
                Assert.That(json.Value<Boolean>("encrypted"),  Is.True);
                Assert.That(json.Value<String>("identity"),    Is.EqualTo("changed"));
            });

            Assert.That(ChatMessage.TryParse(json, alice, out var read), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(read!.Identity,   Is.EqualTo(OmemoIdentityCheck.Changed));
                Assert.That(read.Encrypted,   Is.True);
            });

        }

        #endregion

        #region APlainLine_CarriesNoLock()

        [Test]
        public void APlainLine_CarriesNoLock()
        {

            var store    = new ChatStore();
            var written  = store.AddIncoming(alice, "alice@example.org/phone", "m1", "Shall we meet at eight?", noon);
            var json     = written.ToJSON();

            Assert.Multiple(() =>
            {
                Assert.That(written.Encrypted,                 Is.False);
                Assert.That(json.Value<Boolean>("encrypted"),  Is.False);
                Assert.That(json["identity"]?.Type,            Is.EqualTo(JTokenType.Null));
            });

        }

        #endregion

        #region AnArchiveLineWithANonsenseIdentity_IsReadAsNotEncrypted()

        /// <summary>
        /// A value this version does not know becomes "came in the clear" and
        /// not a guess.
        /// </summary>
        /// <remarks>
        /// Both ways of being wrong are wrong, and only one of them tells
        /// somebody their conversation was protected when it was not. The line
        /// itself is still shown - what it says is worth reading either way.
        /// </remarks>
        [Test]
        public void AnArchiveLineWithANonsenseIdentity_IsReadAsNotEncrypted()
        {

            var line = new JObject(
                           new JProperty("id",         "m1"),
                           new JProperty("chat",       alice.ToString()),
                           new JProperty("direction",  "in"),
                           new JProperty("from",       "alice@example.org/phone"),
                           new JProperty("body",       "Shall we meet at eight?"),
                           new JProperty("timestamp",  noon.ToString("o")),
                           new JProperty("encrypted",  true),
                           new JProperty("identity",   "verified-by-a-later-version")
                       );

            Assert.That(ChatMessage.TryParse(line, alice, out var read), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(read!.Body,      Is.EqualTo("Shall we meet at eight?"), "the line is still shown");
                Assert.That(read.Identity,   Is.Null);
                Assert.That(read.Encrypted,  Is.False, "a lock that rests on nothing is no lock");
            });

        }

        #endregion

        #region AnArchiveLineWithANumberForAnIdentity_IsReadAsNotEncrypted()

        /// <summary>
        /// The same again for the value that parses and still means nothing.
        /// </summary>
        /// <remarks>
        /// Separate from the test above because it fails for a different
        /// reason. Enum.TryParse takes a number as well as a name: "7" comes
        /// back as true, with the value 7, which is none of the three. Only the
        /// IsDefined beside it turns that into null - and an identity of 7
        /// would otherwise be a locked line whose lock describes nothing.
        /// </remarks>
        [Test]
        public void AnArchiveLineWithANumberForAnIdentity_IsReadAsNotEncrypted()
        {

            var line = new JObject(
                           new JProperty("id",         "m1"),
                           new JProperty("chat",       alice.ToString()),
                           new JProperty("direction",  "in"),
                           new JProperty("from",       "alice@example.org/phone"),
                           new JProperty("body",       "Shall we meet at eight?"),
                           new JProperty("timestamp",  noon.ToString("o")),
                           new JProperty("identity",   "7")
                       );

            Assert.That(ChatMessage.TryParse(line, alice, out var read), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(read!.Identity,   Is.Null);
                Assert.That(read.Encrypted,   Is.False);
            });

        }

        #endregion


        #region APlaintextCorrection_TakesTheLockOffTheLineItReplaces()

        /// <summary>
        /// A correction replaces the text; the lock belongs to the text and not
        /// to the line.
        /// </summary>
        /// <remarks>
        /// XEP-0308 lets a sender replace what they wrote, and nothing obliges
        /// the replacement to travel the way the original did. If the line kept
        /// its lock, the words displayed would be words that came in the clear
        /// under a mark saying they did not.
        /// </remarks>
        [Test]
        public void APlaintextCorrection_TakesTheLockOffTheLineItReplaces()
        {

            var store = new ChatStore();

            store.AddIncoming(alice, "alice@example.org/phone", "m1", "Shall we meet at eight?", noon,
                              Identity: OmemoIdentityCheck.Known);

            var corrected = store.AddIncoming(alice, "alice@example.org/phone", "m2", "Shall we meet at nine?", noon,
                                              Corrects: "m1");

            Assert.Multiple(() =>
            {
                Assert.That(corrected.Id,         Is.EqualTo("m1"), "the correction replaced the line it named");
                Assert.That(corrected.Body,       Is.EqualTo("Shall we meet at nine?"));
                Assert.That(corrected.Corrected,  Is.True);
                Assert.That(corrected.Encrypted,  Is.False, "the words shown arrived in the clear, so the line does not claim otherwise");
            });

        }

        #endregion

        #region AnEncryptedCorrection_KeepsTheLock()

        [Test]
        public void AnEncryptedCorrection_KeepsTheLock()
        {

            var store = new ChatStore();

            store.AddIncoming(alice, "alice@example.org/phone", "m1", "Shall we meet at eight?", noon,
                              Identity: OmemoIdentityCheck.New);

            var corrected = store.AddIncoming(alice, "alice@example.org/phone", "m2", "Shall we meet at nine?", noon,
                                              Corrects: "m1",
                                              Identity: OmemoIdentityCheck.Known);

            Assert.Multiple(() =>
            {
                Assert.That(corrected.Body,       Is.EqualTo("Shall we meet at nine?"));
                Assert.That(corrected.Encrypted,  Is.True);
                Assert.That(corrected.Identity,   Is.EqualTo(OmemoIdentityCheck.Known), "and it is the new message's rating, not the old one's");
            });

        }

        #endregion

    }

}
