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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// The XMPP account the account page asks for: what a person may type, and
    /// what travels to the browser and back.
    /// </summary>
    /// <remarks>
    /// Two things matter here and are easy to get wrong. The endpoint rule is
    /// the same one XMPPConsole enforces - a plain ws:// endpoint is refused
    /// unless it was expressly allowed - and it has to hold whether the account
    /// comes from the command line or from the page. And the password: it is
    /// written to the file and never sent to the browser, so the JSON for the
    /// page must not carry it and the JSON for the file must.
    /// </remarks>
    [TestFixture]
    public class AccountSettingsTests
    {

        #region AValidAccount_IsAccepted()

        [Test]
        public void AValidAccount_IsAccepted()
        {

            Assert.That(AccountSettings.TryCreate("alice@example.org", "secret",
                                                  "wss://xmpp.example.org:5281/xmpp-websocket",
                                                  null, false, false,
                                                  out var settings, out var error),
                        Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(error,                          Is.Null);
                Assert.That(settings!.JID.ToString(),       Is.EqualTo("alice@example.org"));
                Assert.That(settings.Password,              Is.EqualTo("secret"));
                Assert.That(settings.WebSocketURI,          Is.EqualTo("wss://xmpp.example.org:5281/xmpp-websocket"));
                Assert.That(settings.MinimumSaslMechanism,  Is.EqualTo("SCRAM-SHA-256"), "the default");
            });

        }

        #endregion

        #region AnEndpointlessAccount_AsksTheHostMeta()

        [Test]
        public void AnEndpointlessAccount_AsksTheHostMeta()
        {

            Assert.That(AccountSettings.TryCreate("alice@example.org", "secret", "", "  ", false, false,
                                                  out var settings, out _),
                        Is.True);

            Assert.That(settings!.WebSocketURI, Is.Null, "a blank endpoint is no endpoint");

        }

        #endregion

        #region WhatIsMissingOrWrong_IsRefusedWithAReason()

        [Test]
        public void WhatIsMissingOrWrong_IsRefusedWithAReason()
        {

            Assert.Multiple(() =>
            {

                Assert.That(AccountSettings.TryCreate("",                 "pw", null, null, false, false, out _, out var e1), Is.False);
                Assert.That(e1, Does.Contain("JID"));

                Assert.That(AccountSettings.TryCreate("example.org",      "pw", null, null, false, false, out _, out var e2), Is.False);
                Assert.That(e2, Does.Contain("domain and no account"));

                Assert.That(AccountSettings.TryCreate("alice@example.org", "",  null, null, false, false, out _, out var e3), Is.False);
                Assert.That(e3, Does.Contain("password"));

                Assert.That(AccountSettings.TryCreate("alice@example.org", "pw", "https://x/ws", null, false, false, out _, out var e4), Is.False);
                Assert.That(e4, Does.Contain("wss://"));

                Assert.That(AccountSettings.TryCreate("alice@example.org", "pw", null, "MD5", false, false, out _, out var e5), Is.False);
                Assert.That(e5, Does.Contain("SASL"));

            });

        }

        #endregion

        #region APlainEndpoint_IsRefusedUnlessAllowed()

        /// <summary>
        /// The same rule as XMPPConsole and the command line: ws:// only with
        /// the box ticked.
        /// </summary>
        [Test]
        public void APlainEndpoint_IsRefusedUnlessAllowed()
        {

            Assert.That(AccountSettings.TryCreate("alice@example.org", "pw", "ws://localhost:5299/ws/", null, false, false, out _, out var refused), Is.False);
            Assert.That(refused, Does.Contain("unencrypted"));

            Assert.That(AccountSettings.TryCreate("alice@example.org", "pw", "ws://localhost:5299/ws/", null, true, false, out var settings, out _), Is.True);
            Assert.That(settings!.AllowInsecure,      Is.True);
            Assert.That(settings.IsInsecureEndpoint,  Is.True);

        }

        #endregion

        #region ThePasswordTravelsToTheFile_NeverToTheBrowser()

        [Test]
        public void ThePasswordTravelsToTheFile_NeverToTheBrowser()
        {

            AccountSettings.TryCreate("alice@example.org", "secret", "wss://x.example/ws", "SCRAM-SHA-1", false, true, out var settings, out _);

            var forFile     = settings!.ToJSON(IncludePassword: true);
            var forBrowser  = settings.ToJSON(IncludePassword: false);

            Assert.Multiple(() =>
            {
                Assert.That(forFile["password"]?.ToString(),      Is.EqualTo("secret"));
                Assert.That(forFile["minimumSasl"]?.ToString(),   Is.EqualTo("SCRAM-SHA-1"));
                Assert.That(forFile["trustAnnouncement"]?.Value<System.Boolean>(), Is.True);

                Assert.That(forBrowser["password"],               Is.Null, "the browser never receives the password");
                Assert.That(forBrowser["passwordSet"]?.Value<System.Boolean>(), Is.True, "only that one is set");
                Assert.That(forBrowser["jid"]?.ToString(),        Is.EqualTo("alice@example.org"));
            });

        }

        #endregion

        #region TryParse_IsTheInverseOfToJSON()

        [Test]
        public void TryParse_IsTheInverseOfToJSON()
        {

            AccountSettings.TryCreate("alice@example.org", "secret", "wss://x.example/ws", "SCRAM-SHA-1", false, true, out var original, out _);

            var json = original!.ToJSON(IncludePassword: true);

            Assert.That(AccountSettings.TryParse(json, out var read, out var error), Is.True);
            Assert.That(error, Is.Null);

            Assert.Multiple(() =>
            {
                Assert.That(read!.JID,                   Is.EqualTo(original.JID));
                Assert.That(read.Password,               Is.EqualTo(original.Password));
                Assert.That(read.WebSocketURI,           Is.EqualTo(original.WebSocketURI));
                Assert.That(read.MinimumSaslMechanism,   Is.EqualTo(original.MinimumSaslMechanism));
                Assert.That(read.TrustAnnouncement,      Is.EqualTo(original.TrustAnnouncement));
            });

        }

        #endregion

        #region TryParse_RefusesJSONWithoutAnAccount()

        [Test]
        public void TryParse_RefusesJSONWithoutAnAccount()
        {
            Assert.That(AccountSettings.TryParse(new JObject(new JProperty("jid", "alice@example.org")), out _, out var error), Is.False);
            Assert.That(error, Does.Contain("password"));
        }

        #endregion

        #region ToString_NeverShowsThePassword()

        [Test]
        public void ToString_NeverShowsThePassword()
        {
            AccountSettings.TryCreate("alice@example.org", "hunter2", "wss://x.example/ws", null, false, false, out var settings, out _);
            Assert.That(settings!.ToString(), Does.Not.Contain("hunter2"));
        }

        #endregion

    }

}
