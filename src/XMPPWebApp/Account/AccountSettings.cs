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

using Microsoft.Extensions.Logging;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Account
{

    /// <summary>
    /// Where the account settings came from.
    /// </summary>
    public enum AccountSource
    {
        /// <summary>No account is configured.</summary>
        None,
        /// <summary>The account file, written by the account page.</summary>
        File,
        /// <summary>The command line, overriding the file for this run.</summary>
        Arguments
    }


    /// <summary>
    /// The XMPP account this web app signs in to, and how: what the account
    /// page asks for and the account file keeps.
    /// </summary>
    /// <param name="JID">The account, user@domain, optionally with the resource this device should ask for.</param>
    /// <param name="Password">Its password.</param>
    /// <param name="WebSocketURI">The WebSocket endpoint, or null to ask the host-meta of the domain (XEP-0156).</param>
    /// <param name="MinimumSaslMechanism">The weakest SASL mechanism still accepted.</param>
    /// <param name="AllowInsecure">Whether a plain ws:// endpoint may be used at all.</param>
    /// <param name="TrustAnnouncement">Whether to carry on when the server signs a different mechanism list than the one that arrived (XEP-0474).</param>
    public sealed record AccountSettings(JID      JID,
                                         String   Password,
                                         String?  WebSocketURI,
                                         String   MinimumSaslMechanism   = AccountSettings.DefaultMinimumSaslMechanism,
                                         Boolean  AllowInsecure          = false,
                                         Boolean  TrustAnnouncement      = false)
    {

        #region Data

        /// <summary>
        /// The default lower bound for the SASL mechanism - the same one
        /// XMPPConsole demands, for the same reasons.
        /// </summary>
        public const String DefaultMinimumSaslMechanism = "SCRAM-SHA-256";

        /// <summary>
        /// The mechanisms the account page offers, strongest first.
        /// </summary>
        public static readonly String[] KnownSaslMechanisms = ["SCRAM-SHA-256", "SCRAM-SHA-1", "PLAIN"];

        #endregion

        #region Properties

        /// <summary>
        /// The account without a resource.
        /// </summary>
        public JID BareJID
            => JID.Bare;

        /// <summary>
        /// Whether the endpoint is a plain ws:// one, which is only ever taken
        /// because <see cref="AllowInsecure"/> says so.
        /// </summary>
        public Boolean IsInsecureEndpoint
            => WebSocketURI?.TrimStart().StartsWith("ws://", StringComparison.OrdinalIgnoreCase) == true;

        #endregion


        #region TryCreate(JIDText, Password, WebSocketURI, MinimumSaslMechanism, AllowInsecure, TrustAnnouncement, out Settings, out Error)

        /// <summary>
        /// Settings from what a person typed, or the one sentence that says
        /// what is wrong with them.
        /// </summary>
        public static Boolean TryCreate(String?                                    JIDText,
                                        String?                                    Password,
                                        String?                                    WebSocketURI,
                                        String?                                    MinimumSaslMechanism,
                                        Boolean                                    AllowInsecure,
                                        Boolean                                    TrustAnnouncement,
                                        [NotNullWhen(true)]  out AccountSettings?  Settings,
                                        [NotNullWhen(false)] out String?           Error)
        {

            Settings  = null;
            Error     = null;

            if (String.IsNullOrWhiteSpace(JIDText))
            {
                Error = "A JID is required, e.g. user@example.org.";
                return false;
            }

            if (!JID.TryParse(JIDText.Trim(), out var jid))
            {
                Error = $"'{JIDText.Trim()}' is not a valid JID.";
                return false;
            }

            if (jid.Localpart is null)
            {
                Error = $"'{jid}' names a domain and no account. A login JID has the form user@example.org.";
                return false;
            }

            if (String.IsNullOrEmpty(Password))
            {
                Error = "A password is required.";
                return false;
            }

            var endpoint = String.IsNullOrWhiteSpace(WebSocketURI)
                               ? null
                               : WebSocketURI.Trim();

            if (EndpointPolicy.Refuse(endpoint, AllowInsecure) is String objection)
            {
                Error = objection;
                return false;
            }

            if (endpoint is not null && !URL.TryParse(endpoint, out _))
            {
                Error = $"'{endpoint}' is not a valid URL.";
                return false;
            }

            var mechanism = String.IsNullOrWhiteSpace(MinimumSaslMechanism)
                                ? DefaultMinimumSaslMechanism
                                : KnownSaslMechanisms.FirstOrDefault(known => known.Equals(MinimumSaslMechanism.Trim(), StringComparison.OrdinalIgnoreCase));

            if (mechanism is null)
            {
                Error = $"The SASL mechanism has to be one of: {String.Join(", ", KnownSaslMechanisms)}.";
                return false;
            }

            Settings = new AccountSettings(jid, Password, endpoint, mechanism, AllowInsecure, TrustAnnouncement);
            return true;

        }

        #endregion

        #region TryParse(JSON, out Settings, out Error)

        /// <summary>
        /// Settings from the account file or the account page:
        /// {"jid", "password", "websocket", "minimumSasl", "allowInsecure", "trustAnnouncement"}.
        /// </summary>
        public static Boolean TryParse(JObject                                    JSON,
                                       [NotNullWhen(true)]  out AccountSettings?  Settings,
                                       [NotNullWhen(false)] out String?           Error)

            => TryCreate(JSON.Value<String>("jid"),
                         JSON.Value<String>("password"),
                         JSON.Value<String>("websocket"),
                         JSON.Value<String>("minimumSasl"),
                         JSON.Value<Boolean?>("allowInsecure")     ?? false,
                         JSON.Value<Boolean?>("trustAnnouncement") ?? false,
                         out Settings,
                         out Error);

        #endregion

        #region ToJSON(IncludePassword)

        /// <summary>
        /// The settings as JSON: with the password for the file, without it
        /// for the browser - which only learns that one is set.
        /// </summary>
        public JObject ToJSON(Boolean IncludePassword)
        {

            var json = new JObject(
                           new JProperty("jid",                JID.ToString()),
                           new JProperty("websocket",          WebSocketURI),
                           new JProperty("minimumSasl",        MinimumSaslMechanism),
                           new JProperty("allowInsecure",      AllowInsecure),
                           new JProperty("trustAnnouncement",  TrustAnnouncement)
                       );

            if (IncludePassword)
                json.AddFirst(new JProperty("password", Password));
            else
                json.Add("passwordSet", true);

            return json;

        }

        #endregion

        #region CreateClient(LoggerFactory = null)

        /// <summary>
        /// A client for this account, not yet connected. The SASL bound is set
        /// before ConnectAsync, because with PLAIN the password already stands
        /// in the first frame.
        /// </summary>
        public XMPPClient CreateClient(ILoggerFactory? LoggerFactory = null)
        {

            var client = new XMPPClient(
                             JID,
                             Password,
                             WebSocketURI is not null ? URL.Parse(WebSocketURI) : null,
                             LoggerFactory
                         );

            client.Connection.MinimumSaslMechanism = MinimumSaslMechanism;

            if (TrustAnnouncement)
                client.Connection.RefuseOnAnnouncementMismatch = false;

            // A resource typed into the JID is a wish for a device name; without
            // one this process names itself.
            if (JID.Resourcepart is null)
                client.Connection.Resource = $"webapp-{Environment.ProcessId}";

            return client;

        }

        #endregion


        #region (override) ToString()

        /// <summary>
        /// The account and its endpoint - never the password.
        /// </summary>
        public override String ToString()

            => $"{JID}, endpoint {WebSocketURI ?? "from the host-meta of " + JID.Domainpart + " (XEP-0156)"}, at least {MinimumSaslMechanism}";

        #endregion

    }

}
