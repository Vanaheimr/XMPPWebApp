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
using org.GraphDefined.Vanaheimr.Hermod.Passkeys;
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
    public sealed partial class XMPPWebAPI : HTTPExtAPI
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
        /// A session ends when it was not used for this long.
        /// </summary>
        /// <remarks>
        /// Named here rather than left to HTTPExtAPI, whose sessions have no
        /// idle timeout at all and last thirty days. That is a reasonable answer
        /// for a service people sign in to from their own machines; it is the
        /// wrong one for a page that holds somebody's chat archive open, where
        /// the browser left behind is the likelier way in than the password.
        /// </remarks>
        public static readonly TimeSpan  SessionIdleTime     = TimeSpan.FromHours(12);

        /// <summary>
        /// A session ends this long after the sign-in at the latest, used or
        /// not.
        /// </summary>
        public static readonly TimeSpan  SessionLifetime     = TimeSpan.FromDays(7);

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
        /// XEP-0384: the conversations somebody turned encryption off for, or
        /// null when there is no OMEMO at all.
        /// </summary>
        public PlaintextChats?           PlaintextChats { get; }

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
        /// <param name="AccountFile">The file the account settings live in.</param>
        /// <param name="Settings">The account to start with, or null when none is configured yet.</param>
        /// <param name="Source">Where those settings came from.</param>
        /// <param name="RootPath">The root path of the API, "/api" by default.</param>
        /// <param name="Version">The version reported by the status resource.</param>
        /// <param name="Chats">The chat store; an empty one by default.</param>
        /// <param name="Archive">Where the conversations are kept, or null to keep none.</param>
        /// <param name="HistoryWindow">How much of the archive is loaded at a start.</param>
        /// <param name="OmemoDirectory">XEP-0384: where the OMEMO keys live, or null to do no OMEMO.</param>
        /// <param name="DataDirectory">Where the account database and the logs go.</param>
        /// <param name="SecureCookies">Whether the session cookie is marked Secure - true behind TLS.</param>
        /// <param name="WebAuthnSettings">Passkeys, or null not to register those routes at all.</param>
        /// <param name="LoggerFactory">An optional logger factory, handed on to every XMPP client.</param>
        public XMPPWebAPI(HTTPServer        HTTPServer,
                          AccountFile       AccountFile,
                          AccountSettings?  Settings        = null,
                          AccountSource     Source          = AccountSource.None,
                          HTTPPath?         RootPath        = null,
                          String?           Version         = null,
                          ChatStore?        Chats           = null,
                          ChatArchive?      Archive         = null,
                          TimeSpan?         HistoryWindow   = null,
                          String?            OmemoDirectory    = null,
                          String             DataDirectory     = "",
                          Boolean            SecureCookies     = false,
                          WebAuthnSettings?  WebAuthnSettings  = null,
                          ILoggerFactory?    LoggerFactory     = null)

            : base(HTTPServer,
                   RootPath:              RootPath ?? DefaultRootPath,
                   Description:           I18NString.Create("XMPP WebApp JSON API"),

                   // What this application does not have and does not want: the
                   // HTML templates of the account pages - this front end is a
                   // single-page application of its own - and the notification
                   // machinery, which wants an SMTP submission client and an
                   // e-mail address for a robot. Sign-up is a separate object
                   // (SelfSignUpAPI) and is simply never made.
                   SkipURLTemplates:      true,
                   DisableNotifications:  true,

                   LoggingPath:               DataDirectory,
                   HTTPCookiePath:            "/",
                   UseSecureCookies:          SecureCookies,

                   MaxSignInSessionLifetime:  SessionLifetime,
                   SessionIdleTimeout:        SessionIdleTime,

                   // Null and the seven passkey routes are not registered at
                   // all; set and they are. What decides it is whether this
                   // process can name one origin a browser will accept - see
                   // Program.cs.
                   WebAuthnSettings:          WebAuthnSettings)

        {

            this.Version        = Version
                                      ?? typeof(XMPPWebAPI).Assembly.GetName().Version?.ToString(3)
                                      ?? "0.0.0";

            this.AccountFile    = AccountFile;
            this.Chats          = Chats ?? new ChatStore();
            this.Archive        = Archive;
            this.HistoryWindow  = HistoryWindow ?? ChatArchive.DefaultHistoryWindow;
            this.OmemoDirectory = OmemoDirectory;

            // Beside the keys, and only when there are keys: without OMEMO
            // there is nothing to turn off.
            this.PlaintextChats = OmemoDirectory is null
                                      ? null
                                      : new PlaintextChats(Path.Combine(OmemoDirectory, XMPPWebApp.Chats.PlaintextChats.DefaultFileName));
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

            // The sign-in, the sign-out and "who am I" are not registered
            // here any more: they come from HTTPExtAPI as /api/auth/login,
            // /api/auth/logout and /api/auth/me, together with the password
            // change and - once WebAuthnSettings is set - the passkeys. What is
            // left below is this application's own API, which is what the "v1"
            // was ever a promise about.

            AddHandler(HTTPPath.Root + "v1/status",                   GetStatus,      HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/connection/reconnect",     Reconnect,      HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/account",                  GetAccount,     HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/account",                  PutAccount,     HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/account",                  DeleteAccount,  HTTPMethod.DELETE);

            // The web login of this page used to be a route of its own here.
            // It is HTTPExtAPI's now: GET and PUT /api/auth/me for the account
            // itself, POST /api/auth/password to change the password.

            AddHandler(HTTPPath.Root + "v1/chats",                    ListChats,      HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/chats",                    OpenChat,       HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/messages",     GetMessages,    HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/messages",     SendMessage,    HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/media/{name}", GetMedia,       HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/files",        SendFile,       HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/read",         MarkRead,       HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/chats/{jid}/encryption",   SetEncryption,  HTTPMethod.POST);
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
                               new JProperty("sessions",          Sessions.Count),
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

            // Before anything is read out of the body: a session that was left
            // open must not be able to reconfigure the account it can read.
            if (Unconfirmed(Request, json, out var unconfirmed))
                return unconfirmed;

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

            // A DELETE that carries a body is unusual and is the lesser oddity:
            // the alternative is the confirmation in a query string, where it
            // would end up in every log this request passes.
            var json = new JObject();

            if (Request.HTTPBodyAsUTF8String is { Length: > 0 } body)
            {
                try
                {
                    json = JObject.Parse(body);
                }
                catch (Exception)
                {
                    return ErrorJSON(Request, HTTPStatusCode.BadRequest, "Invalid JSON.");
                }
            }

            if (Unconfirmed(Request, json, out var unconfirmed))
                return unconfirmed;

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

        #region (private) SendFile       (Request)

        /// <summary>
        /// POST /api/v1/chats/{jid}/files?name=… with the file as the body:
        /// puts it on the server's upload service and sends the address.
        /// </summary>
        /// <remarks>
        /// <b>The conversation's encryption decides the file's.</b> Somebody who
        /// turned encryption on for a chat and then sent a photograph in the
        /// clear would be right to be surprised - so when OMEMO is on for this
        /// conversation the file goes up under XEP-0454 and the storage host
        /// holds bytes it cannot read.
        ///
        /// What that buys is worth being exact about: the key travels in the URL
        /// to whoever gets the message, so whoever can read the message can read
        /// the file. It is the host that is shut out, not the conversation.
        ///
        /// The name travels as a query parameter and the bytes as the body,
        /// rather than as a multipart form. There is one file and one field;
        /// multipart would be a parser for a shape nothing here needs.
        /// </remarks>
        private async Task<HTTPResponse> SendFile(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return unauthorized;

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseChatJID(Request.TryGetURLParameter("jid"), out var jid, out var error))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, error);

            if (Client is not { IsConnected: true } client)
                return ErrorJSON(Request, HTTPStatusCode.ServiceUnavailable, NotConnectedText());

            var name = Request.QueryString?.GetString("name") ?? "";

            // A name travels into the URL the service hands out, so one carrying
            // a path separator is a way of writing somewhere else. Refused
            // rather than repaired: a name that had to be changed is not the
            // name that was asked for, and the sender should know.
            if (name.Length == 0 ||
                name.Contains('/') ||
                name.Contains('\\') ||
                name.Contains(".."))
            {
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "That is not a usable file name.");
            }

            var content = Request.HTTPBody;

            if (content is null || content.Length == 0)
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "There is no file in the request.");

            // Asked before anything is sent, because the service announces its
            // limit here and nowhere else - and a refusal after the bytes have
            // crossed the browser is a worse way to learn it.
            var service = await client.DiscoverUploadServiceAsync();

            if (service is null)
                return ErrorJSON(Request, HTTPStatusCode.ServiceUnavailable,
                                 "This server has no upload service, so a file cannot be sent.");

            if (service.IsTooLarge(content.Length))
                return ErrorJSON(Request, HTTPStatusCode.RequestEntityTooLarge,
                                 $"The file is {content.Length} bytes and the service takes " +
                                 $"{service.MaxFileSize} at most.");

            ChatMessage?  message;
            String?       refusal;

            try
            {
                (message, refusal) = await SendFileToAsync(jid, content, name);
            }
            catch (Exception e)
            {
                logger.LogWarning("Sending a file to {Jid} failed: {Error}", jid, e.Message);
                return ErrorJSON(Request, HTTPStatusCode.BadGateway,
                                 $"The file could not be sent: {e.Message}");
            }

            if (message is null)
                return ErrorJSON(Request, HTTPStatusCode.Conflict, refusal ?? "The file was not sent.");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.Created,
                       new JObject(new JProperty("message", message.ToJSON()))
                   );

        }

        #endregion

        #region (internal) SendFileToAsync(Jid, Content, Name)

        /// <summary>
        /// Uploads a file and puts the line into the conversation.
        /// </summary>
        /// <remarks>
        /// Apart from the route for the same reason <see cref="SendToAsync"/>
        /// is: the route reads a request and writes a status code, this decides
        /// what a file is. Keeping them together would mean the interesting half
        /// could only be exercised through a signed-in browser.
        /// </remarks>
        internal async Task<(ChatMessage? Message, String? Refusal)> SendFileToAsync(JID     Jid,
                                                                                     Byte[]  Content,
                                                                                     String  Name)
        {

            if (Client is not { IsConnected: true } client)
                return (null, NotConnectedText());

            // The conversation decides. Not "encrypt whenever OMEMO is
            // available" - that would encrypt files in a chat somebody
            // deliberately left in the clear - and not "never", which would put
            // a photograph on a server in a conversation somebody deliberately
            // encrypted.
            var encrypt = client.OmemoEnabled && Chats.EncryptionOn(Jid);

            using var stream = new MemoryStream(Content);

            var sent = encrypt
                           ? await client.SendEncryptedFileAsync(Jid, stream, Name)
                           : await client.SendFileAsync(Jid, stream, Content.LongLength, Name,
                                                        HttpFileUpload.GuessContentType(Name));

            if (!sent.Sent)
                return (null,
                        sent.Upload.Refusal    is not null ? $"The upload service refused it: {sent.Upload.Refusal}"
                      : sent.Upload.HttpStatus is not null ? $"The upload was refused with HTTP {(Int32) sent.Upload.HttpStatus}."
                      : "The file was not sent, and the service said nothing either.");

            // The address is the body, which is what XEP-0363 asks for and what
            // every other client shows. The page turns it back into a picture -
            // see links.ts - and the archive replaces it with its own copy as
            // soon as it has one.
            return (Chats.AddOutgoing(Jid, sent.MessageId!, sent.Url!.AbsoluteUri, DateTimeOffset.UtcNow),
                    null);

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

            if (Client is not { IsConnected: true })
                return ErrorJSON(Request, HTTPStatusCode.ServiceUnavailable, NotConnectedText());

            ChatMessage?  message;
            String?       refusal;

            try
            {
                (message, refusal) = await SendToAsync(jid, body);
            }
            catch (Exception e)
            {
                logger.LogWarning("Sending to {Jid} failed: {Error}", jid, e.Message);
                return ErrorJSON(Request, HTTPStatusCode.BadGateway, $"The message could not be sent: {e.Message}");
            }

            if (message is null)
                return ErrorJSON(Request, HTTPStatusCode.Conflict, refusal ?? "The message was not sent.");

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

        #region (internal) SendToAsync   (Jid, Body)

        /// <summary>
        /// Sends one message the way the policy says it should travel, and puts
        /// the line into the conversation.
        /// </summary>
        /// <remarks>
        /// Apart from the route because the two are different jobs: the route
        /// reads a request and writes a status code, this decides what a
        /// message is. Keeping them together meant the interesting half could
        /// only be exercised through a signed-in browser.
        ///
        /// <b>The rule is asked twice, and it has to be.</b> Whether the far end
        /// can be encrypted to is not knowable before trying - the device list
        /// is fetched by the encrypting itself - so the first answer is "OMEMO
        /// is on, encrypt", and if that message turns out to have reached
        /// nobody, the same rule is asked again with what has just been learnt.
        /// Falling back is safe precisely there and nowhere else: a message
        /// nobody could open was not delivered, so sending it again in the clear
        /// duplicates nothing.
        /// </remarks>
        /// <returns>
        /// The line as the conversation now holds it, or null and the sentence
        /// that says why it was not sent.
        /// </returns>
        internal async Task<(ChatMessage? Message, String? Refusal)> SendToAsync(JID     Jid,
                                                                                String  Body)
        {

            if (Client is not { IsConnected: true } client)
                return (null, NotConnectedText());

            var turnedOff           = !Chats.EncryptionOn(Jid);
            var wasEncryptedBefore  = Chats.WasEncrypted(Jid);

            String               messageId;
            OmemoIdentityCheck?  identity  = null;

            switch (HowToSend(client.OmemoEnabled, turnedOff, wasEncryptedBefore))
            {

                case Delivery.Encrypted:
                {

                    var sent = await client.SendEncryptedMessageAsync(Jid, Body);

                    if (sent.Readable)
                    {

                        messageId  = sent.MessageId;

                        // Known rather than New: a session with this device
                        // exists, because a message has just gone through it.
                        // Nothing on the sending side asks the question the
                        // receiving side asks, and calling it New would say
                        // something about a key that was never checked.
                        identity   = OmemoIdentityCheck.Known;

                        if (sent.Skipped.Count > 0)
                            PublishNotice("warning", SkippedText(Jid, sent.Skipped));

                        break;

                    }

                    if (HowToSend(false, turnedOff, wasEncryptedBefore) == Delivery.Refused)
                        return (null, CannotEncryptText(Jid, sent));

                    logger.LogInformation("OMEMO: {Jid} has no device that can read an encrypted message; sending in the clear", Jid);

                    messageId = await client.SendMessageAsync(Jid, Body);
                    break;

                }

                case Delivery.Refused:
                    return (null, CannotEncryptText(Jid, null));

                default:
                    messageId = await client.SendMessageAsync(Jid, Body);
                    break;

            }

            return (Chats.AddOutgoing(Jid, messageId, Body, DateTimeOffset.UtcNow, Identity: identity),
                    null);

        }

        /// <summary>
        /// Why a message was not sent: this conversation has been encrypted
        /// before and cannot be now.
        /// </summary>
        /// <remarks>
        /// <b>A refusal has to be actionable or it is just an obstacle.</b> The
        /// two things that produce this are a contact who has genuinely removed
        /// every OMEMO device, and somebody who has taken their device list out
        /// of the way - and the reader cannot tell them apart either, which is
        /// exactly why the program does not quietly pick one. So it names both
        /// and names the way out.
        /// </remarks>
        private static String CannotEncryptText(JID         Jid,
                                                OmemoSent?  Sent)

            => $"This conversation has been encrypted before, and {Jid} now has no device that can read an " +
               "encrypted message. Either they removed their last one, or somebody took their device list out " +
               "of the way - from here the two look the same, so this was not sent in the clear. Ask them " +
               "through another channel, or turn encryption off for this conversation if you know why." +
               (Sent?.Skipped.Count > 0
                    ? $" ({Describe(Sent.Skipped)})"
                    : "");

        /// <summary>
        /// Which devices of the recipient could not be written to, for a
        /// message that did go out to the others.
        /// </summary>
        private static String SkippedText(JID                                Jid,
                                          IReadOnlyList<OmemoSkippedDevice>  Skipped)

            => $"The message to {Jid} went out encrypted, but not every device can read it: {Describe(Skipped)}. " +
               "Those devices will show nothing at all rather than something unreadable.";

        private static String Describe(IReadOnlyList<OmemoSkippedDevice> Skipped)

            => String.Join(", ",
                           Skipped.Select(device => device.DeviceId == 0
                                                        ? $"{device.Jid} ({device.Reason})"
                                                        : $"{device.Jid} device {device.DeviceId} ({device.Reason})"));

        #endregion

        #region (private) SetEncryption  (Request)

        /// <summary>
        /// POST /api/v1/chats/{jid}/encryption with {"enabled"}: turns OMEMO
        /// off for this conversation, or back on.
        /// </summary>
        /// <remarks>
        /// <b>Off is not a preference, it is a decision with a consequence</b>,
        /// so it is stored where it survives a restart and the browser is told
        /// the state it ended in rather than the state it asked for. What it is
        /// for: clients whose OMEMO is broken in ways no correctness on this
        /// side repairs - and the alternative to a switch is a contact who
        /// cannot be written to at all.
        ///
        /// Not behind the account gate. It changes what one conversation does,
        /// not who this program is; a stolen session that turns encryption off
        /// has to write the message too, and the messages are what the session
        /// already opens.
        /// </remarks>
        private Task<HTTPResponse> SetEncryption(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            if (!TryParseChatJID(Request.TryGetURLParameter("jid"), out var jid, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (json.Value<Boolean?>("enabled") is not Boolean enabled)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "'enabled' has to be true or false."));

            var on = Chats.SetEncryption(jid, enabled);

            if (Settings is not null)
                PlaintextChats?.Set(Settings.BareJID, jid, !on);

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               new JProperty("seq",         Chats.Sequence),
                               new JProperty("encryption",  on ? "auto" : "off")
                           )
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
        /// <remarks>
        /// The session is checked again for every event, and that is the
        /// difference between a stream and every other route here. A route
        /// answers one request and the check it made is as old as the answer.
        /// This one stays open for hours and keeps delivering, so a check made
        /// only at the start would mean that signing out, changing the web
        /// password, or letting a session time out ends the right to ask for
        /// chats while leaving a channel open that keeps handing them over.
        /// The settings page says a password change ends every other session;
        /// without this it did not.
        ///
        /// Per event and not on a timer: an event is the only moment at which
        /// this stream could disclose anything, so it is the moment worth
        /// guarding. A revoked stream that nobody sends anything to sits there
        /// until the next event or until the browser goes away - and delivers
        /// nothing either way.
        /// </remarks>
        private Task<HTTPResponse> StreamEvents(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out var session, out var unauthorized))
                return Task.FromResult(unauthorized);

            var token    = session.Token;
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

                                       // Before the write, never after it: the
                                       // question is whether this event may be
                                       // handed over at all.
                                       if (!StillLive(token))
                                       {
                                           logger.LogInformation("The event stream of {Client} ended: its session is gone.", clientId);
                                           await Events.Unsubscribe(clientId);
                                           break;
                                       }

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

        #region (private) Unconfirmed(Request, JSON, out Refusal)

        /// <summary>
        /// Whether this request carries a fresh proof that the person is still
        /// there - the password of the account, or a passkey - and the 403 to
        /// answer with when it does not.
        /// </summary>
        /// <remarks>
        /// The session alone opens the chats, and that is what a session is for.
        /// It must not also be enough to point this program's XMPP account at
        /// another server or to delete it: a browser left open on a desk is the
        /// likeliest way in here, likelier than the password, and those two are
        /// the requests that cannot be undone by closing the tab again.
        ///
        /// So the two routes that reconfigure or forget the account ask once
        /// more, in the same request. Not a confirmation that is remembered for
        /// five minutes: remembering it means deciding what invalidates it, and
        /// every answer to that is a new way to be wrong. Twice into the same
        /// form is cheap, and for a passkey it is a fingerprint.
        ///
        /// <b>Which password.</b> The one of this page's account, not the XMPP
        /// one. The XMPP password has a rule of its own, further down: it may
        /// not follow a changed endpoint. The two are different questions - "are
        /// you still there" and "may this secret go there" - and a request that
        /// moves the account to another server has to answer both.
        /// </remarks>
        private Boolean Unconfirmed(HTTPRequest                            Request,
                                    JObject                                JSON,
                                    [NotNullWhen(true)] out HTTPResponse?  Refusal)
        {

            Refusal = null;

            if (!TryGetSignedInUser(Request, out var user, out _, out _))
            {
                Refusal = ErrorJSON(Request, HTTPStatusCode.Unauthorized, "Sign in required.");
                return true;
            }

            if (JSON["confirm"] is not JObject confirmation)
                return NotConfirmed(Request, out Refusal);

            // A password, which everybody has.
            if (confirmation.Value<String>("password") is { Length: > 0 } password)
            {

                if (VerifyPassword(user.Id, password))
                    return false;

                Refusal = ErrorJSON(Request, HTTPStatusCode.Forbidden, "The password is wrong.");
                return true;

            }

            // Or a passkey, which is a fingerprint rather than a password typed
            // into a page for the second time.
            if (confirmation["credential"] is JObject credential &&
                WebAuthnSettings is not null)
            {

                if (!PasskeyCeremonies.TryTake(confirmation.Value<String>("ceremonyId") ?? "",
                                               CeremonyType.Authentication,
                                               out var ceremony))
                {
                    Refusal = ErrorJSON(Request, HTTPStatusCode.Forbidden, "The confirmation timed out or was already used; please try again.");
                    return true;
                }

                if (!TryGetPasskey(credential.Value<String>("id") ?? "", out var owner, out var passkey) ||
                    owner is null || passkey is null || owner.Id != user.Id)
                {
                    Refusal = ErrorJSON(Request, HTTPStatusCode.Forbidden, "That passkey does not belong to this account.");
                    return true;
                }

                if (!WebAuthn.TryVerifyAuthentication(WebAuthnSettings, ceremony, credential, passkey, user.Id, out _, out var problem))
                {
                    Refusal = ErrorJSON(Request, HTTPStatusCode.Forbidden, problem);
                    return true;
                }

                return false;

            }

            return NotConfirmed(Request, out Refusal);

        }

        /// <summary>
        /// The 403 that asks for a confirmation, with a flag the page can act on
        /// rather than a sentence it would have to read.
        /// </summary>
        private static Boolean NotConfirmed(HTTPRequest       Request,
                                            out HTTPResponse  Refusal)
        {

            Refusal = JSONResponse(
                          Request,
                          HTTPStatusCode.Forbidden,
                          new JObject(
                              new JProperty("error",                  "Please confirm with your password or a passkey."),
                              new JProperty("confirmationRequired",   true)
                          )
                      );

            return true;

        }

        #endregion

        #region (private) TryGetSession(Request, out Session, out Unauthorized)

        /// <summary>
        /// The live session behind the request's cookie, or the 401 to answer
        /// with.
        /// </summary>
        /// <remarks>
        /// A thin name over <see cref="HTTPExtAPI.TryGetSignedInUser"/>, kept
        /// because every route here asks the same question and because this API
        /// wants the stronger promise: HTTPExtAPI can hand back a user without a
        /// session - that is what its other ways in are for - and a route of
        /// this application without a session has nothing to answer with, since
        /// the session is what the event stream and the archive are keyed on.
        /// </remarks>
        private Boolean TryGetSession(HTTPRequest                             Request,
                                      [NotNullWhen(true)]  out Session?       Session,
                                      [NotNullWhen(false)] out HTTPResponse?  Unauthorized)
        {

            if (TryGetSignedInUser(Request, out _, out Session, out Unauthorized) &&
                Session is not null)
            {
                return true;
            }

            Session       = null;
            Unauthorized ??= new HTTPResponse.Builder(Request) {
                                 HTTPStatusCode  = HTTPStatusCode.Unauthorized,
                                 ContentType     = HTTPContentType.Application.JSON_UTF8,
                                 Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "Sign in required.")).ToString(Formatting.None)),
                                 CacheControl    = "no-store"
                             }.WithCommonSecurityHeaders().AsImmutable;

            return false;

        }

        #endregion

        #region (private) StillLive(Token)

        /// <summary>
        /// Whether the session with this token is still live - asked without
        /// touching it.
        /// </summary>
        /// <remarks>
        /// For the event stream, which holds a session open for hours instead of
        /// asking once per request. It reads and does not write, because
        /// SessionStore.TryGet slides the idle timeout on every lookup: a stream
        /// that checked itself that way would renew its own session for every
        /// event, and "twelve hours unused" would quietly become "twelve hours
        /// after the browser was closed".
        /// </remarks>
        private Boolean StillLive(SecurityToken_Id Token)
        {

            if (Token.IsNullOrEmpty)
                return false;

            var now = Sessions.TimeProvider.GetUtcNow();

            foreach (var session in Sessions)
            {
                if (session.Token == Token)
                    return !session.IsExpired(now);
            }

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


        /// <summary>
        /// An error that says when it is worth asking again, as a header a
        /// client can act on and a sentence a person can read.
        /// </summary>
        private static HTTPResponse RetryLaterJSON(HTTPRequest     Request,
                                                   HTTPStatusCode  StatusCode,
                                                   TimeSpan        RetryAfter,
                                                   String          Message)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = StatusCode,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", Message)).ToString(Formatting.None)),
                   CacheControl    = "no-store",
                   RetryAfter      = Math.Max(1, (Int32) Math.Ceiling(RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)
               }.WithCommonSecurityHeaders().AsImmutable;


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
