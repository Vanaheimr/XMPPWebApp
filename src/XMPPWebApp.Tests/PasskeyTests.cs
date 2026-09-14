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

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using NUnit.Framework;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using org.GraphDefined.Vanaheimr.Hermod.Passkeys;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// Signing in with a passkey, from the browser's side of the wire.
    /// </summary>
    /// <remarks>
    /// The ceremonies are Hermod's and Hermod tests them. What is tested here is
    /// that this application reaches them: that the routes appear when - and
    /// only when - an origin could be named, that a key registered through them
    /// opens a session afterwards, and that the three ways a ceremony is
    /// supposed to fail do fail.
    ///
    /// The authenticator is a key pair and a counter. That is what makes this
    /// testable at all: there is no hardware in it, and the parts a real one
    /// contributes - the signature over authenticatorData and the hash of the
    /// client data - are exactly the parts the server checks.
    ///
    /// The relying party is "localhost" and not "127.0.0.1", which is not a
    /// detail: a relying party id has to be a domain and an IP address is not
    /// one. The server binds to the address, the ceremony names the host.
    /// </remarks>
    [TestFixture]
    public class PasskeyTests
    {

        #region Data

        private const String Username = "admin";
        private const String Password = "correct-horse-battery-staple";

        private String       root    = "";
        private HTTPServer?  server;
        private XMPPWebAPI?  api;
        private Uri          origin  = new ("http://127.0.0.1/");
        private String       rpOrigin = "";

        #endregion

        #region (private) SoftwareAuthenticator

        /// <summary>
        /// What a platform authenticator does, without the platform: one key
        /// pair, one credential id and a signature counter.
        /// </summary>
        private sealed class SoftwareAuthenticator : IDisposable
        {

            private readonly ECDsa   ecdsa  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            private readonly String  rpId;
            private readonly String  origin;

            public Byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

            public String CredentialIdText
                => Base64Url.EncodeToString(CredentialId);

            public UInt32 Counter { get; set; }

            public SoftwareAuthenticator(String RpId, String Origin)
            {
                rpId    = RpId;
                origin  = Origin;
            }

            private Byte[] COSEKey()
            {

                var parameters = ecdsa.ExportParameters(false);

                return new CBORMap {
                           {  1,  2 },            // kty: EC2
                           {  3, -7 },            // alg: ES256
                           { -1,  1 },            // crv: P-256
                           { -2, parameters.Q.X! },
                           { -3, parameters.Q.Y! }
                       }.ToValue().ToByteArray();

            }

            private static Byte[] ClientDataJSON(String Type, String Challenge, String Origin)

                => Encoding.UTF8.GetBytes(new JObject(
                       new JProperty("type",         Type),
                       new JProperty("challenge",    Challenge),
                       new JProperty("origin",       Origin),
                       new JProperty("crossOrigin",  false)
                   ).ToString(Newtonsoft.Json.Formatting.None));

            private Byte[] AuthenticatorData(Byte Flags, Boolean WithCredential)
            {

                var data = new List<Byte>();

                data.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
                data.Add(Flags);

                var counter = new Byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(counter, Counter);
                data.AddRange(counter);

                if (WithCredential)
                {
                    data.AddRange(new Byte[16]);                     // AAGUID
                    data.Add((Byte) (CredentialId.Length >> 8));
                    data.Add((Byte)  CredentialId.Length);
                    data.AddRange(CredentialId);
                    data.AddRange(COSEKey());
                }

                return [.. data];

            }

            /// <summary>
            /// What navigator.credentials.create() hands back.
            /// </summary>
            public JObject Register(String Challenge, String? OriginOverride = null)
            {

                var attestationObject = new CBORMap {
                                            { "fmt",       "none" },
                                            { "attStmt",   new CBORMap().ToValue() },
                                            { "authData",  AuthenticatorData(0x45, WithCredential: true) }
                                        }.ToValue().ToByteArray();

                return new JObject(
                           new JProperty("id",        CredentialIdText),
                           new JProperty("rawId",     CredentialIdText),
                           new JProperty("type",      "public-key"),
                           new JProperty("response",  new JObject(
                               new JProperty("clientDataJSON",     Base64Url.EncodeToString(ClientDataJSON("webauthn.create", Challenge, OriginOverride ?? origin))),
                               new JProperty("attestationObject",  Base64Url.EncodeToString(attestationObject))
                           ))
                       );

            }

            /// <summary>
            /// What navigator.credentials.get() hands back.
            /// </summary>
            public JObject Authenticate(String   Challenge,
                                        String?  OriginOverride   = null,
                                        Boolean  TamperSignature  = false)
            {

                Counter++;

                var authenticatorData  = AuthenticatorData(0x05, WithCredential: false);
                var clientDataJSON     = ClientDataJSON("webauthn.get", Challenge, OriginOverride ?? origin);
                var signedData         = new Byte[authenticatorData.Length + 32];

                authenticatorData.CopyTo(signedData, 0);
                SHA256.HashData(clientDataJSON).CopyTo(signedData, authenticatorData.Length);

                var signature = ecdsa.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

                if (TamperSignature)
                    signature[^1] ^= 0x01;

                return new JObject(
                           new JProperty("id",        CredentialIdText),
                           new JProperty("rawId",     CredentialIdText),
                           new JProperty("type",      "public-key"),
                           new JProperty("response",  new JObject(
                               new JProperty("clientDataJSON",     Base64Url.EncodeToString(clientDataJSON)),
                               new JProperty("authenticatorData",  Base64Url.EncodeToString(authenticatorData)),
                               new JProperty("signature",          Base64Url.EncodeToString(signature)),
                               new JProperty("userHandle",         null as String)
                           ))
                       );

            }

            public void Dispose()
                => ecdsa.Dispose();

        }

        #endregion

        #region Setup / Teardown

        [SetUp]
        public async Task StartTheServer()
        {

            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-passkeys-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            server    = await HTTPServer.StartNew(IPv4Address.Parse("127.0.0.1"));
            origin    = new Uri($"http://127.0.0.1:{server.TCPPort}/");

            // The address is what the socket listens on; the name is what a
            // passkey can belong to.
            rpOrigin  = $"http://localhost:{server.TCPPort}";

            api       = new XMPPWebAPI(
                            server,
                            new AccountFile(Path.Combine(root, "xmpp-account.json")),
                            DataDirectory:     root,
                            WebAuthnSettings:  new WebAuthnSettings("localhost", "XMPPWebApp Tests", [ rpOrigin ])
                        );

            await api.LoadDatabase();

            await api.CreateUserIfNotExists(
                      User_Id.Parse(Username),
                      I18NString.Create(Username),
                      SimpleEMailAddress.Parse($"{Username}@localhost"),
                      Password:                  Password,
                      IsAuthenticated:           true,
                      SkipNewUserEMail:          true,
                      SkipNewUserNotifications:  true,
                      SkipDefaultNotifications:  true
                  );

        }

        [TearDown]
        public async Task StopTheServer()
        {

            if (server is not null)
                await server.Stop();

            server = null;
            api    = null;

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (Exception)
            { }

        }

        #endregion

        #region (private) helpers

        private HttpClient Browser()

            => new (new HttpClientHandler {
                        CookieContainer    = new CookieContainer(),
                        UseCookies         = true,
                        AllowAutoRedirect  = false
                    }) {
                   BaseAddress = origin,
                   Timeout     = TimeSpan.FromSeconds(30)
               };

        private static async Task<(HttpStatusCode Status, JObject? JSON)> Call(HttpClient  Browser,
                                                                               HttpMethod  Method,
                                                                               String      Path,
                                                                               JObject?    Body = null)
        {

            using var request = new HttpRequestMessage(Method, Path);

            if (Body is not null)
                request.Content = new StringContent(Body.ToString(), Encoding.UTF8, "application/json");

            using var response  = await Browser.SendAsync(request);
            var       text      = await response.Content.ReadAsStringAsync();

            JObject? json = null;

            try
            {
                if (text.Length > 0)
                    json = JObject.Parse(text);
            }
            catch (Exception)
            { }

            return (response.StatusCode, json);

        }

        private static Task<(HttpStatusCode, JObject?)> SignIn(HttpClient Browser)

            => Call(Browser, HttpMethod.Post, "api/auth/login",
                    new JObject(new JProperty("login", Username), new JProperty("password", Password)));

        /// <summary>
        /// The whole registration ceremony for a signed-in browser.
        /// </summary>
        private async Task<(HttpStatusCode Status, JObject? JSON)> Register(HttpClient              Browser,
                                                                            SoftwareAuthenticator   Authenticator,
                                                                            String                  Name             = "this laptop",
                                                                            String?                 OriginOverride   = null)
        {

            var (status, options) = await Call(Browser, HttpMethod.Post, "api/auth/passkeys/register/options", []);

            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "the options of the registration ceremony");

            var challenge = options?["publicKey"]?.Value<String>("challenge") ?? "";

            return await Call(Browser, HttpMethod.Post, "api/auth/passkeys/register",
                              new JObject(
                                  new JProperty("name",        Name),
                                  new JProperty("ceremonyId",  options?.Value<String>("ceremonyId")),
                                  new JProperty("credential",  Authenticator.Register(challenge, OriginOverride))
                              ));

        }

        #endregion


        #region APasskey_IsRegisteredAndThenSignsIn()

        /// <summary>
        /// The round trip: register one with a password in hand, then open a
        /// browser that never saw the password and get in with the key alone.
        /// </summary>
        [Test]
        public async Task APasskey_IsRegisteredAndThenSignsIn()
        {

            using var authenticator = new SoftwareAuthenticator("localhost", rpOrigin);
            using var browser       = Browser();

            Assert.That((await SignIn(browser)).Item1, Is.EqualTo(HttpStatusCode.OK));

            var (registered, _) = await Register(browser, authenticator);

            // 201 and not 200: something was made that was not there before.
            Assert.That(registered, Is.EqualTo(HttpStatusCode.Created), "the passkey was registered");

            var (listed, list) = await Call(browser, HttpMethod.Get, "api/auth/passkeys");

            Assert.Multiple(() =>
            {
                Assert.That(listed,                                              Is.EqualTo(HttpStatusCode.OK));
                Assert.That(list?["passkeys"]?.Count(),                          Is.EqualTo(1));
                Assert.That(list?["passkeys"]?[0]?.Value<String>("name"),        Is.EqualTo("this laptop"));
            });

            // A browser of its own, which has never been told the password.
            using var withTheKeyOnly = Browser();

            var (optionsStatus, options) = await Call(withTheKeyOnly, HttpMethod.Post, "api/auth/passkeys/login/options", []);

            Assert.That(optionsStatus, Is.EqualTo(HttpStatusCode.OK));

            var (signedIn, me) = await Call(withTheKeyOnly, HttpMethod.Post, "api/auth/passkeys/login",
                                            new JObject(
                                                new JProperty("ceremonyId",  options?.Value<String>("ceremonyId")),
                                                new JProperty("credential",  authenticator.Authenticate(options?["publicKey"]?.Value<String>("challenge") ?? ""))
                                            ));

            Assert.Multiple(() =>
            {
                Assert.That(signedIn,                            Is.EqualTo(HttpStatusCode.OK), "signed in with the passkey alone");
                Assert.That(me?["user"]?.Value<String>("id"),    Is.EqualTo(Username));
            });

            // And the session it opened is a session like any other.
            Assert.That((await Call(withTheKeyOnly, HttpMethod.Get, "api/v1/status")).Status,
                        Is.EqualTo(HttpStatusCode.OK),
                        "and it opens this application's own routes");

        }

        #endregion

        #region ATamperedSignature_IsRefused()

        [Test]
        public async Task ATamperedSignature_IsRefused()
        {

            using var authenticator = new SoftwareAuthenticator("localhost", rpOrigin);
            using var browser       = Browser();

            await SignIn(browser);
            await Register(browser, authenticator);

            using var attacker = Browser();

            var (_, options) = await Call(attacker, HttpMethod.Post, "api/auth/passkeys/login/options", []);

            var (status, _) = await Call(attacker, HttpMethod.Post, "api/auth/passkeys/login",
                                         new JObject(
                                             new JProperty("ceremonyId",  options?.Value<String>("ceremonyId")),
                                             new JProperty("credential",  authenticator.Authenticate(options?["publicKey"]?.Value<String>("challenge") ?? "",
                                                                                                     TamperSignature: true))
                                         ));

            Assert.That(status, Is.EqualTo(HttpStatusCode.Unauthorized), "one flipped bit and the assertion is worthless");

        }

        #endregion

        #region AnotherOrigin_IsRefused()

        /// <summary>
        /// What makes a passkey phishing-resistant: the authenticator writes the
        /// origin into the signed client data, so a ceremony carried out for a
        /// different site cannot be replayed against this one.
        /// </summary>
        [Test]
        public async Task AnotherOrigin_IsRefused()
        {

            using var authenticator = new SoftwareAuthenticator("localhost", rpOrigin);
            using var browser       = Browser();

            await SignIn(browser);

            var (status, json) = await Register(browser, authenticator, OriginOverride: "https://phish.example");

            Assert.Multiple(() =>
            {
                Assert.That(status,                          Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(json?.Value<String>("description"), Does.Contain("origin"));
            });

        }

        #endregion

        #region ACeremony_IsGoodForOneAttempt()

        /// <summary>
        /// A challenge answered twice is a challenge somebody recorded.
        /// </summary>
        [Test]
        public async Task ACeremony_IsGoodForOneAttempt()
        {

            using var authenticator = new SoftwareAuthenticator("localhost", rpOrigin);
            using var browser       = Browser();

            await SignIn(browser);
            await Register(browser, authenticator);

            using var first = Browser();

            var (_, options)  = await Call(first, HttpMethod.Post, "api/auth/passkeys/login/options", []);
            var ceremonyId    = options?.Value<String>("ceremonyId");
            var challenge     = options?["publicKey"]?.Value<String>("challenge") ?? "";

            Assert.That((await Call(first, HttpMethod.Post, "api/auth/passkeys/login",
                                    new JObject(new JProperty("ceremonyId", ceremonyId),
                                                new JProperty("credential", authenticator.Authenticate(challenge))))).Status,
                        Is.EqualTo(HttpStatusCode.OK),
                        "the first time");

            using var replay = Browser();

            Assert.That((await Call(replay, HttpMethod.Post, "api/auth/passkeys/login",
                                    new JObject(new JProperty("ceremonyId", ceremonyId),
                                                new JProperty("credential", authenticator.Authenticate(challenge))))).Status,
                        Is.Not.EqualTo(HttpStatusCode.OK),
                        "and never again");

        }

        #endregion

        #region WithoutASession_NothingIsRegistered()

        [Test]
        public async Task WithoutASession_NothingIsRegistered()
        {

            using var browser = Browser();

            var (options, _)  = await Call(browser, HttpMethod.Post, "api/auth/passkeys/register/options", []);
            var (listed,  _)  = await Call(browser, HttpMethod.Get,  "api/auth/passkeys");

            Assert.Multiple(() =>
            {
                Assert.That(options,  Is.EqualTo(HttpStatusCode.Unauthorized), "a passkey is added to an account, so the account has to be open");
                Assert.That(listed,   Is.EqualTo(HttpStatusCode.Unauthorized));
            });

        }

        #endregion

    }

}
