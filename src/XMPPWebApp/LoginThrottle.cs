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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// What it costs to try the web login: a ration per source, and a ceiling
    /// on how much of this machine all of them together may occupy.
    /// </summary>
    /// <remarks>
    /// Verifying the web password is 600 000 rounds of PBKDF2-SHA256, and that
    /// is deliberate - it is what makes the stored hash expensive to attack
    /// offline. The same number is what makes the sign-in route expensive to
    /// answer, and the route can be asked by anybody who can reach the port.
    ///
    /// <b>The half-second after a wrong password is not a rate limit.</b> It
    /// delays the answer, not the attempt: requests are served concurrently, so
    /// a thousand may be in flight and each waits out its own half second. It
    /// costs an attacker nothing but patience they do not need to have, and it
    /// never was meant to do more than take the speed out of a person guessing
    /// at a form.
    ///
    /// So two limits, because there are two different things to protect:
    ///
    /// <list type="bullet">
    /// <item>A <b>ration per source</b>, which is what stops guessing. Hermod's
    /// token bucket: a burst of <see cref="DefaultAttempts"/>, then one attempt
    /// per window/attempts. Whoever answers a password prompt wrongly twice is
    /// unaffected; whoever works through a word list is stopped after ten.</item>
    /// <item>A <b>ceiling on simultaneous verifications</b>, which is what stops
    /// the machine falling over. The bucket rations one source, and ten thousand
    /// sources are ten thousand buckets - a botnet would still get its free
    /// burst from each of them, and every one of those is a core-second. Behind
    /// this ceiling they queue instead, and what does not fit is turned away
    /// before any hashing happens.</item>
    /// </list>
    ///
    /// Two things this does not solve, said out loud rather than left to be
    /// discovered:
    ///
    /// The key is the remote address, so behind a reverse proxy every request
    /// arrives from the same one and shares a single ration - one attacker would
    /// lock everybody out. <c>X-Forwarded-For</c> is not read, and that is not an
    /// oversight: a header anybody may write is a ration anybody may sidestep,
    /// and reading it without being told whom to trust would be worse than not
    /// reading it at all.
    ///
    /// And the bucket registry is bounded - Hermod evicts idle buckets and
    /// refuses new ones past its maximum. A distributed attack wide enough to
    /// fill it therefore ends in everybody being refused rather than in an
    /// unbounded table. On a program that serves one person that is the right
    /// way round, but it is a lockout, not a shrug.
    /// </remarks>
    public sealed class LoginThrottle
    {

        #region Data

        /// <summary>
        /// How many attempts a single source may make back to back.
        /// </summary>
        public const           Int32     DefaultAttempts      = 10;

        /// <summary>
        /// The time in which those attempts are replenished - ten in a quarter
        /// of an hour, so one attempt every ninety seconds once the burst is
        /// spent.
        /// </summary>
        public static readonly TimeSpan  DefaultWindow        = TimeSpan.FromMinutes(15);

        /// <summary>
        /// How many passwords this program verifies at the same time, over all
        /// sources together.
        /// </summary>
        public const           Int32     DefaultVerifiers     = 2;

        /// <summary>
        /// How long a request waits for its turn before it is told the server
        /// is busy. Long enough that a queue behind one other verification is
        /// never noticed, short enough that nobody sits in a browser wondering.
        /// </summary>
        public static readonly TimeSpan  DefaultVerifierWait  = TimeSpan.FromSeconds(5);


        private readonly InMemoryTokenBucketRateLimiter  perSource;
        private readonly SemaphoreSlim                   verifiers;
        private readonly TimeSpan                        verifierWait;

        #endregion

        #region Properties

        /// <summary>
        /// How many sources are being counted at the moment.
        /// </summary>
        public Int32 SourceCount
            => perSource.BucketCount;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the ration and the ceiling for the web sign-in.
        /// </summary>
        /// <param name="Attempts">A burst of this many attempts per source.</param>
        /// <param name="Window">The time in which a spent burst is replenished.</param>
        /// <param name="Verifiers">How many passwords are verified at once, over all sources.</param>
        /// <param name="VerifierWait">How long to wait for a turn before answering "busy".</param>
        public LoginThrottle(Int32?     Attempts       = null,
                             TimeSpan?  Window         = null,
                             Int32?     Verifiers      = null,
                             TimeSpan?  VerifierWait   = null)
        {

            this.perSource     = new InMemoryTokenBucketRateLimiter(
                                     Capacity:      Attempts ?? DefaultAttempts,
                                     RefillPeriod:  Window   ?? DefaultWindow
                                 );

            this.verifiers     = new SemaphoreSlim(Verifiers    ?? DefaultVerifiers);
            this.verifierWait  = VerifierWait ?? DefaultVerifierWait;

        }

        #endregion


        #region (static) SourceOf(Request)

        /// <summary>
        /// What a request is rationed as: the address it came from.
        /// </summary>
        public static String SourceOf(HTTPRequest Request)

            => Request.RemoteSocket.IPAddress.ToString();

        #endregion

        #region Ask(Source, Timestamp = null)

        /// <summary>
        /// Take one attempt from this source's ration.
        /// </summary>
        /// <remarks>
        /// A correct password spends a token too. The gate has to sit in front
        /// of the work, and at that moment nobody knows yet whether the password
        /// is right - so what is rationed is the asking, not the being wrong.
        /// Ten sign-ins in a quarter of an hour is not a thing that happens to
        /// somebody who knows their password.
        /// </remarks>
        /// <param name="Source">The source, from <see cref="SourceOf"/>.</param>
        /// <param name="Timestamp">The time to reckon with; the clock unless a test says otherwise.</param>
        public RateLimitDecision Ask(String           Source,
                                     DateTimeOffset?  Timestamp   = null)

            => perSource.TryAcquire(Source, Timestamp);

        #endregion

        #region EnterVerifierAsync(CancellationToken = default) / LeaveVerifier()

        /// <summary>
        /// Wait for a turn at verifying a password. False when the wait was too
        /// long, and then nothing was taken and nothing has to be given back.
        /// </summary>
        public Task<Boolean> EnterVerifierAsync(CancellationToken CancellationToken = default)

            => verifiers.WaitAsync(verifierWait, CancellationToken);

        /// <summary>
        /// Give the turn back. In a finally, always.
        /// </summary>
        public void LeaveVerifier()

            => verifiers.Release();

        #endregion

    }

}
