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
    /// Which endpoints this web app will open at all - the same rule as in
    /// XMPPConsole, and one comparison that stays silently correct until the
    /// day it is silently not.
    /// </summary>
    [TestFixture]
    public class EndpointPolicyTests
    {

        [Test]
        public void NoEndpoint_IsNoObjection()
        {
            Assert.That(EndpointPolicy.Refuse(null), Is.Null);
            Assert.That(EndpointPolicy.Refuse("  "), Is.Null);
        }

        [Test]
        public void Wss_IsTaken()
        {
            Assert.That(EndpointPolicy.Refuse("wss://xmpp.example.org:5281/xmpp-websocket"), Is.Null);
            Assert.That(EndpointPolicy.Refuse("WSS://xmpp.example.org/ws"),                  Is.Null);
        }

        [Test]
        public void Ws_IsRefused_UnlessInsecureWasSaid()
        {
            Assert.That(EndpointPolicy.Refuse("ws://localhost:5299/ws/"),       Does.Contain("unencrypted"));
            Assert.That(EndpointPolicy.Refuse("ws://localhost:5299/ws/", true), Is.Null);
        }

        [Test]
        public void AnythingElse_IsNoWebSocketEndpoint()
        {
            Assert.That(EndpointPolicy.Refuse("https://xmpp.example.org/ws"),       Does.Contain("did you mean wss://xmpp.example.org/ws"));
            Assert.That(EndpointPolicy.Refuse("https://xmpp.example.org/ws", true), Is.Not.Null, "--insecure permits ws://, nothing else");
            Assert.That(EndpointPolicy.Refuse("xmpp.example.org"),                  Does.Contain("no WebSocket endpoint"));
            Assert.That(EndpointPolicy.Refuse("not a url at all"),                  Does.Contain("no WebSocket endpoint"));
        }

    }

}
