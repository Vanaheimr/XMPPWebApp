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

using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// The login of the web page itself: the one door in front of the chats
    /// and the account page.
    /// </summary>
    /// <remarks>
    /// Unlike the XMPP password, this one is only ever compared against what
    /// somebody typed, so it is stored as a hash and nothing here should ever
    /// be able to get the password back out. The other half of the story is
    /// what happens when it changes: a browser signed in with the old password
    /// must not keep the page.
    /// </remarks>
    [TestFixture]
    public class WebLoginTests
    {

        #region (private) Login(Username, Password)

        private static WebLoginSettings Login(String Username, String Password)
        {
            Assert.That(WebLoginSettings.TryCreate(Username, Password, out var login, out var error), Is.True, error);
            return login!;
        }

        #endregion


        #region OnlyTheRightPair_Verifies()

        [Test]
        public void OnlyTheRightPair_Verifies()
        {

            var login = Login("admin", "change-me");

            Assert.Multiple(() =>
            {
                Assert.That(login.Verify("admin", "change-me"),   Is.True);
                Assert.That(login.Verify("admin", "Change-me"),   Is.False);
                Assert.That(login.Verify("Admin", "change-me"),   Is.False);
                Assert.That(login.Verify("",      "change-me"),   Is.False);
                Assert.That(login.Verify(null,    null),          Is.False);
                Assert.That(login.Verify("admin", "change-me "),  Is.False);
            });

        }

        #endregion

        #region WhatIsMissingOrTooShort_IsRefused()

        [Test]
        public void WhatIsMissingOrTooShort_IsRefused()
        {

            Assert.Multiple(() =>
            {

                Assert.That(WebLoginSettings.TryCreate("",      "long-enough", out _, out var e1), Is.False);
                Assert.That(e1, Does.Contain("username"));

                Assert.That(WebLoginSettings.TryCreate("   ",   "long-enough", out _, out _),      Is.False);

                Assert.That(WebLoginSettings.TryCreate("admin", "short",       out _, out var e2), Is.False);
                Assert.That(e2, Does.Contain("8"));

                Assert.That(WebLoginSettings.TryCreate("admin", null,          out _, out _),      Is.False);

            });

        }

        #endregion

        #region TheUsername_IsTrimmed()

        [Test]
        public void TheUsername_IsTrimmed()
        {
            Assert.That(Login("  admin  ", "change-me").Username, Is.EqualTo("admin"));
        }

        #endregion

        #region ThePasswordHash_GoesToTheFileAndNeverToTheBrowser()

        [Test]
        public void ThePasswordHash_GoesToTheFileAndNeverToTheBrowser()
        {

            var login       = Login("admin", "change-me");
            var forFile     = login.ToJSON(IncludePasswordHash: true);
            var forBrowser  = login.ToJSON(IncludePasswordHash: false);

            Assert.Multiple(() =>
            {
                Assert.That(forFile["username"]?.ToString(),  Is.EqualTo("admin"));
                Assert.That(forFile["password"]?.ToString(),  Does.StartWith("$pbkdf2-sha256$"));
                Assert.That(forFile.ToString(),               Does.Not.Contain("change-me"), "the file keeps a hash, not the password");

                Assert.That(forBrowser["password"],           Is.Null, "the browser gets no hash either");
                Assert.That(forBrowser["username"]?.ToString(), Is.EqualTo("admin"));
            });

        }

        #endregion

        #region TryParse_IsTheInverseOfToJSON()

        [Test]
        public void TryParse_IsTheInverseOfToJSON()
        {

            var original = Login("admin", "change-me");

            Assert.That(WebLoginSettings.TryParse(original.ToJSON(IncludePasswordHash: true), out var read, out var error), Is.True);
            Assert.That(error, Is.Null);

            Assert.Multiple(() =>
            {
                Assert.That(read!.Username,                    Is.EqualTo("admin"));
                Assert.That(read.Verify("admin", "change-me"),  Is.True, "the hash survived the round trip");
                Assert.That(read.Verify("admin", "wrong"),      Is.False);
            });

        }

        #endregion

        #region TryParse_RefusesWhatIsNotALogin()

        [Test]
        public void TryParse_RefusesWhatIsNotALogin()
        {

            Assert.Multiple(() =>
            {
                Assert.That(WebLoginSettings.TryParse(new Newtonsoft.Json.Linq.JObject(), out _, out _), Is.False);

                Assert.That(WebLoginSettings.TryParse(
                                new Newtonsoft.Json.Linq.JObject(
                                    new Newtonsoft.Json.Linq.JProperty("username", "admin"),
                                    new Newtonsoft.Json.Linq.JProperty("password", "not-a-phc-string")),
                                out _, out var error),
                            Is.False);

                Assert.That(error, Does.Contain("PHC"));
            });

        }

        #endregion

        #region AGeneratedLogin_HasAPasswordThatVerifies()

        /// <summary>
        /// What a first start does when it finds no file.
        /// </summary>
        [Test]
        public void AGeneratedLogin_HasAPasswordThatVerifies()
        {

            var (login, password)  = WebLoginSettings.Generate();
            var (other, otherPw)   = WebLoginSettings.Generate();

            Assert.Multiple(() =>
            {
                Assert.That(login.Username,                       Is.EqualTo("admin"));
                Assert.That(password,                             Has.Length.GreaterThanOrEqualTo(20));
                Assert.That(login.Verify("admin", password),      Is.True);
                Assert.That(login.Verify("admin", otherPw),       Is.False);
                Assert.That(password,                             Is.Not.EqualTo(otherPw), "not the same one twice");
                Assert.That(other.Verify("admin", password),      Is.False);
            });

        }

        #endregion

        #region ToString_IsTheUsernameAlone()

        [Test]
        public void ToString_IsTheUsernameAlone()
        {
            Assert.That(Login("admin", "change-me").ToString(), Is.EqualTo("admin"));
        }

        #endregion


        #region TheFile_RoundTripsAndDeletes()

        [Test]
        public void TheFile_RoundTripsAndDeletes()
        {

            var path = Path.Combine(Path.GetTempPath(), $"web-login-test-{Guid.NewGuid():N}.json");
            var file = new WebLoginFile(path);

            try
            {

                Assert.Multiple(() =>
                {
                    Assert.That(file.Exists,                                   Is.False);
                    Assert.That(file.TryLoad(out _, out var noError),           Is.False);
                    Assert.That(noError,                                        Is.Null, "no file is the normal first start");
                });

                file.Save(Login("achim", "change-me"));

                Assert.That(file.TryLoad(out var loaded, out var error), Is.True);
                Assert.That(error, Is.Null);

                Assert.Multiple(() =>
                {
                    Assert.That(loaded!.Username,                     Is.EqualTo("achim"));
                    Assert.That(loaded.Verify("achim", "change-me"),  Is.True);
                    Assert.That(File.ReadAllText(path),               Does.Not.Contain("change-me"));
                });

                Assert.Multiple(() =>
                {
                    Assert.That(file.Delete(),  Is.True);
                    Assert.That(file.Delete(),  Is.False, "already gone");
                });

            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }

        }

        #endregion

        #region ADamagedFile_IsAnError()

        [Test]
        public void ADamagedFile_IsAnError()
        {

            var path = Path.Combine(Path.GetTempPath(), $"web-login-test-{Guid.NewGuid():N}.json");

            try
            {

                File.WriteAllText(path, "{ not json");

                Assert.That(new WebLoginFile(path).TryLoad(out _, out var error), Is.False);
                Assert.That(error, Is.Not.Null);

            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }

        }

        #endregion


        #region ChangingTheLogin_EndsEveryOtherSession()

        /// <summary>
        /// The point of asking for the current password on the settings page:
        /// after the change, the browsers that were signed in with the old one
        /// are out - except the one that made the change.
        /// </summary>
        [Test]
        public void ChangingTheLogin_EndsEveryOtherSession()
        {

            var sessions = new WebSessions(Login("admin", "change-me"));

            sessions.TryLogin("admin", "change-me", out var mine);
            sessions.TryLogin("admin", "change-me", out var other);
            sessions.TryLogin("admin", "change-me", out _);

            Assert.That(sessions.Store.Count, Is.EqualTo(3));

            var ended = sessions.UpdateLogin(Login("admin", "a-new-one"), mine!.Token);

            Assert.Multiple(() =>
            {
                Assert.That(ended,                                          Is.EqualTo(2));
                Assert.That(sessions.Store.TryGet(mine.Token,  out _),       Is.True,  "the session that changed it stays");
                Assert.That(sessions.Store.TryGet(other!.Token, out _),      Is.False, "the others are out");
                Assert.That(sessions.TryLogin("admin", "change-me", out _),  Is.False, "the old password is gone");
                Assert.That(sessions.TryLogin("admin", "a-new-one", out _),  Is.True);
            });

        }

        #endregion

        #region ChangingOnlyTheUsername_KeepsThePassword()

        [Test]
        public void ChangingOnlyTheUsername_KeepsThePassword()
        {

            var sessions = new WebSessions(Login("admin", "change-me"));

            // What the API does when the new password is left blank: the record
            // keeps its hash and only the name changes.
            sessions.UpdateLogin(sessions.Login with { Username = "achim" });

            Assert.Multiple(() =>
            {
                Assert.That(sessions.Username,                              Is.EqualTo("achim"));
                Assert.That(sessions.TryLogin("achim", "change-me", out _),  Is.True);
                Assert.That(sessions.TryLogin("admin", "change-me", out _),  Is.False);
            });

        }

        #endregion

    }

}
