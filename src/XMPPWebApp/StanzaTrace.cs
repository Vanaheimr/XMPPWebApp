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

using System.Xml.Linq;

using Newtonsoft.Json.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// One stanza as it went over the wire.
    /// </summary>
    /// <param name="Sequence">Counts up; the browser uses it to tell a replay from something new.</param>
    /// <param name="Timestamp">When it passed through.</param>
    /// <param name="Outgoing">True for what this client sent.</param>
    /// <param name="XML">The stanza, with the SASL exchange emptied out - see <see cref="StanzaTrace.Redact"/>.</param>
    public sealed record TracedStanza(Int64           Sequence,
                                      DateTimeOffset  Timestamp,
                                      Boolean         Outgoing,
                                      String          XML)
    {

        public JObject ToJSON()

            => new (
                   new JProperty("seq",        Sequence),
                   new JProperty("timestamp",  Timestamp.ToString("o")),
                   new JProperty("direction",  Outgoing ? "out" : "in"),
                   new JProperty("xml",        XML)
               );

    }


    /// <summary>
    /// What went over the wire, for whoever has to find out why something does
    /// not work.
    /// </summary>
    /// <remarks>
    /// The same thing <c>--verbose</c> writes to the console, kept where the
    /// page can show it. Ratatoskr raises every frame on
    /// <c>XMPPClient.OnRawXml</c> already; this only decides what is kept and
    /// what a browser gets to see.
    ///
    /// <b>Off until somebody switches it on.</b> Not out of thrift - the cost
    /// of keeping five hundred stanzas is nothing - but because a stanza trace
    /// is every word of every conversation, the whole roster, and who is
    /// online. That belongs on a page only while somebody is looking for
    /// something.
    ///
    /// <b>Bounded, and it has to be.</b> A long-lived connection sends a
    /// keepalive every half minute and a presence for every contact that
    /// changes; an unbounded list is the same unbounded growth the security
    /// review found elsewhere, only this one is fed by the server.
    /// </remarks>
    public sealed class StanzaTrace
    {

        #region Data

        /// <summary>
        /// How many stanzas are kept.
        /// </summary>
        /// <remarks>
        /// Enough to hold a sign-in with its stream features, the roster and a
        /// conversation's worth of traffic - which is the window somebody
        /// actually reads. Whoever needs more than this needs
        /// <c>--verbose</c> and a file.
        /// </remarks>
        public const Int32 DefaultCapacity = 500;

        /// <summary>
        /// The elements inside a SASL exchange whose text may be shown.
        /// </summary>
        /// <remarks>
        /// Everything else is emptied, including elements this version does
        /// not know - see <see cref="Redact"/> for why that is the way round
        /// it has to be.
        /// </remarks>
        private static readonly HashSet<String> Harmless = new (StringComparer.Ordinal) {
            "mechanism",
            "mechanisms"
        };

        private readonly Queue<TracedStanza>  stanzas  = new();
        private readonly Lock                 @lock    = new();
        private          Int64                sequence;
        private          Boolean              enabled;

        #endregion

        #region Properties

        /// <summary>
        /// How many stanzas this keeps before the oldest one goes.
        /// </summary>
        public Int32 Capacity { get; }

        /// <summary>
        /// Whether anything is being recorded at all.
        /// </summary>
        /// <remarks>
        /// Switching it off also throws away what was recorded. Leaving it to
        /// lie would mean a trace that is "off" and still holds every word of
        /// the last conversation, which is the opposite of what switching it
        /// off is for.
        /// </remarks>
        public Boolean Enabled
        {

            get
            {
                lock (@lock)
                    return enabled;
            }

            set
            {
                lock (@lock)
                {

                    enabled = value;

                    if (!value)
                        stanzas.Clear();

                }
            }

        }

        /// <summary>
        /// The sequence number of the most recent stanza.
        /// </summary>
        public Int64 Sequence
            => Interlocked.Read(ref sequence);

        #endregion

        #region Constructor(s)

        public StanzaTrace(Int32 Capacity = DefaultCapacity)
        {

            if (Capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(Capacity), "A trace has to keep at least one stanza!");

            this.Capacity = Capacity;

        }

        #endregion


        #region Record(Raw)

        /// <summary>
        /// Takes one frame as Ratatoskr raises it - prefixed "&gt;&gt;&gt; " for
        /// what went out and "&lt;&lt;&lt; " for what came in.
        /// </summary>
        /// <returns>The line as it was kept, or null when nothing is being recorded.</returns>
        public TracedStanza? Record(String Raw)
        {

            if (!Enabled)
                return null;

            var outgoing  = Raw.StartsWith(">>>", StringComparison.Ordinal);
            var xml       = Raw.Length > 4 && (outgoing || Raw.StartsWith("<<<", StringComparison.Ordinal))
                                ? Raw[4..]
                                : Raw;

            var stanza = new TracedStanza(
                             Interlocked.Increment(ref sequence),
                             DateTimeOffset.UtcNow,
                             outgoing,
                             Redact(xml)
                         );

            lock (@lock)
            {

                // Checked again inside the lock: switching off clears the
                // queue, and a frame that arrived in between would otherwise
                // be the one thing left in a trace that is supposed to be gone.
                if (!enabled)
                    return null;

                stanzas.Enqueue(stanza);

                while (stanzas.Count > Capacity)
                    stanzas.Dequeue();

            }

            return stanza;

        }

        #endregion

        #region Snapshot() / Clear()

        /// <summary>
        /// What is kept, oldest first.
        /// </summary>
        public IReadOnlyList<TracedStanza> Snapshot()
        {
            lock (@lock)
                return [.. stanzas];
        }

        /// <summary>
        /// Throws away what was recorded and goes on recording.
        /// </summary>
        public void Clear()
        {
            lock (@lock)
                stanzas.Clear();
        }

        #endregion

        #region (internal, static) Redact(XML)

        /// <summary>
        /// The stanza with the content of the SASL exchange taken out.
        /// </summary>
        /// <remarks>
        /// <b>Not optional, and not a setting.</b> With SCRAM what travels here
        /// is a proof rather than the password; with PLAIN - which
        /// <c>--insecure</c> permits for a server on the same machine - it
        /// <i>is</i> the password, base64 and nothing else. A debug view that
        /// put that on a web page would be a worse hole than anything it helps
        /// to find.
        ///
        /// <b>The elements and their attributes stay.</b> Which mechanism was
        /// offered, which was chosen, how many round trips it took, whether it
        /// ended in success or failure - that is the whole of what anybody
        /// debugs here, and none of it is in the payload. Only the text is
        /// replaced.
        ///
        /// A frame that will not parse is replaced entirely rather than passed
        /// through. It is the one case where this cannot see what it is holding,
        /// and guessing on the side of showing it is the wrong guess.
        /// </remarks>
        internal static String Redact(String XML)
        {

            // The cheap test first: almost no frame is a SASL one, and this
            // runs on every stanza of a live connection.
            //
            // Two namespaces, and the second is the one that was missed:
            // XEP-0388 calls itself "urn:xmpp:sasl:2", in which the string
            // "xmpp-sasl" does not occur at all. A check for the older spelling
            // alone let a whole SASL 2 exchange through unredacted - password
            // included, because PLAIN is PLAIN in either version.
            if (XML.IndexOf("xmpp-sasl",  StringComparison.OrdinalIgnoreCase) < 0 &&
                XML.IndexOf("xmpp:sasl:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return XML;
            }

            try
            {

                var element = XElement.Parse(XML, LoadOptions.PreserveWhitespace);

                foreach (var node in element.DescendantsAndSelf())
                {

                    // An allowlist of the harmless, not a denylist of the
                    // dangerous - so that an element nobody here has heard of
                    // is emptied rather than shown. The mechanism names are
                    // what somebody debugging a negotiation actually reads
                    // ("why did it choose PLAIN"), and they carry nothing.
                    if (Harmless.Contains(node.Name.LocalName))
                        continue;

                    foreach (var text in node.Nodes().OfType<XText>().ToList())
                        if (text.Value.Trim().Length > 0)
                            text.Value = "[redacted]";

                }

                return element.ToString(SaveOptions.DisableFormatting);

            }
            catch (Exception)
            {
                return "<!-- a SASL frame this view could not parse, and therefore does not show -->";
            }

        }

        #endregion

    }

}
