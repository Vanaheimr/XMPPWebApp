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
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Account
{

    /// <summary>
    /// Who may open the web page: one username and one password.
    /// </summary>
    /// <remarks>
    /// The password is kept as a PHC string (PBKDF2-SHA256, Hermod's
    /// <see cref="SecurePassword"/>) and never in the clear. It does not have
    /// to be: unlike the XMPP password, which SCRAM needs to compute a proof
    /// from, this one is only ever compared against what somebody typed. That
    /// is the whole difference between the two files below this namespace, and
    /// the reason this one can afford to be the safer of them.
    /// </remarks>
    /// <param name="Username">The one username.</param>
    /// <param name="Password">The hashed password.</param>
    public sealed record WebLoginSettings(String          Username,
                                          SecurePassword  Password)
    {

        #region Data

        /// <summary>
        /// The username a generated first login gets.
        /// </summary>
        public const Int32   MinimumPasswordLength  = 8;

        /// <summary>
        /// The username of a login this program made up for a first start.
        /// </summary>
        public const String  DefaultUsername        = "admin";

        #endregion


        #region TryCreate(Username, Password, out Login, out Error)

        /// <summary>
        /// A login from what a person typed, with the password hashed - or the
        /// one sentence that says what is wrong with them.
        /// </summary>
        public static Boolean TryCreate(String?                                    Username,
                                        String?                                    Password,
                                        [NotNullWhen(true)]  out WebLoginSettings?  Login,
                                        [NotNullWhen(false)] out String?            Error)
        {

            Login  = null;
            Error  = null;

            var username = Username?.Trim() ?? "";

            if (username.Length == 0)
            {
                Error = "A username is required.";
                return false;
            }

            if (Password is null || Password.Length < MinimumPasswordLength)
            {
                Error = $"The password must have at least {MinimumPasswordLength} characters.";
                return false;
            }

            Login = new WebLoginSettings(username, SecurePassword.Create(Password));
            return true;

        }

        #endregion

        #region Generate(Username = DefaultUsername)

        /// <summary>
        /// A login with a password nobody has chosen, for a first start that
        /// found no file: the plain password comes back once, to be shown on
        /// the console, and is not kept anywhere but in its hash.
        /// </summary>
        public static (WebLoginSettings Login, String Password) Generate(String Username = DefaultUsername)
        {

            // 24 bytes of Base64Url: long enough that nobody guesses it, short
            // enough that somebody can type it off a console.
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).
                                   Replace('+', '-').
                                   Replace('/', '_').
                                   TrimEnd('=');

            return (new WebLoginSettings(Username, SecurePassword.Create(password)), password);

        }

        #endregion

        #region Verify(Username, Password)

        /// <summary>
        /// Whether this pair may open the page.
        /// </summary>
        /// <remarks>
        /// Both halves are always examined and the results are combined without
        /// a short circuit: a wrong username has to take as long as a wrong
        /// password, or the two can be guessed one after the other instead of
        /// together.
        ///
        /// Both names are hashed here rather than once in a field, and that is
        /// not waste: a field initializer does not run again for
        /// <c>with { Username = … }</c> - the copy constructor takes the fields
        /// as they are - so a cached hash would go on answering for the name
        /// this record used to carry. Changing only the username is exactly
        /// what the settings page does.
        /// </remarks>
        public Boolean Verify(String? Username,
                              String? Password)
        {

            // Hashed on both sides so that the comparison has two inputs of the
            // same length whatever was typed: a fixed-time comparison of raw
            // strings gives the length away, and the length is the one thing
            // about a name that is cheap to learn.
            var usernameMatches  = CryptographicOperations.FixedTimeEquals(
                                       SHA256.HashData(Encoding.UTF8.GetBytes(Username      ?? "")),
                                       SHA256.HashData(Encoding.UTF8.GetBytes(this.Username ?? ""))
                                   );

            var passwordMatches  = this.Password.Verify(Password ?? "");

            return usernameMatches & passwordMatches;

        }

        #endregion


        #region TryParse(JSON, out Login, out Error)

        /// <summary>
        /// The login from the file: {"username", "password"}, the password as
        /// a PHC string.
        /// </summary>
        public static Boolean TryParse(JObject                                    JSON,
                                       [NotNullWhen(true)]  out WebLoginSettings?  Login,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Login  = null;
            Error  = null;

            var username = JSON.Value<String>("username")?.Trim() ?? "";

            if (username.Length == 0)
            {
                Error = "The username is missing.";
                return false;
            }

            var password = JSON.Value<String>("password") ?? "";

            if (!SecurePassword.TryParse(password, out var securePassword))
            {
                Error = "The password is not a valid PHC string.";
                return false;
            }

            Login = new WebLoginSettings(username, securePassword);
            return true;

        }

        #endregion

        #region ToJSON(IncludePasswordHash)

        /// <summary>
        /// The login as JSON: with the password hash for the file, with the
        /// username alone for the browser - which has no business knowing even
        /// the hash.
        /// </summary>
        public JObject ToJSON(Boolean IncludePasswordHash)
        {

            var json = new JObject(
                           new JProperty("username", Username)
                       );

            if (IncludePasswordHash)
                json.Add("password", Password.ToString());

            return json;

        }

        #endregion

        #region (override) ToString()

        /// <summary>
        /// The username - never anything about the password.
        /// </summary>
        public override String ToString()
            => Username;

        #endregion

    }

}
