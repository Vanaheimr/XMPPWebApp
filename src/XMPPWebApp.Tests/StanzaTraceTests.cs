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

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// The XML debug trace: what it keeps, what it throws away, and the one
    /// thing it must never show.
    /// </summary>
    /// <remarks>
    /// <b>The redaction is the part that matters.</b> A stanza trace on a web
    /// page is every word of every conversation, which is what somebody
    /// switching it on is asking for. The SASL exchange is not: with SCRAM it
    /// is a proof, with PLAIN - which --insecure permits - it is the password
    /// itself, base64 and nothing else. A debug view that put that on a page
    /// would be a worse hole than anything it helps to find.
    /// </remarks>
    [TestFixture]
    public class StanzaTraceTests
    {

        #region ASaslExchange_LosesItsPayloadAndKeepsItsShape()

        /// <summary>
        /// The one that would be a credential on a web page.
        /// </summary>
        /// <remarks>
        /// PLAIN is checked rather than SCRAM because it is the case that
        /// actually hurts: the base64 there decodes to the password. The
        /// mechanism attribute survives, and has to - which mechanism was
        /// chosen is most of what anybody debugs here, and none of it is in the
        /// payload.
        /// </remarks>
        [Test]
        public void ASaslExchange_LosesItsPayloadAndKeepsItsShape()
        {

            // "\0janus3\0hunter2" in base64 - what PLAIN puts on the wire.
            const String secret = "AGphbnVzMwBodW50ZXIy";

            var redacted = StanzaTrace.Redact(
                               $"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>{secret}</auth>"
                           );

            Assert.Multiple(() =>
            {

                Assert.That(redacted, Does.Not.Contain(secret),
                            "the password went onto the page");

                Assert.That(redacted, Does.Contain("mechanism=\"PLAIN\""),
                            "which mechanism it was is the point of looking");

                Assert.That(redacted, Does.Contain("auth"));

            });

        }

        #endregion

        #region EveryPartOfTheExchange_IsEmptied()

        /// <summary>
        /// Not just the first element: a SASL exchange is four or five frames
        /// and the proof is in the middle ones.
        /// </summary>
        [TestCase("<challenge xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>cj1mOTJhBHJhbmRvbQ==</challenge>",   "cj1mOTJhBHJhbmRvbQ==")]
        [TestCase("<response xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>Yz1iaXdzLHI9ZjkyYQ==</response>",      "Yz1iaXdzLHI9ZjkyYQ==")]
        [TestCase("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>dj1ybUY5cHFWOFM3c3U=</success>",        "dj1ybUY5cHFWOFM3c3U=")]
        public void EveryPartOfTheExchange_IsEmptied(String Frame, String Payload)
        {

            Assert.That(StanzaTrace.Redact(Frame), Does.Not.Contain(Payload));

        }

        #endregion

        #region ANestedPayload_IsReachedToo()

        /// <summary>
        /// SASL 2 wraps its payload a level down (XEP-0388).
        /// </summary>
        /// <remarks>
        /// Written because a redaction that only empties the outermost element
        /// looks right on every frame above and misses this one entirely - and
        /// this is the version the newer servers negotiate.
        /// </remarks>
        [Test]
        public void ANestedPayload_IsReachedToo()
        {

            const String secret = "AGphbnVzMwBodW50ZXIy";

            var redacted = StanzaTrace.Redact(
                               "<authenticate xmlns='urn:xmpp:sasl:2' mechanism='PLAIN'>" +
                                   $"<initial-response>{secret}</initial-response>" +
                                   "<user-agent id='d4213f'><software>XMPPWebApp</software></user-agent>" +
                               "</authenticate>"
                           );

            Assert.That(redacted, Does.Not.Contain(secret),
                        "the payload one level down was left standing");

        }

        #endregion

        #region AnOrdinaryStanza_IsLeftAlone()

        /// <summary>
        /// Everything that is not SASL passes through unchanged - that is what
        /// the view is for.
        /// </summary>
        [Test]
        public void AnOrdinaryStanza_IsLeftAlone()
        {

            const String message = "<message to='alice@example.org' type='chat' id='m1'><body>Shall we meet at eight?</body></message>";

            Assert.That(StanzaTrace.Redact(message), Is.EqualTo(message));

        }

        #endregion

        #region ASaslFrameThatWillNotParse_IsNotShownAtAll()

        /// <summary>
        /// The one case where this cannot see what it is holding.
        /// </summary>
        /// <remarks>
        /// Guessing on the side of showing it would be the wrong guess: a frame
        /// that mentions the SASL namespace and will not parse is exactly the
        /// shape a truncated auth frame has.
        /// </remarks>
        [Test]
        public void ASaslFrameThatWillNotParse_IsNotShownAtAll()
        {

            const String secret = "AGphbnVzMwBodW50ZXIy";

            var redacted = StanzaTrace.Redact($"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>{secret}");

            Assert.Multiple(() =>
            {
                Assert.That(redacted, Does.Not.Contain(secret));
                Assert.That(redacted, Does.Contain("could not"));
            });

        }

        #endregion

        #region TheMechanismList_Survives()

        /// <summary>
        /// The one thing inside a SASL frame that is worth reading.
        /// </summary>
        /// <remarks>
        /// "Why did it negotiate PLAIN" is answered by the mechanism list in
        /// the stream features, and that list lives in the SASL namespace - so
        /// a redaction that empties every text node in a SASL frame blanks
        /// exactly the line somebody opened this view for. The allowlist is
        /// what keeps it, and this is what holds the allowlist in place.
        /// </remarks>
        [Test]
        public void TheMechanismList_Survives()
        {

            var redacted = StanzaTrace.Redact(
                               "<mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>" +
                                   "<mechanism>SCRAM-SHA-256</mechanism>" +
                                   "<mechanism>PLAIN</mechanism>" +
                               "</mechanisms>"
                           );

            Assert.Multiple(() =>
            {
                Assert.That(redacted, Does.Contain("SCRAM-SHA-256"));
                Assert.That(redacted, Does.Contain("PLAIN"));
            });

        }

        #endregion


        #region SwitchedOff_NothingIsKept()

        [Test]
        public void SwitchedOff_NothingIsKept()
        {

            var trace = new StanzaTrace();

            Assert.Multiple(() =>
            {
                Assert.That(trace.Enabled,                            Is.False, "a trace records only once somebody asks");
                Assert.That(trace.Record("<<< <message/>"),           Is.Null);
                Assert.That(trace.Snapshot(),                         Is.Empty);
            });

        }

        #endregion

        #region SwitchingItOff_ThrowsAwayWhatWasKept()

        /// <summary>
        /// Off means gone, not paused.
        /// </summary>
        /// <remarks>
        /// A trace that is "off" and still holds the last conversation is the
        /// opposite of what switching it off is for - and whoever switched it
        /// off is the one person entitled to assume it is no longer there.
        /// </remarks>
        [Test]
        public void SwitchingItOff_ThrowsAwayWhatWasKept()
        {

            var trace = new StanzaTrace { Enabled = true };

            trace.Record("<<< <message><body>Shall we meet at eight?</body></message>");

            Assert.That(trace.Snapshot(), Has.Count.EqualTo(1));

            trace.Enabled = false;

            Assert.That(trace.Snapshot(), Is.Empty,
                        "what was recorded is still lying there after switching off");

        }

        #endregion

        #region TheDirection_ComesOffThePrefix()

        [Test]
        public void TheDirection_ComesOffThePrefix()
        {

            var trace = new StanzaTrace { Enabled = true };

            var sent      = trace.Record(">>> <message id='m1'/>");
            var received  = trace.Record("<<< <message id='m2'/>");

            Assert.Multiple(() =>
            {

                Assert.That(sent!.Outgoing,      Is.True);
                Assert.That(sent.XML,            Is.EqualTo("<message id='m1'/>"), "the prefix is not part of the stanza");

                Assert.That(received!.Outgoing,  Is.False);
                Assert.That(received.XML,        Is.EqualTo("<message id='m2'/>"));

                Assert.That(received.Sequence,   Is.GreaterThan(sent.Sequence));

            });

        }

        #endregion

        #region ItKeepsOnlyItsCapacity()

        /// <summary>
        /// The oldest go, and the list stops growing.
        /// </summary>
        /// <remarks>
        /// A live connection sends a keepalive every half minute and a presence
        /// for every contact that changes. An unbounded trace is the same
        /// unbounded growth the security review found elsewhere, only this one
        /// is fed by the server.
        /// </remarks>
        [Test]
        public void ItKeepsOnlyItsCapacity()
        {

            var trace = new StanzaTrace(Capacity: 3) { Enabled = true };

            for (var i = 1; i <= 10; i++)
                trace.Record($"<<< <message id='m{i}'/>");

            var kept = trace.Snapshot();

            Assert.Multiple(() =>
            {
                Assert.That(kept,              Has.Count.EqualTo(3));
                Assert.That(kept[0].XML,       Does.Contain("m8"), "the oldest should have gone first");
                Assert.That(kept[^1].XML,      Does.Contain("m10"));
            });

        }

        #endregion

    }

}
