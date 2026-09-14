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
    /// What it costs to try the web login.
    /// </summary>
    /// <remarks>
    /// The route this guards is the only one that does 600 000 rounds of
    /// PBKDF2 for somebody who has not signed in, so what is tested here is
    /// that both gates sit in front of that work and that neither of them can
    /// be walked around: the ration is per source and comes back with time, the
    /// ceiling counts what is happening at once and turns away what does not
    /// fit.
    ///
    /// The time is handed in rather than waited out, so a quarter of an hour
    /// costs nothing. The one exception is the ceiling, where a wait that MUST
    /// expire is timed for real - that is the safe direction for a clock in a
    /// test: the slot is genuinely held, so no amount of slowness can make the
    /// wait succeed.
    /// </remarks>
    [TestFixture]
    public class LoginThrottleTests
    {

        #region Data

        private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-14T12:00:00Z");

        #endregion


        #region ABurst_IsAllowed_AndThenIsNot()

        [Test]
        public void ABurst_IsAllowed_AndThenIsNot()
        {

            var throttle = new LoginThrottle(Attempts: 10, Window: TimeSpan.FromMinutes(15));

            for (var attempt = 1; attempt <= 10; attempt++)
                Assert.That(throttle.Ask("10.0.0.1", T0).Allowed, Is.True, $"attempt {attempt}");

            var refused = throttle.Ask("10.0.0.1", T0);

            Assert.Multiple(() =>
            {
                Assert.That(refused.Allowed,                      Is.False, "the eleventh in a row");
                Assert.That(refused.RetryAfter.TotalSeconds,      Is.EqualTo(90).Within(1),
                            "ten in a quarter of an hour is one every ninety seconds");
            });

        }

        #endregion

        #region AnotherSource_HasARationOfItsOwn()

        /// <summary>
        /// Or one machine guessing would lock out the person whose password it
        /// is guessing, which is a denial of service wearing the costume of a
        /// countermeasure.
        /// </summary>
        [Test]
        public void AnotherSource_HasARationOfItsOwn()
        {

            var throttle = new LoginThrottle(Attempts: 3, Window: TimeSpan.FromMinutes(15));

            for (var attempt = 1; attempt <= 3; attempt++)
                throttle.Ask("10.0.0.1", T0);

            Assert.Multiple(() =>
            {
                Assert.That(throttle.Ask("10.0.0.1", T0).Allowed, Is.False, "spent");
                Assert.That(throttle.Ask("10.0.0.2", T0).Allowed, Is.True,  "somebody else entirely");
                Assert.That(throttle.SourceCount,                 Is.EqualTo(2));
            });

        }

        #endregion

        #region TheRation_ComesBackWithTime()

        [Test]
        public void TheRation_ComesBackWithTime()
        {

            var throttle = new LoginThrottle(Attempts: 10, Window: TimeSpan.FromMinutes(15));

            for (var attempt = 1; attempt <= 10; attempt++)
                throttle.Ask("10.0.0.1", T0);

            Assert.Multiple(() =>
            {
                Assert.That(throttle.Ask("10.0.0.1", T0).Allowed,                             Is.False, "spent at once");
                Assert.That(throttle.Ask("10.0.0.1", T0.AddSeconds(89)).Allowed,              Is.False, "not quite yet");
                Assert.That(throttle.Ask("10.0.0.1", T0.AddSeconds(91)).Allowed,              Is.True,  "one back after ninety seconds");
                Assert.That(throttle.Ask("10.0.0.1", T0.AddSeconds(91)).Allowed,              Is.False, "and only the one");
                Assert.That(throttle.Ask("10.0.0.1", T0.AddMinutes(30)).Allowed,              Is.True,  "the whole burst after the window");
            });

        }

        #endregion

        #region TheCeiling_TurnsAwayWhatDoesNotFit()

        /// <summary>
        /// The ration is per source, so ten thousand sources are ten thousand
        /// rations and every one of them a core-second. This is the limit that
        /// does not care where the work came from.
        /// </summary>
        [Test]
        public async Task TheCeiling_TurnsAwayWhatDoesNotFit()
        {

            var throttle = new LoginThrottle(Verifiers:     1,
                                             VerifierWait:  TimeSpan.FromMilliseconds(50));

            Assert.That(await throttle.EnterVerifierAsync(), Is.True, "the first one goes straight through");

            Assert.That(await throttle.EnterVerifierAsync(), Is.False,
                        "the second waits for a slot that nobody gives back, and is told so");

            throttle.LeaveVerifier();

            Assert.That(await throttle.EnterVerifierAsync(), Is.True, "and it is a slot again afterwards");

            throttle.LeaveVerifier();

        }

        #endregion

        #region TheCeiling_LetsThroughWhatFits()

        [Test]
        public async Task TheCeiling_LetsThroughWhatFits()
        {

            var throttle = new LoginThrottle(Verifiers:     2,
                                             VerifierWait:  TimeSpan.FromMilliseconds(50));

            // Two plain assertions and not Assert.Multiple: an async lambda
            // handed to the synchronous overload is an async void, and its
            // assertions can land after the scope has already closed - a test
            // that passes without having looked.
            Assert.That(await throttle.EnterVerifierAsync(), Is.True);
            Assert.That(await throttle.EnterVerifierAsync(), Is.True);

            throttle.LeaveVerifier();
            throttle.LeaveVerifier();

        }

        #endregion

    }

}
