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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// Who may use the web page, and how a browser proves it afterwards.
    /// </summary>
    /// <remarks>
    /// The cookie is the whole of the web app's own security: whoever holds
    /// it reads the chats. So what is checked is what travels in it (a random
    /// token, nothing else), how it travels (HttpOnly, same site only), and
    /// that a token nobody issued opens nothing.
    /// </remarks>
    [TestFixture]
    public class WebSessionsTests
    {

        #region (private) Login(Username, Password) / RequestWith(CookieHeader)

        /// <summary>
        /// The login the sessions are built on: the password hashed, as it is
        /// in the file.
        /// </summary>
        private static WebLoginSettings Login(String Username, String Password)
        {
            Assert.That(WebLoginSettings.TryCreate(Username, Password, out var login, out var error), Is.True, error);
            return login!;
        }


        private static HTTPRequest RequestWith(String? CookieHeader)
        {

            var text = "GET /api/v1/chats HTTP/1.1\r\n" +
                       "Host: 127.0.0.1:8080\r\n" +
                       (CookieHeader is not null ? $"Cookie: {CookieHeader}\r\n" : "") +
                       "\r\n";

            Assert.That(HTTPRequest.TryParse(text, out var request), Is.True, "the test request itself has to parse");

            return request!;

        }

        #endregion


        #region OnlyTheRightPair_SignsIn()

        [Test]
        public void OnlyTheRightPair_SignsIn()
        {

            var sessions = new WebSessions(Login("admin", "change-me"));

            Assert.Multiple(() =>
            {
                Assert.That(sessions.TryLogin("admin",  "change-me",  out var session), Is.True);
                Assert.That(session?.UserId.ToString(),                                  Is.EqualTo("admin"));
                Assert.That(sessions.TryLogin("admin",  "wrong",      out _),           Is.False);
                Assert.That(sessions.TryLogin("Admin",  "change-me",  out _),           Is.False, "the username is a password too, and case counts");
                Assert.That(sessions.TryLogin("",       "change-me",  out _),           Is.False);
                Assert.That(sessions.TryLogin(null,     null,         out _),           Is.False);
                Assert.That(sessions.TryLogin("admin",  "change-me ", out _),           Is.False);
            });

        }

        #endregion

        #region TheCookie_CarriesTheTokenAndNothingReadable()

        [Test]
        public void TheCookie_CarriesTheTokenAndNothingReadable()
        {

            var sessions = new WebSessions(Login("admin", "change-me"), SecureCookies: true);

            sessions.TryLogin("admin", "change-me", out var session);

            var cookie = sessions.SessionCookie(session!).Single().ToString();

            Assert.Multiple(() =>
            {
                Assert.That(cookie, Does.StartWith($"XMPPWebApp={session!.Token}"));
                Assert.That(cookie, Does.Contain("; HttpOnly"));
                Assert.That(cookie, Does.Contain("; SameSite=strict"));
                Assert.That(cookie, Does.Contain("; Path=/"));
                Assert.That(cookie, Does.Contain("; secure"));
                Assert.That(cookie, Does.Not.Contain("admin"));
                Assert.That(cookie, Does.Not.Contain("change-me"));
            });

            var plain = new WebSessions(Login("admin", "change-me")).ExpiredCookie().Single().ToString();

            Assert.Multiple(() =>
            {
                Assert.That(plain, Does.StartWith("XMPPWebApp=;"));
                Assert.That(plain, Does.Contain("Expires=Thu, 01 Jan 1970"));
                Assert.That(plain, Does.Not.Contain("secure"), "without TLS a secure cookie would never come back");
            });

        }

        #endregion

        #region ARequestWithTheCookie_IsTheSession()

        [Test]
        public void ARequestWithTheCookie_IsTheSession()
        {

            var sessions = new WebSessions(Login("admin", "change-me"));

            sessions.TryLogin("admin", "change-me", out var session);

            var request = RequestWith($"other=1; XMPPWebApp={session!.Token}");

            Assert.Multiple(() =>
            {
                Assert.That(sessions.HasCookie(request),                   Is.True);
                Assert.That(sessions.TryGetSession(request, out var found), Is.True);
                Assert.That(found?.Token,                                  Is.EqualTo(session.Token));
            });

        }

        #endregion

        #region ATokenNobodyIssued_OpensNothing()

        [Test]
        public void ATokenNobodyIssued_OpensNothing()
        {

            var sessions = new WebSessions(Login("admin", "change-me"));

            sessions.TryLogin("admin", "change-me", out _);

            Assert.Multiple(() =>
            {
                Assert.That(sessions.TryGetSession(RequestWith(null),                                 out _), Is.False);
                Assert.That(sessions.TryGetSession(RequestWith("XMPPWebApp=not-a-token-anybody-made"), out _), Is.False);
                Assert.That(sessions.TryGetSession(RequestWith("XMPPWebApp="),                        out _), Is.False);
                Assert.That(sessions.HasCookie(RequestWith("XMPPWebApp=stale")),                              Is.True, "stale, but there - so that it can be expired");
                Assert.That(sessions.HasCookie(RequestWith("other=1")),                                       Is.False);
            });

        }

        #endregion

        #region SigningOut_EndsTheSession()

        [Test]
        public void SigningOut_EndsTheSession()
        {

            var sessions = new WebSessions(Login("admin", "change-me"));

            sessions.TryLogin("admin", "change-me", out var session);

            var request = RequestWith($"XMPPWebApp={session!.Token}");

            Assert.Multiple(() =>
            {
                Assert.That(sessions.SignOut(request),                 Is.True);
                Assert.That(sessions.TryGetSession(request, out _),    Is.False);
                Assert.That(sessions.SignOut(request),                 Is.False, "already gone");
                Assert.That(sessions.Store.Count,                      Is.EqualTo(0));
            });

        }

        #endregion

        #region AnIdleSession_Expires()

        [Test]
        public void AnIdleSession_Expires()
        {

            var sessions = new WebSessions(Login("admin", "change-me"), IdleTimeout: TimeSpan.FromMilliseconds(50));

            sessions.TryLogin("admin", "change-me", out var session);

            var request = RequestWith($"XMPPWebApp={session!.Token}");

            Assert.That(sessions.TryGetSession(request, out _), Is.True);

            Thread.Sleep(120);

            Assert.That(sessions.TryGetSession(request, out _), Is.False);

        }

        #endregion

    }

}
