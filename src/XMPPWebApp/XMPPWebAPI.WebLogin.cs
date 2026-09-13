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

using Microsoft.Extensions.Logging;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// The web login: who may open the page at all, and how that is changed
    /// from the page itself.
    /// </summary>
    /// <remarks>
    /// The one thing this must not become is a way to take the page over from
    /// a session that merely happens to be open: changing the login therefore
    /// asks for the current password again, exactly as changing a password
    /// anywhere else does. Whoever gets it right keeps their own session and
    /// loses every other one.
    /// </remarks>
    public sealed partial class XMPPWebAPI
    {

        #region Properties

        /// <summary>
        /// The file the web login lives in.
        /// </summary>
        public WebLoginFile  WebLoginFile    { get; }

        #endregion


        #region (private) GetWebLogin (Request)

        /// <summary>
        /// GET /api/v1/weblogin: the username. The password, even hashed, is
        /// nothing the browser needs.
        /// </summary>
        private Task<HTTPResponse> GetWebLogin(HTTPRequest Request)

            => Task.FromResult(
                   TryGetSession(Request, out _, out var unauthorized)
                       ? JSONResponse(Request, HTTPStatusCode.OK, WebLoginJSON())
                       : unauthorized
               );

        #endregion

        #region (private) PutWebLogin (Request)

        /// <summary>
        /// PUT /api/v1/weblogin with {"currentPassword", "username",
        /// "newPassword"}: changes the login of the page. The current password
        /// is required; an empty new password keeps the one in force and
        /// changes only the username.
        /// </summary>
        private async Task<HTTPResponse> PutWebLogin(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out var session, out var unauthorized))
                return unauthorized;

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            // The session alone is not enough: a browser somebody left open
            // must not be able to lock its owner out.
            if (!Sessions.Login.Verify(Sessions.Username, json.Value<String>("currentPassword")))
            {

                logger.LogWarning("A web login change was refused for {Remote}: wrong current password", Request.RemoteSocket);

                await Task.Delay(FailedLoginDelay, Request.CancellationToken);

                return ErrorJSON(Request, HTTPStatusCode.Forbidden, "The current password is wrong.");

            }

            var username     = json.Value<String>("username") ?? Sessions.Username;
            var newPassword  = json.Value<String>("newPassword");

            WebLoginSettings login;

            if (String.IsNullOrEmpty(newPassword))
            {
                // Only the username changes; the password in force stays as it
                // is, hash and all.
                var trimmed = username.Trim();

                if (trimmed.Length == 0)
                    return ErrorJSON(Request, HTTPStatusCode.BadRequest, "A username is required.");

                login = Sessions.Login with { Username = trimmed };

            }

            else if (!WebLoginSettings.TryCreate(username, newPassword, out login!, out var error))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, error);

            try
            {
                WebLoginFile.Save(login);
            }
            catch (Exception e)
            {
                logger.LogError("The web login could not be saved to '{File}': {Error}", WebLoginFile.Path, e.Message);
                return ErrorJSON(Request, HTTPStatusCode.InternalServerError, $"The web login could not be saved to '{WebLoginFile.Path}': {e.Message}");
            }

            var ended = Sessions.UpdateLogin(login, session.Token);

            logger.LogInformation("The web login is now '{Username}'; {Ended} other session(s) ended", login.Username, ended);

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       WebLoginJSON(new JProperty("sessionsEnded", ended))
                   );

        }

        #endregion

        #region (private) WebLoginJSON(More)

        /// <summary>
        /// The username, where it is kept, and whatever the caller adds.
        /// </summary>
        private JObject WebLoginJSON(params JProperty[] More)
        {

            var json = new JObject(
                           new JProperty("username",  Sessions.Username),
                           new JProperty("file",      WebLoginFile.Path)
                       );

            foreach (var property in More)
                json.Add(property);

            return json;

        }

        #endregion

    }

}
