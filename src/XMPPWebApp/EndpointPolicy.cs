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

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// Which XMPP endpoint this web app is willing to sign on to.
    /// </summary>
    /// <remarks>
    /// The same rule as in XMPPConsole, for the same reason. The library
    /// connects to whatever address it is handed, and that is right for a
    /// library. It is wrong for the program in front of it: an endpoint is not
    /// merely where the connection goes, it decides whether the login is
    /// protected on the way at all.
    ///
    /// Over <c>ws://</c> it is not - and the damage is not that somebody reads
    /// along, it is that the password itself goes out. A man in the middle
    /// rewrites the feature announcement down to SASL PLAIN, and PLAIN is the
    /// password, not a proof of it. The pinning in Ratatoskr only takes hold
    /// from the second login onwards, and the first is precisely the one being
    /// taken.
    ///
    /// Hence refused, not warned about. Whoever really wants a plain endpoint -
    /// a server on the same machine, a test setup with no certificate - says so
    /// with <c>--insecure</c>.
    /// </remarks>
    public static class EndpointPolicy
    {

        #region Refuse(Endpoint, AllowInsecure = false)

        /// <summary>
        /// Why this endpoint may not be used - or null when it may.
        /// </summary>
        /// <param name="Endpoint">
        /// The address as it was given. Nothing at all is no objection: without
        /// one the library asks the host-meta of the domain (XEP-0156) and
        /// otherwise stays at <c>wss://{domain}:5443/ws</c>. Both are
        /// TLS-protected by construction.
        /// </param>
        /// <param name="AllowInsecure">Whether <c>ws://</c> was expressly asked for.</param>
        public static String? Refuse(String?  Endpoint,
                                     Boolean  AllowInsecure   = false)
        {

            if (String.IsNullOrWhiteSpace(Endpoint))
                return null;

            var parsed = Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri);

            if (parsed && uri!.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
                return null;

            if (parsed && uri!.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase))
                return AllowInsecure
                           ? null
                           : $"'{Endpoint}' is unencrypted. The login would travel readable, and " +
                              "a man in the middle can strip the announcement down to SASL PLAIN " +
                              "- then it is the password itself that goes out, not a proof of it. " +
                              "Use wss://, or --insecure if this is a server you trust the way to.";

            return $"'{Endpoint}' is no WebSocket endpoint. This client speaks XMPP over " +
                    "WebSocket (RFC 7395), so the address begins with wss://" +
                   (parsed && uri!.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                        ? $" - did you mean wss://{uri.Authority}{uri.PathAndQuery}?"
                        : ".");

        }

        #endregion

    }

}
