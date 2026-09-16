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

using System.Net;
using System.Text;

using Microsoft.Extensions.Logging;

using NUnit.Framework;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// The API as a browser meets it: a real server on a real port, answering
    /// real requests.
    /// </summary>
    /// <remarks>
    /// Everything else in this project tests a decision - may this password be
    /// reused, is this session still live, is this source out of attempts - and
    /// a decision that is right and never asked is worth nothing. Four of them
    /// guard something now, and all four depend on sitting at the right place
    /// in the right handler:
    ///
    /// <list type="bullet">
    /// <item>the stored password is checked only when it came from the file,
    /// after the settings were understood and before they are applied;</item>
    /// <item>the session is checked for every event, before the write;</item>
    /// <item>the ration and the ceiling are checked before the verification,
    /// because a gate behind the work is no gate;</item>
    /// <item>a name in a media URL is checked before it becomes a path.</item>
    /// </list>
    ///
    /// Each of those was verified by hand, once, with curl. This fixture is
    /// what makes them stay verified: move one of those lines and something
    /// here goes red.
    /// </remarks>
    [TestFixture]
    public class HTTPRouteTests
    {

        #region Data

        private const String Username = "admin";
        private const String Password = "correct-horse-battery-staple";

        private String       root     = "";
        private HTTPServer?  server;
        private XMPPWebAPI?  api;
        private Uri          origin   = new ("http://127.0.0.1/");
        private Recorder     log      = new ();

        #endregion

        #region (private) Recorder

        /// <summary>
        /// What the API said while the test ran.
        /// </summary>
        /// <remarks>
        /// For the one thing a client cannot see from outside: whether a gate
        /// fired, or whether nothing simply happened to arrive. Those two look
        /// identical over HTTP and mean opposite things.
        /// </remarks>
        private sealed class Recorder : ILoggerProvider, ILogger
        {

            private readonly List<String>  lines  = [];
            private readonly Lock          @lock  = new();

            public ILogger CreateLogger(String CategoryName) => this;
            public IDisposable? BeginScope<TState>(TState State) where TState : notnull => null;
            public Boolean IsEnabled(LogLevel Level) => true;
            public void Dispose() { }

            public void Log<TState>(LogLevel                         Level,
                                    EventId                          EventId,
                                    TState                           State,
                                    Exception?                       Exception,
                                    Func<TState, Exception?, String> Formatter)
            {
                lock (@lock) { lines.Add(Formatter(State, Exception)); }
            }

            /// <summary>
            /// Waits for a line containing this text, or says false when none
            /// came.
            /// </summary>
            public async Task<Boolean> WaitForAsync(String Text, TimeSpan Timeout)
            {

                var deadline = DateTimeOffset.UtcNow + Timeout;

                while (DateTimeOffset.UtcNow < deadline)
                {

                    lock (@lock)
                    {
                        if (lines.Any(line => line.Contains(Text, StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }

                    await Task.Delay(25);

                }

                return false;

            }

        }

        #endregion

        #region Setup / Teardown

        [SetUp]
        public async Task StartTheServer()
        {

            root = Path.Combine(Path.GetTempPath(), "XMPPWebApp-http-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            log     = new Recorder();

            server  = await HTTPServer.StartNew(IPv4Address.Parse("127.0.0.1"));

            api     = new XMPPWebAPI(
                          server,
                          new AccountFile(Path.Combine(root, "xmpp-account.json")),
                          DataDirectory:  root,
                          LoggerFactory:  LoggerFactory.Create(builder => builder.AddProvider(log).SetMinimumLevel(LogLevel.Trace))
                      );

            // The account database is an append-only log and has to be replayed
            // before anything is asked of it - here it is empty, so this makes
            // the files and nothing else.
            await api.LoadDatabase();

            Assert.That(await api.CreateUserIfNotExists(
                                  User_Id.Parse(Username),
                                  I18NString.Create(Username),
                                  SimpleEMailAddress.Parse($"{Username}@localhost"),
                                  Password:                  Password,
                                  IsAuthenticated:           true,
                                  SkipNewUserEMail:          true,
                                  SkipNewUserNotifications:  true,
                                  SkipDefaultNotifications:  true
                              ),
                        Is.Not.Null,
                        "the one account this fixture signs in as");

            origin  = new Uri($"http://127.0.0.1:{server.TCPPort}/");

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

        #region (private) Browser() / SignIn(...) / GetAsync(...) / PutAsync(...)

        /// <summary>
        /// A client that keeps its cookies, like the browser this API is for.
        /// Two of them are two browsers.
        /// </summary>
        private HttpClient Browser()

            => new (new HttpClientHandler {
                        CookieContainer    = new CookieContainer(),
                        UseCookies         = true,
                        AllowAutoRedirect  = false
                    }) {
                   BaseAddress = origin,
                   Timeout     = TimeSpan.FromSeconds(30)
               };

        private static Task<HttpResponseMessage> PostJSON(HttpClient Browser, String Path, JObject JSON)

            => Browser.PostAsync(Path, new StringContent(JSON.ToString(), Encoding.UTF8, "application/json"));

        private static Task<HttpResponseMessage> PutJSON(HttpClient Browser, String Path, JObject JSON)

            => Browser.PutAsync(Path, new StringContent(JSON.ToString(), Encoding.UTF8, "application/json"));

        /// <summary>
        /// The sign-in is HTTPExtAPI's now: "/api/auth/login", and the field is
        /// called "login" rather than "username" because it takes the e-mail
        /// address just as well.
        /// </summary>
        private static Task<HttpResponseMessage> SignIn(HttpClient Browser, String? WithPassword = null)

            => PostJSON(Browser, "api/auth/login",
                        new JObject(new JProperty("login",    Username),
                                    new JProperty("password", WithPassword ?? Password)));

        /// <summary>
        /// What the two account routes ask for beside the session: proof that
        /// the person is still there.
        /// </summary>
        private static JObject Confirmation()
            => new (new JProperty("password", Password));

        private static async Task<String> ErrorOf(HttpResponseMessage Response)
        {

            var body = await Response.Content.ReadAsStringAsync();

            try
            {
                return JObject.Parse(body).Value<String>("error") ?? body;
            }
            catch (Exception)
            {
                return body;
            }

        }

        #endregion


        #region WithoutASession_EveryRouteRefuses()

        [Test]
        public async Task WithoutASession_EveryRouteRefuses()
        {

            using var browser = Browser();

            var status   = await browser.GetAsync("api/v1/status");
            var account  = await browser.GetAsync("api/v1/account");
            var events   = await browser.GetAsync("api/v1/events");
            var media    = await browser.GetAsync("api/v1/chats/alice@example.org/media/20260914T120000Z_x.png");
            var avatar   = await browser.GetAsync("api/v1/avatars/da39a3ee5e6b4b0d3255bfef95601890afd80709");

            Assert.Multiple(() =>
            {
                Assert.That(status. StatusCode,  Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(account.StatusCode,  Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(events. StatusCode,  Is.EqualTo(HttpStatusCode.Unauthorized), "the stream is checked before it is opened");
                Assert.That(media.  StatusCode,  Is.EqualTo(HttpStatusCode.Unauthorized), "the pictures of a conversation are the conversation");

                // An avatar is published to everybody subscribed and is not a
                // secret. Which of them this account has in its roster is, and
                // an open route would answer "is this picture one of yours" for
                // any id anybody cared to try.
                Assert.That(avatar. StatusCode,  Is.EqualTo(HttpStatusCode.Unauthorized), "whose face is in whose roster is the roster");
            });

        }

        #endregion

        #region TheRightPassword_SignsIn_AndTheWrongOneDoesNot()

        [Test]
        public async Task TheRightPassword_SignsIn_AndTheWrongOneDoesNot()
        {

            using var browser = Browser();

            var wrong = await SignIn(browser, "not-it");

            Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            var right = await SignIn(browser);

            var cookie = right.Headers.TryGetValues("Set-Cookie", out var values)
                             ? String.Join("; ", values)
                             : "";

            Assert.Multiple(() =>
            {
                Assert.That(right.StatusCode,  Is.EqualTo(HttpStatusCode.OK));
                Assert.That(cookie,            Does.Contain("HttpOnly"),          "not readable by script");
                Assert.That(cookie,            Does.Contain("SameSite=strict"),   "and not sent by another site");
                Assert.That(cookie,            Does.Not.Contain(Password),        "and it carries a token, not the password");
            });

            Assert.That((await browser.GetAsync("api/v1/status")).StatusCode,
                        Is.EqualTo(HttpStatusCode.OK),
                        "and the session opens the rest");

        }

        #endregion

        #region TooManyAttempts_Answer429_BeforeAnyHashing()

        /// <summary>
        /// The ration, from outside.
        /// </summary>
        /// <remarks>
        /// The limiter belongs to HTTPExtAPI now and is a better one than the
        /// one this application had: ten attempts a minute per address AND ten
        /// per account, where this had ten a quarter of an hour per address and
        /// nothing per account. What is asserted here is not that Hermod's
        /// limiter works - Hermod tests that - but that it is reached through
        /// this application's routes and that it sits in front of the
        /// verification rather than behind it.
        ///
        /// The evidence for "in front" is the clock. A wrong password costs
        /// 600 000 rounds of PBKDF2; a refusal that never gets that far costs a
        /// dictionary lookup. If the order were the other way round the two
        /// would take the same time.
        /// </remarks>
        [Test]
        public async Task TooManyAttempts_Answer429_BeforeAnyHashing()
        {

            using var browser = Browser();

            var firstStarted = DateTimeOffset.UtcNow;
            var first        = await SignIn(browser, "no");
            var hashingTook  = DateTimeOffset.UtcNow - firstStarted;

            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "the first of ten");

            for (var attempt = 2; attempt <= 10; attempt++)
                Assert.That((await SignIn(browser, "no")).StatusCode,
                            Is.EqualTo(HttpStatusCode.Unauthorized),
                            $"attempt {attempt}");

            var refusedStarted  = DateTimeOffset.UtcNow;
            var refused         = await SignIn(browser, "no");
            var refusalTook     = DateTimeOffset.UtcNow - refusedStarted;

            var retryAfter = refused.Headers.RetryAfter?.Delta;

            Assert.Multiple(() =>
            {

                Assert.That(refused.StatusCode,  Is.EqualTo(HttpStatusCode.TooManyRequests), "the eleventh");

                Assert.That(retryAfter,          Is.Not.Null, "and it says when it is worth asking again");
                Assert.That(retryAfter!.Value,   Is.GreaterThan(TimeSpan.Zero));

                Assert.That(refusalTook,         Is.LessThan(hashingTook),
                            "and it cost less than a verification does, which is what puts the gate in front of the work");

            });

            // The right password does not buy its way past the ration either:
            // what is rationed is the asking, and at the moment of asking nobody
            // knows yet whether the password is right.
            Assert.That((await SignIn(browser)).StatusCode,
                        Is.EqualTo(HttpStatusCode.TooManyRequests));

        }

        #endregion

        #region TheStoredPassword_DoesNotFollowAChangedEndpoint()

        /// <summary>
        /// The account page is never given the password, and this is the route
        /// that could hand it to somebody else anyway: an empty password field
        /// keeps the one on file, so a session that named another server would
        /// have the login walk over to it.
        /// </summary>
        [Test]
        public async Task TheStoredPassword_DoesNotFollowAChangedEndpoint()
        {

            using var browser = Browser();

            await SignIn(browser);

            var saved = await PutJSON(browser, "api/v1/account",
                                      new JObject(new JProperty("jid",       "alice@example.org"),
                                                  new JProperty("password",  "s3cr3t-on-file"),
                                                  new JProperty("websocket", "wss://127.0.0.1:1/ws"),
                                                  new JProperty("confirm",   Confirmation())));

            Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK), await ErrorOf(saved));

            // No password, another endpoint: this is the attack.
            var moved = await PutJSON(browser, "api/v1/account",
                                      new JObject(new JProperty("jid",         "alice@example.org"),
                                                  new JProperty("websocket",   "wss://collector.example/ws"),
                                                  new JProperty("minimumSasl", "PLAIN"),
                                                  new JProperty("confirm",     Confirmation())));

            // Awaited first and asserted after: an async lambda handed to the
            // synchronous Assert.Multiple is an async void, and a failure inside
            // one does not fail the test - it hangs the run. Learned the hard
            // way, when the gate below turned this assertion red for the first
            // time.
            var movedSaid = await ErrorOf(moved);

            Assert.Multiple(() =>
            {
                Assert.That(moved.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(movedSaid,         Does.Contain("collector.example"), "and it says where it would have gone");
            });

            // The same endpoint without a password is still the convenience it
            // was meant to be.
            var kept = await PutJSON(browser, "api/v1/account",
                                     new JObject(new JProperty("jid",         "alice@example.org"),
                                                 new JProperty("websocket",   "wss://127.0.0.1:1/ws"),
                                                 new JProperty("minimumSasl", "SCRAM-SHA-1"),
                                                 new JProperty("confirm",     Confirmation())));

            Assert.That(kept.StatusCode, Is.EqualTo(HttpStatusCode.OK), await ErrorOf(kept));

            // And the endpoint on file never moved.
            var account = JObject.Parse(await (await browser.GetAsync("api/v1/account")).Content.ReadAsStringAsync());

            Assert.That(account["account"]?.Value<String>("websocket"),
                        Is.EqualTo("wss://127.0.0.1:1/ws"));

        }

        #endregion

        #region TheAccountRoutes_AskAgainBeforeTheyActs()

        /// <summary>
        /// A session opens the chats, and that is what a session is for. It must
        /// not also be enough to point this program at another XMPP server or to
        /// forget the account: those two cannot be undone by closing the tab,
        /// and a browser left open on a desk is the likeliest way in here.
        /// </summary>
        [Test]
        public async Task TheAccountRoutes_AskAgainBeforeTheyAct()
        {

            using var browser = Browser();

            await SignIn(browser);

            // Set one up, properly confirmed, so there is something to protect.
            var saved = await PutJSON(browser, "api/v1/account",
                                      new JObject(new JProperty("jid",       "alice@example.org"),
                                                  new JProperty("password",  "s3cr3t-on-file"),
                                                  new JProperty("websocket", "wss://127.0.0.1:1/ws"),
                                                  new JProperty("confirm",   Confirmation())));

            Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK), await ErrorOf(saved));

            // No confirmation at all.
            var bare = await PutJSON(browser, "api/v1/account",
                                     new JObject(new JProperty("jid",       "alice@example.org"),
                                                 new JProperty("password",  "s3cr3t-on-file"),
                                                 new JProperty("websocket", "wss://127.0.0.1:2/ws")));

            var bareJSON = JObject.Parse(await bare.Content.ReadAsStringAsync());

            Assert.Multiple(() =>
            {
                Assert.That(bare.StatusCode,                              Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(bareJSON.Value<Boolean?>("confirmationRequired"), Is.True,
                            "and it says so in a way the page can act on rather than a sentence it would have to read");
            });

            // A confirmation that is wrong.
            var wrong = await PutJSON(browser, "api/v1/account",
                                      new JObject(new JProperty("jid",       "alice@example.org"),
                                                  new JProperty("password",  "s3cr3t-on-file"),
                                                  new JProperty("websocket", "wss://127.0.0.1:2/ws"),
                                                  new JProperty("confirm",   new JObject(new JProperty("password", "not-the-password")))));

            Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            // Deleting asks too, and the account is still there afterwards.
            using var request = new HttpRequestMessage(HttpMethod.Delete, "api/v1/account");
            var deleted       = await browser.SendAsync(request);

            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "forgetting the account is the one that cannot be undone");

            var account = JObject.Parse(await (await browser.GetAsync("api/v1/account")).Content.ReadAsStringAsync());

            Assert.Multiple(() =>
            {
                Assert.That(account["account"]?.Value<String>("websocket"),  Is.EqualTo("wss://127.0.0.1:1/ws"), "nothing moved");
                Assert.That(account.Value<Boolean?>("configured"),           Is.True,                            "and nothing was forgotten");
            });

        }

        #endregion

        #region TheMediaRoute_RefusesANameThatIsAPath()

        [Test]
        public async Task TheMediaRoute_RefusesANameThatIsAPath()
        {

            using var browser = Browser();

            await SignIn(browser);

            var wanted = await browser.GetAsync("api/v1/chats/alice@example.org/media/..%2F..%2Fweb-login.json");

            Assert.That(wanted.StatusCode,
                        Is.AnyOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound),
                        "a name is a name; it never becomes a path");

            Assert.That(await wanted.Content.ReadAsStringAsync(),
                        Does.Not.Contain("hash"),
                        "and nothing of the login file comes back either way");

        }

        #endregion

        #region TheAvatarRoute_RefusesAnIdThatIsAPath()

        /// <summary>
        /// The same question one route further along, and a different answer to
        /// it.
        /// </summary>
        /// <remarks>
        /// The media route checks a file name against a list of what a name may
        /// not contain. An avatar id has a shape - forty lower-case hex digits -
        /// so it is checked against that instead, which cannot be short by one
        /// character the way a list of forbidden things always can.
        ///
        /// What the traversal is aimed at here is the account file, which lies
        /// in the data directory the avatars hang below.
        /// </remarks>
        [Test]
        public async Task TheAvatarRoute_RefusesAnIdThatIsAPath()
        {

            using var browser = Browser();

            await SignIn(browser);

            var wanted = await browser.GetAsync("api/v1/avatars/..%2F..%2Fweb-login.json");

            Assert.That(wanted.StatusCode,
                        Is.AnyOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound),
                        "an id is a hash; it never becomes a path");

            Assert.That(await wanted.Content.ReadAsStringAsync(),
                        Does.Not.Contain("hash"),
                        "and nothing of the login file comes back either way");

        }

        #endregion

        #region (private) Listener

        /// <summary>
        /// An open event stream, read the way a browser reads one, counting the
        /// events that arrive.
        /// </summary>
        private sealed class Listener : IDisposable
        {

            private readonly HttpClient               browser;
            private readonly CancellationTokenSource  stopping  = new();
            private readonly List<String>             ids       = [];
            private readonly Lock                     @lock     = new();

            /// <summary>Whether the server closed the stream.</summary>
            public Boolean Ended { get; private set; }

            /// <summary>How many events have arrived.</summary>
            public Int32 Count
            {
                get { lock (@lock) { return ids.Count; } }
            }

            public Listener(HttpClient Browser)
            {
                browser = Browser;
                _       = Task.Run(ReadAsync);
            }

            private async Task ReadAsync()
            {

                try
                {

                    using var response = await browser.SendAsync(
                                             new HttpRequestMessage(HttpMethod.Get, "api/v1/events"),
                                             HttpCompletionOption.ResponseHeadersRead,
                                             stopping.Token
                                         );

                    response.EnsureSuccessStatusCode();

                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stopping.Token));

                    while (!stopping.IsCancellationRequested)
                    {

                        var line = await reader.ReadLineAsync(stopping.Token);

                        // null is the end of the stream: the server let go.
                        if (line is null)
                            break;

                        if (line.StartsWith("id:", StringComparison.Ordinal))
                            lock (@lock) { ids.Add(line); }

                    }

                }
                catch (Exception)
                {
                    // Cancelled, or the connection went away. Either way this
                    // stream is over, which is what Ended says.
                }

                Ended = true;

            }

            /// <summary>
            /// Waits until more than <paramref name="Than"/> events have
            /// arrived, or says false when they did not.
            /// </summary>
            public async Task<Boolean> WaitForMoreThanAsync(Int32 Than, TimeSpan Timeout)
            {

                var deadline = DateTimeOffset.UtcNow + Timeout;

                while (DateTimeOffset.UtcNow < deadline)
                {

                    if (Count > Than)
                        return true;

                    await Task.Delay(25);

                }

                return false;

            }

            /// <summary>
            /// Waits for the server to close the stream, or says false when it
            /// did not.
            /// </summary>
            public async Task<Boolean> WaitForTheEndAsync(TimeSpan Timeout)
            {

                var deadline = DateTimeOffset.UtcNow + Timeout;

                while (DateTimeOffset.UtcNow < deadline)
                {

                    if (Ended)
                        return true;

                    await Task.Delay(25);

                }

                return false;

            }

            public void Dispose()
            {
                stopping.Cancel();
                stopping.Dispose();
            }

        }

        #endregion

        #region AStream_DoesNotKeepItsOwnSessionAlive()

        /// <summary>
        /// The subtlest of the session rules, and the reason the event stream
        /// asks a question of its own instead of reusing the one every other
        /// route asks.
        /// </summary>
        /// <remarks>
        /// A lookup in the session store slides the idle timeout - that is what
        /// makes "twelve hours without use" mean anything. A stream that checked
        /// itself that way would renew its own session for every event it
        /// carried, and a chat that kept arriving would hold the session open
        /// for as long as the browser stayed open, or as long as nobody closed
        /// the laptop lid. So the stream reads and does not touch.
        ///
        /// Both halves are asserted, because one without the other proves
        /// nothing: a store that never renewed anything would pass the first
        /// assertion and be broken.
        /// </remarks>
        [Test]
        public async Task AStream_DoesNotKeepItsOwnSessionAlive()
        {

            using var browser = Browser();

            await SignIn(browser);

            DateTimeOffset ExpiryOfTheOneSession()
                => api!.Sessions.First().ExpiresAt;

            var whenSignedIn = ExpiryOfTheOneSession();

            using var listener = new Listener(browser);

            // Something for the stream to carry: an account pointed at a closed
            // port, which fails and retries and says so every time.
            var saved = await PutJSON(browser, "api/v1/account",
                                      new JObject(new JProperty("jid",       "alice@example.org"),
                                                  new JProperty("password",  "s3cr3t-on-file"),
                                                  new JProperty("websocket", "wss://127.0.0.1:1/ws"),
                                                  new JProperty("confirm",   Confirmation())));

            Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK), await ErrorOf(saved));

            Assert.That(await listener.WaitForMoreThanAsync(2, TimeSpan.FromSeconds(20)),
                        Is.True,
                        "several events went through the stream");

            // The PUT above was an ordinary request and renewed the session, so
            // what is compared is from after it.
            var beforeTheEvents = ExpiryOfTheOneSession();

            Assert.That(await listener.WaitForMoreThanAsync(listener.Count + 2, TimeSpan.FromSeconds(20)),
                        Is.True,
                        "and two more after that");

            Assert.That(ExpiryOfTheOneSession(),
                        Is.EqualTo(beforeTheEvents),
                        "carrying events did not buy the session another twelve hours");

            // The other half: an ordinary request is use, and use is what the
            // idle timeout measures.
            Assert.That((await browser.GetAsync("api/v1/status")).StatusCode, Is.EqualTo(HttpStatusCode.OK));

            Assert.That(ExpiryOfTheOneSession(),
                        Is.GreaterThan(whenSignedIn),
                        "while a request does move it along");

        }

        #endregion

        #region ARevokedSession_StopsReceivingEvents()

        /// <summary>
        /// The one that needs a server, a stream and two browsers, and cannot
        /// be had any other way. A session is checked when the stream opens and
        /// the stream then stays open for hours, so what is tested is what
        /// happens to it when the session ends underneath.
        /// </summary>
        /// <remarks>
        /// The second browser is the instrument, not decoration: it is what
        /// tells this test that events really were produced after the sign-out.
        /// Without it, "the first stream received nothing more" would also be
        /// true of a server that had simply gone quiet, and the test would pass
        /// while proving nothing.
        ///
        /// What produces the events is an account pointed at a closed port -
        /// connecting, failing, reconnecting - which is a steady supply of
        /// connection events and needs nothing outside this machine.
        ///
        /// The three things asserted at the end are three different claims and
        /// all of them are needed. That no event arrived is the security one.
        /// That the log says why is what tells a gate that fired apart from a
        /// server that merely went quiet, which look identical over HTTP. And
        /// that the connection closed is what the browser acts on: EventSource
        /// finds out that it has been signed out by the stream ending and the
        /// reconnect being refused. That last one did not hold when this was
        /// written - Hermod read the keep-alive header of an SSE response and
        /// went back to waiting for a request that was never coming - and it is
        /// the reason for Hermod f903c583.
        /// </remarks>
        [Test]
        public async Task ARevokedSession_StopsReceivingEvents()
        {

            using var goes  = Browser();
            using var stays = Browser();

            await SignIn(goes);
            await SignIn(stays);

            using var listenGoes  = new Listener(goes);
            using var listenStays = new Listener(stays);

            // Something to listen to: an account that cannot connect, and keeps
            // saying so.
            var saved = await PutJSON(stays, "api/v1/account",
                                      new JObject(new JProperty("jid",       "alice@example.org"),
                                                  new JProperty("password",  "s3cr3t-on-file"),
                                                  new JProperty("websocket", "wss://127.0.0.1:1/ws"),
                                                  new JProperty("confirm",   Confirmation())));

            Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK), await ErrorOf(saved));

            Assert.That(await listenGoes. WaitForMoreThanAsync(0, TimeSpan.FromSeconds(20)), Is.True, "the stream carries events while its session is live");
            Assert.That(await listenStays.WaitForMoreThanAsync(0, TimeSpan.FromSeconds(20)), Is.True, "and so does the other one");

            var seenBeforeSignOut = listenGoes.Count;

            var out_ = await goes.PostAsync("api/auth/logout", null);

            Assert.That(out_.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.NoContent));

            // Two more events on the stream whose session is still live: that is
            // what makes the silence on the other one mean something.
            var seenByTheOther = listenStays.Count;

            Assert.That(await listenStays.WaitForMoreThanAsync(seenByTheOther + 1, TimeSpan.FromSeconds(20)),
                        Is.True,
                        "events went on being produced after the sign-out");

            // Read before the wait below, so that this says what it looks
            // like it says: at the moment the live stream had gained two
            // events, the revoked one had gained none.
            Assert.That(listenGoes.Count,
                        Is.EqualTo(seenBeforeSignOut),
                        "and not one of them reached the stream whose session is gone");

            // Silence over HTTP has two explanations that look identical from
            // out here - the gate fired, or nothing happened to be sent - and
            // they mean opposite things. The log is what tells them apart.
            Assert.That(await log.WaitForAsync("its session is gone", TimeSpan.FromSeconds(20)),
                        Is.True,
                        "the stream was ended because the session was gone, and not merely quiet");

            // And the connection goes, which is what a browser waits for: an
            // SSE body has no framing of its own, so the close is the only way
            // to say that this stream is over.
            Assert.That(await listenGoes.WaitForTheEndAsync(TimeSpan.FromSeconds(20)),
                        Is.True,
                        "and the server let the connection go rather than holding one that will never carry anything again");

            // What the browser does next: EventSource reconnects after the
            // stream drops, and the reconnect is what sends it back to the
            // sign-in page.
            using var reconnect = await goes.GetAsync("api/v1/events");

            Assert.That(reconnect.StatusCode,
                        Is.EqualTo(HttpStatusCode.Unauthorized),
                        "and opening it again with the same cookie is refused");

        }

        #endregion

    }

}
