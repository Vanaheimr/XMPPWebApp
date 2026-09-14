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
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// The JSON API the browser talks to, registered at "/api": the sign-in,
    /// the account, the list of chats, the messages of one chat, sending, and
    /// one Server-Sent Events stream that carries everything that happens.
    /// </summary>
    /// <remarks>
    /// It lives in its own HTTPAPI so that unknown API paths never reach the
    /// single-page-application fallback of the web HTTPAPI at "/": Hermod
    /// dispatches a request to the most specific HTTPAPI first.
    ///
    /// Everything below /api/v1 except the sign-in itself needs the session
    /// cookie. The XMPP side - the account, the client made from it, and which
    /// of its events end up where - lives in XMPPWebAPI.XMPP.cs.
    /// </remarks>
    public sealed partial class XMPPWebAPI : HTTPAPI
    {

        #region Data

        /// <summary>
        /// The default root path of this API.
        /// </summary>
        public static readonly HTTPPath  DefaultRootPath     = HTTPPath.Parse("/api");

        /// <summary>
        /// The identification of the Server-Sent Events source.
        /// </summary>
        public const           String    EventSourceName     = "chat";

        /// <summary>
        /// How long a failed sign-in waits before it answers. Not a lock-out,
        /// just enough to make guessing a slow business.
        /// </summary>
        public static readonly TimeSpan  FailedLoginDelay    = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// The most characters a message may have.
        /// </summary>
        public const           Int32     MaxMessageLength    = 10_000;

        /// <summary>
        /// The most messages one scroll into the past may ask the archive for.
        /// </summary>
        public const           Int32     MaxHistoryPageSize  = 500;

        private readonly DateTimeOffset  startedAt           = DateTimeOffset.UtcNow;
        private readonly ILogger         logger;
        private readonly ILoggerFactory? loggerFactory;

        #endregion

        #region Properties

        /// <summary>
        /// The version reported by the status resource.
        /// </summary>
        public String                    Version     { get; }

        /// <summary>
        /// The signed-in browsers.
        /// </summary>
        public WebSessions               Sessions    { get; }

        /// <summary>
        /// The conversations, as far as this process has seen them.
        /// </summary>
        public ChatStore                 Chats          { get; }

        /// <summary>
        /// Where the conversations are kept between starts, or null when
        /// nothing is kept.
        /// </summary>
        public ChatArchive?              Archive        { get; }

        /// <summary>
        /// How much of the archive is loaded into the store at a start, and
        /// therefore how far back a browser can look without asking for more.
        /// </summary>
        public TimeSpan                  HistoryWindow  { get; }

        /// <summary>
        /// The Server-Sent Events source every browser hangs on (/api/v1/events).
        /// </summary>
        public HTTPEventSource<JObject>  Events         { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// Nothing connects yet: <see cref="ConnectAsync"/> does, once the
        /// web server answers.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="Sessions">The web sessions.</param>
        /// <param name="AccountFile">The file the account settings live in.</param>
        /// <param name="WebLoginFile">The file the web login lives in.</param>
        /// <param name="Settings">The account to start with, or null when none is configured yet.</param>
        /// <param name="Source">Where those settings came from.</param>
        /// <param name="RootPath">The root path of the API, "/api" by default.</param>
        /// <param name="Version">The version reported by the status resource.</param>
        /// <param name="Chats">The chat store; an empty one by default.</param>
        /// <param name="Archive">Where the conversations are kept, or null to keep none.</param>
        /// <param name="HistoryWindow">How much of the archive is loaded at a start.</param>
        /// <param name="LoggerFactory">An optional logger factory, handed on to every XMPP client.</param>
        public XMPPWebAPI(HTTPServer        HTTPServer,
                          WebSessions       Sessions,
                          AccountFile       AccountFile,
                          WebLoginFile      WebLoginFile,
                          AccountSettings?  Settings        = null,
                          AccountSource     Source          = AccountSource.None,
                          HTTPPath?         RootPath        = null,
                          String?           Version         = null,
                          ChatStore?        Chats           = null,
                          ChatArchive?      Archive         = null,
                          TimeSpan?         HistoryWindow   = null,
                          ILoggerFactory?   LoggerFactory   = null)

            : base(HTTPServer,
                   RootPath:     RootPath ?? DefaultRootPath,
                   Description:  I18NString.Create("XMPP WebApp JSON API"))

        {

            this.Version        = Version
                                      ?? typeof(XMPPWebAPI).Assembly.GetName().Version?.ToString(3)
                                      ?? "0.0.0";

            this.Sessions       = Sessions;
            this.AccountFile    = AccountFile;
            this.WebLoginFile   = WebLoginFile;
            this.Chats          = Chats ?? new ChatStore();
            this.Archive        = Archive;
            this.HistoryWindow  = HistoryWindow ?? ChatArchive.DefaultHistoryWindow;
            this.loggerFactory  = LoggerFactory;
            this.logger         = LoggerFactory?.CreateLogger<XMPPWebAPI>() ?? NullLogger<XMPPWebAPI>.Instance;

            // Hermod caches the last events and replays them to a new client.
            // The browser ignores everything older than the snapshot it loaded,
            // so a replay costs nothing but bytes; what it buys is that a browser
            // which reconnects after a hiccup gets what it missed in between.
            this.Events         = this.AddJSONEventSource(
                                      HTTPEventSource_Id.Parse(EventSourceName),
                                      MaxNumberOfCachedEvents:  200,
                                      RetryInterval:            TimeSpan.FromSeconds(2),
                                      EnableLogging:            false
                                  );

            this.Chats.OnChatChanged     += (sequence, chat)    => Publish("chat",    sequence, new JObject(new JProperty("chat",    chat.   ToJSON())));

            // Into the archive first, then to the browsers: both are a handover
            // that returns at once - a queue and a fire-and-forget - and of the
            // two, the one that must not be dropped goes first.
            this.Chats.OnMessageChanged  += (sequence, message) => {
                                                this.Archive?.Record(message);
                                                Publish("message", sequence, new JObject(new JProperty("message", message.ToJSON())));
                                            };

            // A file the archive fetched becomes part of its message, which
            // sends it on to the browsers - and, through the handler above,
            // back into the archive, where the line is recognised as one
            // already written.
            //
            // "this." throughout, and not for tidiness: the parameters of this
            // constructor shadow the properties, and one of them may be null
            // where the property never is.
            if (this.Archive is not null)
                this.Archive.OnMediaStored += (chat, messageId, media) => this.Chats.AttachMedia(chat, messageId, media);

            if (Settings is not null)
            {
                this.Settings  = Settings;
                this.Source    = Source == AccountSource.None ? AccountSource.File : Source;
                this.Client    = WireUp(Settings);

                Archive?.UseAccount(Settings.BareJID);

            }

            RegisterURLTemplates();

        }

        #endregion


        #region (private) RegisterURLTemplates()

        private void RegisterURLTemplates()
        {

            AddHandler(HTTPPath.Root + "v1/auth/login",               Login,          HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/auth/logout",              Logout,         HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/auth/me",                  Me,             HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/status",                   GetStatus,      HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/connection/reconnect",     Reconnect,      HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/account",                  GetAccount,     HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/account",                  PutAccount,     HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/account",                  DeleteAccount,  HTTPMethod.DELETE);

            AddHandler(HTTPPath.Root + "v1/weblogin",                 GetWebLogin,    HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/weblogin",                 PutWebLogin,    HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/chats",                    ListChats,      HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/chats",                    OpenChat,       HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/messages",     GetMessages,    HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/messages",     SendMessage,    HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/media/{name}", GetMedia,       HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/read",         MarkRead,       HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/state",        SendChatState,  HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/contact",      Contact,        HTTPMethod.POST);

            AddHandler(HTTPMethod.GET,
                       HTTPPath.Root + "v1/events",
                       HTTPContentType.Text.EVENTSTREAM,
                       StreamEvents);

            // Everything else below /api answers with a JSON 404 instead of
            // Hermod's mixed 404/500 for unknown paths.
            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD, HTTPMethod.POST, HTTPMethod.PUT, HTTPMethod.DELETE })
                AddHandler(HTTPPath.Root + "{path..}", UnknownPath, method);

        }

        #endregion


        #region (private) Login          (Request)

        /// <summary>
        /// POST /api/v1/auth/login with {"username", "password"}: the session
        /// cookie, or 401 after a short pause.
        /// </summary>
        private async Task<HTTPResponse> Login(HTTPRequest Request)
        {

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!Sessions.TryLogin(json.Value<String>("username"),
                                   json.Value<String>("password"),
                                   out var session))
            {

                logger.LogWarning("Web sign-in refused for {Remote}", Request.RemoteSocket);

                await Task.Delay(FailedLoginDelay, Request.CancellationToken);

                return ErrorJSON(Request, HTTPStatusCode.Unauthorized, "Wrong username or password.");

            }

            logger.LogInformation("Web sign-in of '{User}' from {Remote}", session.UserId, Request.RemoteSocket);

            return new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.OK,
                       ContentType     = HTTPContentType.Application.JSON_UTF8,
                       Content         = Encoding.UTF8.GetBytes(MeJSON(session).ToString(Formatting.None)),
                       CacheControl    = "no-store",
                       SetCookie       = Sessions.SessionCookie(session)
                   }.WithCommonSecurityHeaders().AsImmutable;

        }

        #endregion

        #region (private) Logout         (Request)

        /// <summary>
        /// POST /api/v1/auth/logout: ends the session and expires the cookie.
        /// </summary>
        private Task<HTTPResponse> Logout(HTTPRequest Request)
        {

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            Sessions.SignOut(Request);

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {
                           HTTPStatusCode  = HTTPStatusCode.NoContent,
                           CacheControl    = "no-store",
                           SetCookie       = Sessions.ExpiredCookie()
                       }.WithCommonSecurityHeaders().AsImmutable
                   );

        }

        #endregion

        #region (private) Me             (Request)

        /// <summary>
        /// GET /api/v1/auth/me: who is signed in, or 401.
        /// </summary>
        private Task<HTTPResponse> Me(HTTPRequest Request)

            => Task.FromResult(
                   TryGetSession(Request, out var session, out var unauthorized)
                       ? JSONResponse(Request, HTTPStatusCode.OK, MeJSON(session))
                       : unauthorized
               );

        #endregion


        #region (private) GetStatus      (Request)

        /// <summary>
        /// GET /api/v1/status: the XMPP connection as it is right now.
        /// </summary>
        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var now = DateTimeOffset.UtcNow;

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               new JProperty("service",           "XMPP WebApp"),
                               new JProperty("version",           Version),
                               new JProperty("hermod",            typeof(HTTPServer).Assembly.GetName().Version?.ToString(3)),
                               new JProperty("ratatoskr",         typeof(XMPPClient).Assembly.GetName().Version?.ToString(3)),
                               new JProperty("timestamp",         now.ToString("o")),
                               new JProperty("uptime",            (now - startedAt).ToString(@"d\.hh\:mm\:ss")),
                               new JProperty("connection",        ConnectionJSON()),
                               new JProperty("account",           AccountJSON()),
                               new JProperty("contacts",          Client?.Roster.Items.Count ?? 0),
                               new JProperty("chats",             Chats.Count),
                               new JProperty("unread",            Chats.Unread),
                               new JProperty("sessions",          Sessions.Store.Count),
                               new JProperty("seq",               Chats.Sequence)
                           )
                       )
                   );

        }

        #endregion

        #region (private) Reconnect      (Request)

        /// <summary>
        /// POST /api/v1/connection/reconnect: connect again after the client
        /// gave up. 409 while connected or connecting, or without an account.
        /// </summary>
        private Task<HTTPResponse> Reconnect(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            if (Client is null)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.Conflict, "No XMPP account is configured yet."));

            if (Client.IsConnected)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.Conflict, "Already connected."));

            if (!StartConnecting())
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.Conflict, "Already connecting."));

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.Accepted,
                           new JObject(new JProperty("connection", ConnectionJSON()))
                       )
                   );

        }

        #endregion


        #region (private) GetAccount     (Request)

        /// <summary>
        /// GET /api/v1/account: the account settings without the password, and
        /// the connection made from them.
        /// </summary>
        private Task<HTTPResponse> GetAccount(HTTPRequest Request)

            => Task.FromResult(
                   TryGetSession(Request, out _, out var unauthorized)
                       ? JSONResponse(Request, HTTPStatusCode.OK, AccountResponseJSON())
                       : unauthorized
               );

        #endregion

        #region (private) PutAccount     (Request)

        /// <summary>
        /// PUT /api/v1/account with {"jid", "password", "websocket",
        /// "minimumSasl", "allowInsecure", "trustAnnouncement"}: writes the
        /// account file, drops the old connection and connects anew. An empty
        /// password keeps the one already stored - but only for the same
        /// account on the same endpoint, see
        /// <see cref="AccountSettings.RefuseStoredPassword"/>.
        /// </summary>
        private async Task<HTTPResponse> PutAccount(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return unauthorized;

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var password    = json.Value<String>("password");
            var fromTheFile = String.IsNullOrEmpty(password);

            if (fromTheFile)
                password = Settings?.Password;

            if (!AccountSettings.TryCreate(json.Value<String>("jid"),
                                           password,
                                           json.Value<String>("websocket"),
                                           json.Value<String>("minimumSasl"),
                                           json.Value<Boolean?>("allowInsecure")     ?? false,
                                           json.Value<Boolean?>("trustAnnouncement") ?? false,
                                           out var settings,
                                           out var error))
            {
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, error);
            }

            // A password the browser is never shown must not be one the browser
            // can send elsewhere either. Reusing the stored one is a convenience
            // for the same account on the same endpoint and nothing beyond that:
            // saving a new endpoint - or a new domain, which an endpointless
            // account is asked for by name - means typing the password again.
            // Checked after TryCreate so that what is compared is the endpoint
            // as it was understood, not the text that arrived.
            if (fromTheFile &&
                AccountSettings.RefuseStoredPassword(Settings, settings) is String refusal)
            {
                logger.LogWarning("An account change to {Account} was refused: the stored password would have gone somewhere else.", settings);
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, refusal);
            }

            try
            {
                await ApplyAccountAsync(settings);
            }
            catch (Exception e)
            {
                logger.LogError("The account could not be saved to '{File}': {Error}", AccountFile.Path, e.Message);
                return ErrorJSON(Request, HTTPStatusCode.InternalServerError, $"The account could not be saved to '{AccountFile.Path}': {e.Message}");
            }

            logger.LogInformation("Account changed to {Account}", settings);

            return JSONResponse(Request, HTTPStatusCode.OK, AccountResponseJSON());

        }

        #endregion

        #region (private) DeleteAccount  (Request)

        /// <summary>
        /// DELETE /api/v1/account: forgets the account, deletes the file,
        /// disconnects. The next start asks again.
        /// </summary>
        private async Task<HTTPResponse> DeleteAccount(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return unauthorized;

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            try
            {
                await ForgetAccountAsync();
            }
            catch (Exception e)
            {
                logger.LogError("The account file '{File}' could not be removed: {Error}", AccountFile.Path, e.Message);
                return ErrorJSON(Request, HTTPStatusCode.InternalServerError, $"The account file could not be removed: {e.Message}");
            }

            logger.LogInformation("Account forgotten");

            return JSONResponse(Request, HTTPStatusCode.OK, AccountResponseJSON());

        }

        #endregion


        #region (private) ListChats      (Request)

        /// <summary>
        /// GET /api/v1/chats: every conversation, the most recently active first.
        /// </summary>
        private Task<HTTPResponse> ListChats(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var sequence = Chats.Snapshot(out var chats);

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               new JProperty("seq",    sequence),
                               new JProperty("chats",  new JArray(chats.Select(chat => chat.ToJSON())))
                           )
                       )
                   );

        }

        #endregion

        #region (private) OpenChat       (Request)

        /// <summary>
        /// POST /api/v1/chats with {"jid"}: a conversation with somebody who
        /// is not in the list yet.
        /// </summary>
        private Task<HTTPResponse> OpenChat(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!TryParseChatJID(json.Value<String>("jid"), out var jid, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            var chat = Chats.Open(jid);

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.Created,
                           new JObject(
                               new JProperty("seq",   Chats.Sequence),
                               new JProperty("chat",  chat.ToJSON())
                           )
                       )
                   );

        }

        #endregion

        #region (private) GetMessages    (Request)

        /// <summary>
        /// GET /api/v1/chats/{jid}/messages: the conversation, oldest message
        /// first. Looking at it is reading it: the unread count drops to zero.
        ///
        /// With <c>?before=&lt;timestamp&gt;</c> it answers the other question -
        /// what was said before that - out of the archive, and changes nothing:
        /// how far somebody has scrolled back is their browser's business.
        /// <c>?limit=</c> says how many, 100 by default.
        /// </summary>
        private Task<HTTPResponse> GetMessages(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (!TryParseChatJID(Request.TryGetURLParameter("jid"), out var jid, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            var limit = Math.Clamp(Request.QueryString.GetInt32("limit") ?? ChatArchive.DefaultPageSize,
                                   1,
                                   MaxHistoryPageSize);

            #region ?before=... - the older messages, straight from the archive

            if (Request.QueryString.GetString("before") is String beforeText)
            {

                if (!DateTimeOffset.TryParse(beforeText,
                                             System.Globalization.CultureInfo.InvariantCulture,
                                             System.Globalization.DateTimeStyles.RoundtripKind,
                                             out var before))
                {
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                                     $"'{beforeText}' is not a timestamp."));
                }

                if (Archive is null)
                    return Task.FromResult(
                               JSONResponse(
                                   Request,
                                   HTTPStatusCode.OK,
                                   new JObject(
                                       new JProperty("seq",       Chats.Sequence),
                                       new JProperty("before",    before.ToString("o")),
                                       new JProperty("messages",  new JArray()),
                                       new JProperty("hasMore",   false)
                                   )
                               )
                           );

                var (older, hasMore) = Archive.LoadBefore(jid, before, limit);

                return Task.FromResult(
                           JSONResponse(
                               Request,
                               HTTPStatusCode.OK,
                               new JObject(
                                   new JProperty("seq",       Chats.Sequence),
                                   new JProperty("before",    before.ToString("o")),
                                   new JProperty("messages",  new JArray(older.Select(message => message.ToJSON()))),
                                   new JProperty("hasMore",   hasMore)
                               )
                           )
                       );

            }

            #endregion

            // A deep link to somebody nobody has talked to yet opens the chat.
            Chats.Open(jid);
            Chats.MarkRead(jid);
            Chats.TrySnapshot(jid, out var sequence, out var chat, out var messages);

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               new JProperty("seq",       sequence),
                               new JProperty("chat",      chat?.ToJSON()),
                               new JProperty("messages",  new JArray(messages.Select(message => message.ToJSON()))),
                               new JProperty("hasMore",   HasOlderThan(jid, messages))
                           )
                       )
                   );

        }


        /// <summary>
        /// Whether the archive holds anything of this conversation from before
        /// what the browser is about to be handed - which is what tells the
        /// browser whether scrolling up leads anywhere.
        /// </summary>
        private Boolean HasOlderThan(JID                             Chat,
                                     IReadOnlyList<ChatMessage>      Loaded)

            => Archive is not null &&
               Archive.HasOlderThan(Chat,
                                    Loaded.Count > 0
                                        ? Loaded[0].Timestamp
                                        : DateTimeOffset.UtcNow);

        #endregion

        #region (private) GetMedia       (Request)

        /// <summary>
        /// GET /api/v1/chats/{jid}/media/{name}: a file that was shared in this
        /// conversation and fetched into the archive.
        /// </summary>
        /// <remarks>
        /// It is served from this program's own origin, so what it is served as
        /// is a security decision and not a convenience: only the types
        /// <see cref="MediaStore"/> is willing to keep are named, everything
        /// else is a download, and nothing is ever sniffed. The
        /// Content-Security-Policy of the page does the same job from the other
        /// side.
        ///
        /// A session is required, like everywhere below /api/v1 - the pictures
        /// of a conversation are the conversation.
        /// </remarks>
        private Task<HTTPResponse> GetMedia(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (!TryParseChatJID(Request.TryGetURLParameter("jid"), out var jid, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            if (Archive is null ||
                !Archive.TryGetMedia(jid, Request.TryGetURLParameter("name"), out var path, out var contentType))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, "No such file."));
            }

            Byte[] content;

            try
            {
                content = File.ReadAllBytes(path!);
            }
            catch (Exception e)
            {
                logger.LogWarning("'{Path}' could not be read: {Error}", path, e.Message);
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.InternalServerError, "The file could not be read."));
            }

            var builder = new HTTPResponse.Builder(Request) {
                              HTTPStatusCode  = HTTPStatusCode.OK,
                              ContentType     = HTTPContentType.TryParse(contentType ?? "", out var parsed)
                                                    ? parsed
                                                    : HTTPContentType.Application.OCTETSTREAM,
                              Content         = content,
                              // The name is the time it arrived: a file in here
                              // never changes, and nothing but this session may
                              // see it - so a browser may keep it, and no proxy
                              // may.
                              CacheControl    = "private, max-age=31536000, immutable"
                          };

            // A type this program does not vouch for is not opened in a tab.
            if (contentType is null)
                builder.SetHeaderField("Content-Disposition", "attachment");

            return Task.FromResult(builder.WithCommonSecurityHeaders().AsImmutable);

        }

        #endregion

        #region (private) SendMessage    (Request)

        /// <summary>
        /// POST /api/v1/chats/{jid}/messages with {"body"}: sends the message
        /// and answers with the line as it now stands in the conversation.
        /// </summary>
        private async Task<HTTPResponse> SendMessage(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return unauthorized;

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseChatJID(Request.TryGetURLParameter("jid"), out var jid, out var error))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, error);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var body = XmlText.Sanitize((json.Value<String>("body") ?? "").TrimEnd());

            if (body.Trim().Length == 0)
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "The message is empty.");

            if (body.Length > MaxMessageLength)
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, $"The message is longer than {MaxMessageLength} characters.");

            if (Client is not { IsConnected: true } client)
                return ErrorJSON(Request, HTTPStatusCode.ServiceUnavailable, NotConnectedText());

            String messageId;

            try
            {
                messageId = await client.SendMessageAsync(jid, body);
            }
            catch (Exception e)
            {
                logger.LogWarning("Sending to {Jid} failed: {Error}", jid, e.Message);
                return ErrorJSON(Request, HTTPStatusCode.BadGateway, $"The message could not be sent: {e.Message}");
            }

            var message = Chats.AddOutgoing(jid, messageId, body, DateTimeOffset.UtcNow);

            return JSONResponse(
                       Request,
                       HTTPStatusCode.Created,
                       new JObject(
                           new JProperty("seq",      Chats.Sequence),
                           new JProperty("message",  message.ToJSON())
                       )
                   );

        }

        #endregion

        #region (private) MarkRead       (Request)

        /// <summary>
        /// POST /api/v1/chats/{jid}/read: somebody looked at the conversation.
        /// </summary>
        private Task<HTTPResponse> MarkRead(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            if (!TryParseChatJID(Request.TryGetURLParameter("jid"), out var jid, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            Chats.MarkRead(jid);

            return Task.FromResult(NoContent(Request));

        }

        #endregion

        #region (private) SendChatState  (Request)

        /// <summary>
        /// POST /api/v1/chats/{jid}/state with {"state"}: tells the far end
        /// that we are typing, paused, active, inactive or gone (XEP-0085).
        /// Best effort - without a connection there is nobody to tell.
        /// </summary>
        private async Task<HTTPResponse> SendChatState(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return unauthorized;

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseChatJID(Request.TryGetURLParameter("jid"), out var jid, out var error))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, error);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!Enum.TryParse<ChatState>(json.Value<String>("state") ?? "", ignoreCase: true, out var state))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "The state has to be one of: active, composing, paused, inactive, gone.");

            if (Client is { IsConnected: true } client)
            {
                try
                {
                    await client.Connection.SendChatStateAsync(jid, state);
                }
                catch (Exception e)
                {
                    logger.LogDebug("Chat state to {Jid} not sent: {Error}", jid, e.Message);
                }
            }

            return NoContent(Request);

        }

        #endregion

        #region (private) Contact        (Request)

        /// <summary>
        /// POST /api/v1/chats/{jid}/contact with {"action"}: "add" asks the far
        /// end to become a contact, "accept" and "deny" answer their request,
        /// "remove" takes them out of the roster.
        /// </summary>
        private async Task<HTTPResponse> Contact(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return unauthorized;

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseChatJID(Request.TryGetURLParameter("jid"), out var jid, out var error))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, error);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var action = json.Value<String>("action")?.Trim().ToLowerInvariant();

            if (action is not ("add" or "accept" or "deny" or "remove"))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "The action has to be one of: add, accept, deny, remove.");

            if (Client is not { IsConnected: true } client)
                return ErrorJSON(Request, HTTPStatusCode.ServiceUnavailable, NotConnectedText());

            try
            {

                switch (action)
                {

                    case "add":
                        await client.AddContactAsync(jid);
                        break;

                    case "accept":
                        await client.AcceptSubscriptionAsync(jid);
                        Chats.SetPendingRequest(jid, false);
                        break;

                    case "deny":
                        await client.DenySubscriptionAsync(jid);
                        Chats.SetPendingRequest(jid, false);
                        break;

                    case "remove":
                        await client.RemoveContactAsync(jid);
                        break;

                }

            }
            catch (Exception e)
            {
                logger.LogWarning("Contact action '{Action}' for {Jid} failed: {Error}", action, jid, e.Message);
                return ErrorJSON(Request, HTTPStatusCode.BadGateway, $"The request could not be sent: {e.Message}");
            }

            return NoContent(Request);

        }

        #endregion

        #region (private) StreamEvents   (Request)

        /// <summary>
        /// GET /api/v1/events: the Server-Sent Events stream. Modelled on
        /// Hermod's MapEventSource, with the session checked first and
        /// without opening the stream to other origins.
        /// </summary>
        private Task<HTTPResponse> StreamEvents(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var clientId = Request.RemoteSocket.ToString();

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {

                           HTTPStatusCode  = HTTPStatusCode.OK,
                           Server          = HTTPServer.HTTPServerName,
                           ContentType     = HTTPContentType.Text.EVENTSTREAM,
                           CacheControl    = "no-cache",
                           Connection      = ConnectionType.KeepAlive,

                           HTTPSSEWorker   = async (response, stream) => {

                               try
                               {

                                   await stream.WriteAsync("retry: ");
                                   await stream.WriteAsync(((UInt32) Events.RetryInterval.TotalMilliseconds).ToString());
                                   await stream.WriteAsync("\n\n");

                                   // The preamble has to leave the buffer now, not with
                                   // the first event: on a quiet evening the browser would
                                   // otherwise wait for its first byte until its own read
                                   // timeout expired.
                                   await stream.FlushAsync(Request.CancellationToken);

                                   await foreach (var httpEvent in Events.GetAllEventsGreater(
                                                                       clientId,
                                                                       Request.GetHeaderField(HTTPRequestHeaderField.LastEventId),
                                                                       Request.CancellationToken
                                                                   ))
                                   {
                                       await stream.WriteAsync(httpEvent.SerializedHeader);
                                       await stream.WriteAsync(httpEvent.SerializedData);
                                       await stream.WriteAsync("\n\n");
                                       await stream.FlushAsync(Request.CancellationToken);
                                   }

                               }
                               catch (OperationCanceledException)
                               {
                                   await Events.Unsubscribe(clientId);
                               }
                               catch (ObjectDisposedException)
                               {
                                   await Events.Unsubscribe(clientId);
                               }
                               catch (Exception e)
                               {
                                   await Events.Unsubscribe(clientId);
                                   logger.LogDebug("The event stream of {Client} ended: {Error}", clientId, e.Message);
                               }

                           }

                       }.WithCommonSecurityHeaders().AsImmutable
                   );

        }

        #endregion

        #region (private) UnknownPath    (Request)

        private Task<HTTPResponse> UnknownPath(HTTPRequest Request)

            => Task.FromResult(
                   JSONResponse(
                       Request,
                       HTTPStatusCode.NotFound,
                       new JObject(
                           new JProperty("error",  "Unknown API path"),
                           new JProperty("path",   Request.Path.ToString())
                       )
                   )
               );

        #endregion


        #region (private) Publish(SubEvent, Sequence, JSON)

        /// <summary>
        /// Hands an event to every browser. Fire-and-forget on purpose: this is
        /// called from inside the chat store's lock and from the XMPP receive
        /// loop, and neither should wait for a slow browser.
        /// </summary>
        private void Publish(String   SubEvent,
                             Int64    Sequence,
                             JObject  JSON)
        {

            JSON.AddFirst(new JProperty("seq", Sequence));

            Events.SubmitEvent(SubEvent, JSON).
                   ContinueWith(task => logger.LogWarning("Publishing a '{Event}' event failed: {Error}", SubEvent, task.Exception?.GetBaseException().Message),
                                TaskContinuationOptions.OnlyOnFaulted);

        }

        #endregion

        #region (private) TryGetSession(Request, out Session, out Unauthorized)

        /// <summary>
        /// The live session behind the request, or the 401 response - which
        /// also expires a stale cookie, so that the browser stops sending it.
        /// </summary>
        private Boolean TryGetSession(HTTPRequest                             Request,
                                      [NotNullWhen(true)]  out Session?       Session,
                                      [NotNullWhen(false)] out HTTPResponse?  Unauthorized)
        {

            if (Sessions.TryGetSession(Request, out Session))
            {
                Unauthorized = null;
                return true;
            }

            var builder = new HTTPResponse.Builder(Request) {
                              HTTPStatusCode  = HTTPStatusCode.Unauthorized,
                              ContentType     = HTTPContentType.Application.JSON_UTF8,
                              Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "Sign in required.")).ToString(Formatting.None)),
                              CacheControl    = "no-store"
                          };

            if (Sessions.HasCookie(Request))
                builder.SetCookie = Sessions.ExpiredCookie();

            Unauthorized = builder.WithCommonSecurityHeaders().AsImmutable;
            return false;

        }

        #endregion

        #region (private static) RefuseCrossSite(Request)

        /// <summary>
        /// The 403 for a request that another site made the browser send, or
        /// null when the request is our own page's.
        /// </summary>
        /// <remarks>
        /// The cookie is SameSite=strict, so a cross-site request would arrive
        /// without a session anyway. This is the second lock on the same door:
        /// browsers say where a request came from (Sec-Fetch-Site, Origin), and
        /// a state-changing request from anywhere but this origin is refused
        /// before it is even read.
        /// </remarks>
        private static HTTPResponse? RefuseCrossSite(HTTPRequest Request)
        {

            var site = Request.GetHeaderField("Sec-Fetch-Site");

            if (site is not null && site is not ("same-origin" or "none"))
                return ErrorJSON(Request, HTTPStatusCode.Forbidden, "Cross-site requests are refused.");

            var origin = Request.GetHeaderField("Origin");

            if (origin is not null && origin != "null")
            {

                var host = Request.GetHeaderField("Host") ?? "";

                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    !uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase))
                {
                    return ErrorJSON(Request, HTTPStatusCode.Forbidden, "Cross-site requests are refused.");
                }

            }

            return null;

        }

        #endregion

        #region (private static) TryParseJSONObject(Request, out JSON, out ErrorResponse)

        /// <summary>
        /// The request body as a JSON object, or the 400 response describing
        /// what is wrong with it.
        /// </summary>
        private static Boolean TryParseJSONObject(HTTPRequest                             Request,
                                                  [NotNullWhen(true)]  out JObject?       JSON,
                                                  [NotNullWhen(false)] out HTTPResponse?  ErrorResponse)
        {

            JSON           = null;
            ErrorResponse  = null;

            var text = Request.HTTPBodyAsUTF8String;

            if (String.IsNullOrWhiteSpace(text))
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, "The request body must be a JSON object!");
                return false;
            }

            try
            {
                JSON = JObject.Parse(text);
                return true;
            }
            catch (JsonException e)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, $"Invalid JSON: {e.Message}");
                return false;
            }

        }

        #endregion

        #region (private static) TryParseChatJID(Text, out JID, out Error)

        /// <summary>
        /// The far end of a conversation: an account, without its resource.
        /// </summary>
        private static Boolean TryParseChatJID(String?                            Text,
                                               out JID                            JID,
                                               [NotNullWhen(false)] out String?   Error)
        {

            JID    = default;
            Error  = null;

            if (String.IsNullOrWhiteSpace(Text))
            {
                Error = "A JID is required.";
                return false;
            }

            if (!Ratatoskr.JID.TryParse(Text.Trim(), out var parsed))
            {
                Error = $"'{Text.Trim()}' is not a valid JID.";
                return false;
            }

            if (parsed.Localpart is null)
            {
                Error = $"'{parsed}' names a domain and no account.";
                return false;
            }

            JID = parsed.Bare;
            return true;

        }

        #endregion

        #region (private) MeJSON(Session) / AccountResponseJSON() / NotConnectedText()

        private static JObject MeJSON(Session Session)

            => new (
                   new JProperty("username",  Session.UserId.ToString()),
                   new JProperty("session",   new JObject(
                                                  new JProperty("createdAt",  Session.CreatedAt.ToString("o")),
                                                  new JProperty("expiresAt",  Session.ExpiresAt.ToString("o"))
                                              ))
               );

        private JObject AccountResponseJSON()

            => new (
                   new JProperty("configured",  Settings is not null),
                   new JProperty("source",      Source.ToString().ToLowerInvariant()),
                   new JProperty("account",     AccountJSON()),
                   new JProperty("connection",  ConnectionJSON())
               );

        private String NotConnectedText()

            => Client is null
                   ? "No XMPP account is configured yet."
                   : "Not connected to the XMPP server.";

        #endregion

        #region (private static) NoContent(Request) / ErrorJSON(...) / JSONResponse(...)

        private static HTTPResponse NoContent(HTTPRequest Request)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = HTTPStatusCode.NoContent,
                   CacheControl    = "no-store"
               }.WithCommonSecurityHeaders().AsImmutable;


        private static HTTPResponse ErrorJSON(HTTPRequest     Request,
                                              HTTPStatusCode  StatusCode,
                                              String          Message)

            => JSONResponse(
                   Request,
                   StatusCode,
                   new JObject(new JProperty("error", Message))
               );


        private static HTTPResponse JSONResponse(HTTPRequest     Request,
                                                 HTTPStatusCode  StatusCode,
                                                 JToken          JSON)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = StatusCode,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(JSON.ToString(Formatting.None)),
                   CacheControl    = "no-store"
               }.WithCommonSecurityHeaders().AsImmutable;

        #endregion

    }

}
