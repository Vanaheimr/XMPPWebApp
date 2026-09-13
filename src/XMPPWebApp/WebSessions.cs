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

using System.Diagnostics.CodeAnalysis;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// Who may use the web page: one username, one password, and a session
    /// cookie for every browser that got both right.
    /// </summary>
    /// <remarks>
    /// The account database of Hermod's HTTPExtAPI stood here before, with
    /// sign-up, profiles and passkeys. A web front end for a single XMPP
    /// account has one user, and that one lives in the web login file - so what
    /// is left of the accounts is the part that was never optional: a
    /// comparison that takes the same time whether the first or the last
    /// character is wrong, a random token that only ever travels as an HttpOnly
    /// cookie, and a session that ends when nobody has used it for a while. The
    /// tokens themselves are kept by Hermod's <see cref="SessionStore"/>, which
    /// already knows how to make and expire them.
    ///
    /// The login is not for the lifetime of the process either: the settings
    /// page replaces it, and <see cref="UpdateLogin"/> ends every other session
    /// when it does.
    /// </remarks>
    public sealed class WebSessions
    {

        #region Data

        /// <summary>
        /// The default name of the session cookie.
        /// </summary>
        public static readonly HTTPCookieName  DefaultCookieName        = HTTPCookieName.Parse("XMPPWebApp");

        /// <summary>
        /// The default idle timeout: a session ends when it was not used for this long.
        /// </summary>
        public static readonly TimeSpan        DefaultIdleTimeout       = TimeSpan.FromHours(12);

        /// <summary>
        /// The default maximum lifetime of a session.
        /// </summary>
        public static readonly TimeSpan        DefaultMaximumLifetime   = TimeSpan.FromDays(7);

        #endregion

        #region Properties

        /// <summary>
        /// The login in force: the one username and the hash of its password.
        /// </summary>
        public WebLoginSettings  Login          { get; private set; }

        /// <summary>
        /// The one username.
        /// </summary>
        public String          Username
            => Login.Username;

        /// <summary>
        /// The name of the session cookie.
        /// </summary>
        public HTTPCookieName  CookieName       { get; }

        /// <summary>
        /// Whether the cookie is marked "secure", i.e. only ever sent over TLS.
        /// </summary>
        public Boolean         SecureCookies    { get; }

        /// <summary>
        /// The live sessions.
        /// </summary>
        public SessionStore    Store            { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the sessions of the one user.
        /// </summary>
        /// <param name="Login">The login: the username and the hash of its password.</param>
        /// <param name="SecureCookies">Whether the cookie is only ever sent over TLS; true when the server speaks TLS.</param>
        /// <param name="CookieName">The name of the session cookie.</param>
        /// <param name="IdleTimeout">A session ends when it was not used for this long; 12 hours by default.</param>
        /// <param name="MaximumLifetime">A session ends this long after the sign-in at the latest; 7 days by default.</param>
        public WebSessions(WebLoginSettings  Login,
                           Boolean           SecureCookies     = false,
                           HTTPCookieName?   CookieName        = null,
                           TimeSpan?         IdleTimeout       = null,
                           TimeSpan?         MaximumLifetime   = null)
        {

            this.Login          = Login ?? throw new ArgumentNullException(nameof(Login));
            this.SecureCookies  = SecureCookies;
            this.CookieName     = CookieName ?? DefaultCookieName;

            this.Store          = new SessionStore(
                                      IdleTimeout:      IdleTimeout     ?? DefaultIdleTimeout,
                                      MaximumLifetime:  MaximumLifetime ?? DefaultMaximumLifetime
                                  );

        }

        #endregion


        #region TryLogin(Username, Password, out Session)

        /// <summary>
        /// Start a session when username and password are right.
        /// </summary>
        /// <param name="Username">What was typed as the username.</param>
        /// <param name="Password">What was typed as the password.</param>
        /// <param name="Session">The new session.</param>
        public Boolean TryLogin(String?                           Username,
                                String?                           Password,
                                [NotNullWhen(true)] out Session?  Session)
        {

            Session = null;

            if (!Login.Verify(Username, Password))
                return false;

            Session = Store.Create(User_Id.Parse(Login.Username));
            return true;

        }

        #endregion

        #region UpdateLogin(NewLogin, ExceptToken = null)

        /// <summary>
        /// Put another login in force and end every session but the one that
        /// made the change.
        /// </summary>
        /// <remarks>
        /// Changing the password has to end the other sessions, or it does not
        /// do what whoever changed it thinks it does: a browser that was signed
        /// in with the old password would keep the page for as long as its
        /// cookie lives. The session doing the change is kept, so that the
        /// settings page does not sign itself out.
        /// </remarks>
        /// <param name="NewLogin">The login from now on.</param>
        /// <param name="ExceptToken">A session to keep, usually the one asking.</param>
        /// <returns>The number of sessions ended.</returns>
        public Int32 UpdateLogin(WebLoginSettings   NewLogin,
                                 SecurityToken_Id?  ExceptToken   = null)
        {

            var previous = Login;

            Login = NewLogin ?? throw new ArgumentNullException(nameof(NewLogin));

            return Store.RemoveAllForUser(User_Id.Parse(previous.Username), ExceptToken);

        }

        #endregion

        #region TryGetSession(Request, out Session)

        /// <summary>
        /// The live session behind the request's cookie, if there is one.
        /// </summary>
        public Boolean TryGetSession(HTTPRequest                       Request,
                                     [NotNullWhen(true)] out Session?  Session)
        {

            Session = null;

            return TryGetToken(Request, out var token) &&
                   Store.TryGet(token, out Session);

        }

        #endregion

        #region HasCookie(Request)

        /// <summary>
        /// Whether the request carries a session cookie at all - live or stale.
        /// </summary>
        public Boolean HasCookie(HTTPRequest Request)
            => Request.Cookies?.Contains(CookieName) == true;

        #endregion

        #region SignOut(Request)

        /// <summary>
        /// End the session behind the request's cookie.
        /// </summary>
        /// <returns>Whether there was a live session to end.</returns>
        public Boolean SignOut(HTTPRequest Request)

            => TryGetToken(Request, out var token) &&
               Store.Remove(token);

        #endregion


        #region SessionCookie(Session) / ExpiredCookie()

        // One HTTPCookie, parsed as one: HTTPCookies.Parse(String) is made for
        // the Cookie header of a request, where a semicolon separates cookies,
        // and would turn "Path=/" and "HttpOnly" into cookies of their own.

        /// <summary>
        /// The Set-Cookie of a sign-in: the token, HttpOnly, for this site only.
        /// </summary>
        public HTTPCookies SessionCookie(Session Session)

            => new (HTTPCookie.Parse(
                        String.Concat(CookieName, "=", Session.Token, Settings(Session.ExpiresAt))
                    ));

        /// <summary>
        /// The Set-Cookie of a sign-out: the same cookie, expired in 1970, so
        /// that the browser drops it.
        /// </summary>
        public HTTPCookies ExpiredCookie()

            => new (HTTPCookie.Parse(
                        String.Concat(CookieName, "=", Settings(DateTimeOffset.UnixEpoch))
                    ));

        #endregion


        #region (private) TryGetToken(Request, out Token)

        private Boolean TryGetToken(HTTPRequest           Request,
                                    out SecurityToken_Id  Token)
        {

            Token = default;

            return Request.Cookies is not null &&
                   Request.Cookies.TryGet(CookieName, out var cookie) &&
                   cookie?.Value is String value &&
                   SecurityToken_Id.TryParse(value, out Token);

        }

        #endregion

        #region (private) Settings(Expires)

        private String Settings(DateTimeOffset Expires)

            => String.Concat("; Expires=", Expires.ToRFC1123(),
                             "; Path=/",
                             "; SameSite=strict",
                             SecureCookies ? "; secure" : "",
                             "; HttpOnly");

        #endregion

    }

}
