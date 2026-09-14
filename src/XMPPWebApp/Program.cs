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

using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Passkeys;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// The web front end for one XMPP account: Ratatoskr's XMPPClient behind
    /// Hermod's HTTP server, with a browser page in front of both.
    /// </summary>
    internal static class Program
    {

        /// <summary>
        /// The account a first start makes for itself. One person runs this and
        /// signs in to it; further accounts are the account database's business
        /// and not this program's.
        /// </summary>
        public const String DefaultUsername = "admin";


        /// <summary>
        /// The manifest resource prefix of the embedded frontend bundle
        /// (see the EmbedFrontend target in XMPPWebApp.csproj).
        /// </summary>
        public const String HTTPRoot = "org.GraphDefined.Vanaheimr.XMPPWebApp.HTTPRoot.";


        public static async Task<Int32> Main(String[] Arguments)
        {

            Console.OutputEncoding = System.Text.Encoding.UTF8;

            #region Arguments

            IPPort?  port               = null;
            var      anyAddress         = false;
            String?  devDirectory       = null;
            var      selfSigned         = false;
            String?  certificateFile    = null;
            String?  certificatePEM     = null;
            String?  keyPEM             = null;
            String?  accountFilePath    = null;
            String?  originArgument     = null;
            String?  archiveDirectory   = null;
            var      keepArchive        = true;
            var      keepMedia          = true;
            var      historyDays        = ChatArchive.DefaultHistoryWindow.TotalDays;

            String?  jid                = null;
            String?  password           = null;
            String?  webSocketURI       = null;
            var      accountGiven       = false;
            var      insecure           = false;
            String?  sasl               = null;
            var      trustAnnouncement  = false;
            var      verbose            = false;

            for (var i = 0; i < Arguments.Length; i++)
            {
                switch (Arguments[i])
                {

                    case "--port":
                        if (i + 1 < Arguments.Length && UInt16.TryParse(Arguments[i + 1], out var parsedPort))
                        {
                            port = IPPort.Parse(parsedPort);
                            i++;
                        }
                        else
                        {
                            Console.Error.WriteLine("Missing or invalid port number after --port!");
                            return 2;
                        }
                        break;

                    case "--any":
                        anyAddress = true;
                        break;

                    case "--dev":
                        devDirectory = i + 1 < Arguments.Length && !Arguments[i + 1].StartsWith("--")
                                           ? Arguments[++i]
                                           : DefaultDevDirectory();
                        break;

                    case "--https":
                        selfSigned = true;
                        break;

                    case "--cert":
                        if (!TryTakeValue(Arguments, ref i, out certificateFile))
                        {
                            Console.Error.WriteLine("Missing PKCS#12 file after --cert!");
                            return 2;
                        }
                        break;

                    case "--cert-pem":
                        if (!TryTakeValue(Arguments, ref i, out certificatePEM))
                        {
                            Console.Error.WriteLine("Missing PEM file after --cert-pem!");
                            return 2;
                        }
                        break;

                    case "--key-pem":
                        if (!TryTakeValue(Arguments, ref i, out keyPEM))
                        {
                            Console.Error.WriteLine("Missing PEM file after --key-pem!");
                            return 2;
                        }
                        break;

                    case "--account":
                        if (!TryTakeValue(Arguments, ref i, out accountFilePath))
                        {
                            Console.Error.WriteLine("Missing file after --account!");
                            return 2;
                        }
                        break;

                    case "--origin":
                        if (!TryTakeValue(Arguments, ref i, out originArgument))
                        {
                            Console.Error.WriteLine("Missing URL after --origin!");
                            return 2;
                        }
                        break;

                    case "--archive":
                        if (!TryTakeValue(Arguments, ref i, out archiveDirectory))
                        {
                            Console.Error.WriteLine("Missing directory after --archive!");
                            return 2;
                        }
                        break;

                    case "--no-archive":
                        keepArchive = false;
                        break;

                    case "--no-media":
                        keepMedia = false;
                        break;

                    case "--history-days":
                        if (i + 1 < Arguments.Length && Double.TryParse(Arguments[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var parsedDays) && parsedDays >= 0)
                        {
                            historyDays = parsedDays;
                            i++;
                        }
                        else
                        {
                            Console.Error.WriteLine("Missing or invalid number of days after --history-days!");
                            return 2;
                        }
                        break;

                    case "-j":
                    case "--jid":
                        if (!TryTakeValue(Arguments, ref i, out jid))
                        {
                            Console.Error.WriteLine("Missing JID after --jid!");
                            return 2;
                        }
                        accountGiven = true;
                        break;

                    case "-p":
                    case "--password":
                        if (!TryTakeValue(Arguments, ref i, out password))
                        {
                            Console.Error.WriteLine("Missing password after --password!");
                            return 2;
                        }
                        accountGiven = true;
                        break;

                    case "-w":
                    case "--ws":
                    case "--websocket":
                        if (!TryTakeValue(Arguments, ref i, out webSocketURI))
                        {
                            Console.Error.WriteLine("Missing WebSocket URI after --ws!");
                            return 2;
                        }
                        accountGiven = true;
                        break;

                    case "--insecure":
                        insecure = true;
                        break;

                    case "--sasl":
                        if (!TryTakeValue(Arguments, ref i, out sasl))
                        {
                            Console.Error.WriteLine("Missing SASL mechanism after --sasl!");
                            return 2;
                        }
                        break;

                    case "--trust-announcement":
                        trustAnnouncement = true;
                        break;

                    case "-v":
                    case "--verbose":
                        verbose = true;
                        break;

                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;

                    default:
                        Console.Error.WriteLine($"Unknown argument '{Arguments[i]}'!");
                        PrintUsage();
                        return 2;

                }
            }

            #endregion

            var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

            #region Frontend content source

            IStaticContentSource content;

            if (devDirectory is not null)
                content = new FileSystemContentSource(devDirectory);

            else
            {

                var embedded = new EmbeddedContentSource(HTTPRoot, typeof(Program).Assembly);

                if (embedded.Count == 0)
                {
                    Console.Error.WriteLine($"No frontend bundle is embedded (no manifest resources '{HTTPRoot}*').");
                    Console.Error.WriteLine("Build the project without -p:SkipFrontendBuild=true, or run with --dev <dist directory>.");
                    return 1;
                }

                content = embedded;

            }

            #endregion

            #region TLS: which certificate, and therefore which port

            // Whether the server speaks TLS follows from the arguments alone.
            // Loading the certificate waits until there is a logger to say what
            // happened - and, later on, to say when it was replaced.

            if (certificateFile is not null && certificatePEM is not null)
            {
                Console.Error.WriteLine("Use either --cert (PKCS#12) or --cert-pem (PEM), not both!");
                return 2;
            }

            if (keyPEM is not null && certificatePEM is null)
            {
                Console.Error.WriteLine("--key-pem names the key belonging to --cert-pem, which is missing!");
                return 2;
            }

            if (selfSigned && (certificateFile is not null || certificatePEM is not null))
            {
                Console.Error.WriteLine("--https makes a certificate up; it cannot be combined with one from a file!");
                return 2;
            }

            var useTLS  = selfSigned || certificateFile is not null || certificatePEM is not null;
            var scheme  = useTLS ? "https" : "http";

            port      ??= IPPort.Parse(useTLS ? 8443 : 8080);

            #endregion

            #region The account: the command line for one run, else the file

            // Filled by DefaultPath when something is still in the old place;
            // printed with the rest of the header further down.
            var movedPaths  = new List<String>();

            var accountFile = new AccountFile(accountFilePath ?? PrivatePaths.For(AccountFile.DefaultFileName,      RepositoryRoot(), PrivatePaths.Directory(), movedPaths));

            AccountSettings?  startupSettings  = null;
            var               startupSource    = AccountSource.None;

            if (accountGiven)
            {

                // The command line names an account: it wins for this run and
                // is not written to the file. jid and password are required,
                // the endpoint is optional.
                if (!AccountSettings.TryCreate(jid,
                                               password,
                                               webSocketURI,
                                               sasl,
                                               insecure,
                                               trustAnnouncement,
                                               out startupSettings,
                                               out var argError))
                {
                    Console.Error.WriteLine($"Error: {argError}");
                    return 2;
                }

                startupSource = AccountSource.Arguments;

            }

            else if (accountFile.TryLoad(out startupSettings, out var fileError))
                startupSource = AccountSource.File;

            else if (fileError is not null)
            {
                // A file that exists but cannot be read is worth a word; a file
                // that simply is not there is the normal first start.
                Console.Error.WriteLine($"Warning: {fileError}");
                Console.Error.WriteLine("Starting without an account - set one up on the account page.");
            }

            #endregion

            #region Logging

            using var loggerFactory = LoggerFactory.Create(builder => {

                builder.AddSimpleConsole(options => {
                    options.SingleLine       = true;
                    options.TimestampFormat  = "HH:mm:ss ";
                });

                builder.SetMinimumLevel(verbose ? LogLevel.Trace : LogLevel.Information);

            });

            #endregion

            #region The TLS certificate itself, re-read while the server runs

            RotatingCertificate? certificate = null;

            try
            {

                if (certificatePEM is not null)
                    certificate = RotatingCertificate.FromPEM(certificatePEM, keyPEM, LoggerFactory: loggerFactory);

                else if (certificateFile is not null)
                    certificate = RotatingCertificate.FromPKCS12(
                                      certificateFile,
                                      Environment.GetEnvironmentVariable(DevCertificate.PasswordVariable),
                                      LoggerFactory: loggerFactory
                                  );

                else if (selfSigned)
                    certificate = RotatingCertificate.Fixed(DevCertificate.CreateSelfSigned());

            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Could not load the TLS certificate: {e.Message}");
                return 1;
            }

            #endregion

            #region The archive: where the conversations are kept

            ChatArchive? archive = null;

            if (keepArchive)
            {
                try
                {
                    archive = new ChatArchive(
                                  archiveDirectory ?? PrivatePaths.For(ChatArchive.DefaultDirectoryName, RepositoryRoot(), PrivatePaths.Directory(), movedPaths),
                                  keepMedia,
                                  loggerFactory
                              );
                }
                catch (Exception e)
                {
                    // A web app that cannot write its archive would go on
                    // working and quietly keep nothing, which is the one thing
                    // nobody would notice until they went looking for a message
                    // from last month.
                    Console.Error.WriteLine($"The chat archive could not be opened: {e.Message}");
                    Console.Error.WriteLine("Give another directory with --archive, or run with --no-archive.");
                    return 1;
                }
            }

            #endregion

            #region Passkeys: one origin, or none

            // A passkey is bound to one name and one origin, and both have to be
            // decided here rather than per request - the browser will not offer
            // one otherwise.
            //
            // Three things have to hold, and each of them is a real limit
            // somebody will meet:
            //
            //  * The relying party id has to be a domain. "127.0.0.1" is not
            //    one, whatever a browser thinks of it otherwise, so the default
            //    names localhost and a passkey only appears for whoever opens
            //    the page as http://localhost:port/ - not as 127.0.0.1.
            //  * The origin has to be a secure context: https anywhere, or http
            //    to localhost. A LAN address over plain http offers no
            //    navigator.credentials at all.
            //  * With --any this process does not know which name a browser
            //    will arrive under, so it has to be told: --origin.
            var passkeyOrigin  = originArgument
                                     ?? (anyAddress
                                             ? null
                                             : $"{(useTLS ? "https" : "http")}://localhost:{port}");

            WebAuthnSettings?  webAuthn         = null;
            String?            passkeyProblem   = null;

            if (passkeyOrigin is null)
                passkeyProblem = "--any does not say which name a browser will use; pass --origin <url> to offer passkeys";

            else if (!Uri.TryCreate(passkeyOrigin, UriKind.Absolute, out var originURL))
                passkeyProblem = $"'{passkeyOrigin}' is not a URL";

            else if (originURL.Scheme != "https" &&
                     !(originURL.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                       originURL.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
                passkeyProblem = $"'{passkeyOrigin}' is not a secure context: a browser offers passkeys over https, or over http only to localhost";

            else
                webAuthn = new WebAuthnSettings(
                               RpId:     originURL.Host,
                               RpName:   "XMPPWebApp",
                               Origins:  [ originURL.GetLeftPart(UriPartial.Authority) ]
                           );

            #endregion

            #region Start the HTTP server

            var httpServer  = await HTTPServer.StartNew(
                                  anyAddress
                                      ? IPvXAddress.Any
                                      : IPv4Address.Parse("127.0.0.1"),
                                  port,
                                  HTTPServerName:             "Vanaheimr XMPPWebApp",
                                  // Asked per accepted connection, which is what
                                  // lets a renewed certificate take over without
                                  // a restart. The chain selector below says the
                                  // same and names the intermediates besides;
                                  // this one enables TLS and answers whatever
                                  // still asks the narrower question.
                                  ServerCertificateSelector:  certificate is not null
                                                                  ? (tcpServer, tcpClient) => certificate.Current
                                                                  : null
                              );

            // The whole chain, so that a client which does not already know the
            // intermediates can build one: a fullchain.pem is called that
            // because of what follows the first certificate in it. Both
            // selectors read the same object, so they cannot disagree about
            // which certificate this connection uses.
            if (certificate is not null)
                httpServer.ServerCertificateChainSelector = (tcpServer, tcpClient) => certificate.CurrentChain;

            // 1) The JSON API at /api (registers itself within the HTTP server).
            //    It builds the XMPP client from the account, and the account
            //    page can replace it at runtime.
            var api = new XMPPWebAPI(
                          httpServer,
                          accountFile,
                          startupSettings,
                          startupSource,
                          Version:        version,
                          Archive:        archive,
                          HistoryWindow:  TimeSpan.FromDays(historyDays),
                          DataDirectory:     PrivatePaths.Directory(),
                          SecureCookies:     useTLS,
                          WebAuthnSettings:  webAuthn,
                          LoggerFactory:  loggerFactory
                      );

            // The account database is an append-only log of what was ever done
            // to it - users, passwords, sessions, passkeys - and it has to be
            // replayed before the first request is answered.
            await api.LoadDatabase();

            String? generatedPassword = null;

            if (!api.Users.Any())
            {

                // A first start: nobody can sign in to a page whose login is not
                // set yet, and an unauthenticated setup page would be a door of
                // its own. So the password is made up here and shown once, on
                // the console, to whoever started the process.
                //
                // 24 characters of Base64Url: long enough that nobody guesses
                // it, short enough that somebody can type it off a console.
                generatedPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).
                                            Replace('+', '-').
                                            Replace('/', '_').
                                            TrimEnd('=');

                // An e-mail address is asked for and this program has no use for
                // one: there is nothing here that writes mail, and
                // DisableNotifications means nothing ever will. So it names the
                // machine, and the account is marked authenticated because no
                // confirmation can arrive for an address nobody reads.
                var created = await api.CreateUserIfNotExists(
                                        User_Id.Parse(DefaultUsername),
                                        I18NString.Create(DefaultUsername),
                                        SimpleEMailAddress.Parse($"{DefaultUsername}@localhost"),
                                        Password:                  generatedPassword,
                                        IsAuthenticated:           true,
                                        SkipNewUserEMail:          true,
                                        SkipNewUserNotifications:  true,
                                        SkipDefaultNotifications:  true
                                    );

                if (created is null)
                {
                    Console.Error.WriteLine($"The first account could not be created in '{PrivatePaths.Directory()}'.");
                    return 1;
                }

            }

            // What was said before this start, back into the chat store: the
            // first browser to look then sees a conversation and not a blank
            // page, and everything older is a scroll away.
            var restored = api.LoadHistory();

            // 2) Development helpers at /dev/: live reload when the bundle on disk changes.
            var dev = devDirectory is not null
                          ? new DevAPI(httpServer, devDirectory)
                          : null;

            // 3) The web frontend at /: bundle files, and the SPA stub for every other page URL.
            var web = httpServer.AddHTTPAPI();

            web.MapSinglePageApplication(
                content,
                new SinglePageAppOptions {

                    IndexTransform   = html => html.Replace("{{ServerVersion}}",  $"v{version}",                      StringComparison.Ordinal).
                                                    Replace("{{DevMode}}",        dev is not null ? "true" : "false",  StringComparison.Ordinal),

                    SecurityHeaders  = SecurityHeaderOptions.Default with {

                                           // Pictures may come from anywhere on the web - that is
                                           // the one thing a chat shows that is not its own -
                                           // but only over TLS, and nothing else is opened up:
                                           // no foreign scripts, styles, frames or connections.
                                           ContentSecurityPolicy    = "default-src 'self'; script-src 'self'; style-src 'self'; " +
                                                                      "img-src 'self' data: https:; font-src 'self'; connect-src 'self'; " +
                                                                      "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'",

                                           // HSTS only makes sense (and is only honoured) over TLS.
                                           StrictTransportSecurity  = useTLS
                                                                          ? SecurityHeaderOptions.DefaultStrictTransportSecurity
                                                                          : null

                                       }

                }
            );

            #endregion

            var origin = $"{scheme}://127.0.0.1:{port}";

            Console.WriteLine($"Vanaheimr XMPPWebApp v{version}");
            Console.WriteLine($"  listening on   {scheme}://{(anyAddress ? "0.0.0.0" : "127.0.0.1")}:{port}/");
            Console.WriteLine($"  frontend from  {content.Description}");
            Console.WriteLine($"  JSON API at    {origin}{api.RootPath.ToString().TrimEnd('/')}/v1/status");
            Console.WriteLine($"  events         {origin}{api.RootPath.ToString().TrimEnd('/')}/v1/events");
            Console.WriteLine($"  accounts       {api.Users.Count()} in {PrivatePaths.Directory()}, sign-in at {origin}{api.RootPath.ToString().TrimEnd('/')}/auth/login");
            Console.WriteLine(webAuthn is not null
                                  ? $"  passkeys       on for {webAuthn.Origins[0]} (relying party '{webAuthn.RpId}')"
                                  : $"  passkeys       off: {passkeyProblem}");
            Console.WriteLine($"  account file   {accountFile.Path}{(accountFile.Exists ? "" : " (not there yet)")}");

            if (archive is not null)
                Console.WriteLine($"  chat archive   {archive.Root}, one directory per conversation, one file per month" +
                                  $"{(archive.KeepsMedia ? ", shared files kept" : ", shared files not fetched")}" +
                                  $"{(restored > 0 ? $", {restored} message(s) loaded" : "")}");
            else
                Console.WriteLine("  chat archive   none (--no-archive): nothing said here survives this process");

            if (api.Settings is not null)
                Console.WriteLine($"  XMPP account   {api.Settings} ({(api.Source == AccountSource.Arguments ? "from the command line, not saved" : "from the account file")})");
            else
                Console.WriteLine($"  XMPP account   none yet - open {origin}/ and set one up on the account page");

            if (dev is not null)
                Console.WriteLine($"  live reload    {origin}{dev.RootPath.ToString().TrimEnd('/')}/reload, watching '{dev.WatchedDirectory}'");

            if (certificate is not null)
            {

                Console.WriteLine($"  TLS            {certificate.Current.Subject}, valid until {certificate.Current.NotAfter.ToUniversalTime():u}, " +
                                  $"SHA-256 {certificate.Current.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}" +
                                  (selfSigned ? " (self-signed, browsers will warn)" : ""));

                Console.WriteLine(certificate.Rotates
                                      ? $"                 re-read from {String.Join(" and ", certificate.Files)} every " +
                                        $"{certificate.CheckInterval.TotalSeconds:0}s, so a renewal needs no restart"
                                      : "                 made up at this start, nothing to re-read");

                var intermediates = certificate.CurrentChain.Intermediates;

                Console.WriteLine(intermediates.Count > 0
                                      ? $"                 sending {intermediates.Count} intermediate(s): " +
                                        String.Join(", ", intermediates.OfType<System.Security.Cryptography.X509Certificates.X509Certificate2>().Select(intermediate => intermediate.Subject))
                                      : "                 no intermediates - clients that do not know the issuer cannot build a chain");

            }

            foreach (var moved in movedPaths)
            {
                Console.WriteLine();
                Console.WriteLine($"  ! {moved}");
            }

            if (generatedPassword is not null)
            {
                Console.WriteLine();
                Console.WriteLine("  ┌─ First start: there was no web login, so one was made up for you ─────────");
                Console.WriteLine($"  │  user      {DefaultUsername}");
                Console.WriteLine($"  │  password  {generatedPassword}");
                Console.WriteLine("  │  It is shown here once and kept only as a hash. Sign in with it and");
                Console.WriteLine("  │  change it on the settings page.");
                Console.WriteLine("  └───────────────────────────────────────────────────────────────────────────");
                Console.WriteLine();
            }

            Console.WriteLine("Press Ctrl+C to stop.");

            #region Connect, then wait for Ctrl+C

            using var shutdown = new CancellationTokenSource();
            var       stopped  = new TaskCompletionSource();

            Console.CancelKeyPress += (_, e) => {
                e.Cancel = true;
                shutdown.Cancel();
                stopped.TrySetResult();
            };

            // After the web server is up, so that the page already answers -
            // with the reason, or the account page - while the XMPP server is
            // still being asked. Without an account this does nothing.
            _ = api.ConnectAsync(shutdown.Token);

            await stopped.Task;

            Console.WriteLine("Shutting down ...");
            dev?.Dispose();

            // Before the client: a message that arrived a moment ago is queued
            // here and belongs in the archive as much as any other.
            if (archive is not null)
                await archive.DisposeAsync();

            if (api.Client is not null)
            {
                try
                {
                    await api.Client.DisposeAsync();
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"The XMPP client did not close cleanly: {e.Message}");
                }
            }

            await httpServer.Stop();

            #endregion

            return 0;

        }


        #region (private) TryTakeValue(Arguments, ref Index, out Value)

        private static Boolean TryTakeValue(String[]      Arguments,
                                            ref Int32     Index,
                                            out String?   Value)
        {

            if (Index + 1 < Arguments.Length && !Arguments[Index + 1].StartsWith("--"))
            {
                Value = Arguments[++Index];
                return true;
            }

            Value = null;
            return false;

        }

        #endregion

        #region (private) RepositoryRoot()

        /// <summary>
        /// The directory containing XMPPWebApp.slnx, looked up from the binary
        /// and from the current directory; the current directory when neither
        /// leads to it. The account file and the --dev directory default to
        /// places below it.
        /// </summary>
        private static String RepositoryRoot()
        {

            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {

                var directory = new DirectoryInfo(start);

                while (directory is not null)
                {

                    if (File.Exists(Path.Combine(directory.FullName, "XMPPWebApp.slnx")))
                        return directory.FullName;

                    directory = directory.Parent;

                }

            }

            return Environment.CurrentDirectory;

        }

        #endregion

        #region (private) DefaultDevDirectory()

        /// <summary>
        /// The webpack output directory for --dev without an explicit path:
        /// src/Frontend/dist below the repository root.
        /// </summary>
        private static String DefaultDevDirectory()
            => Path.Combine(RepositoryRoot(), "src", "Frontend", "dist");

        #endregion

        #region (private) PrintUsage()

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: XMPPWebApp [--port <number>] [--any] [--dev [<dist directory>]]");
            Console.WriteLine("                  [--https | --cert <file.pfx> | --cert-pem <file> [--key-pem <file>]]");
            Console.WriteLine("                  [--archive <dir> | --no-archive] [--no-media] [--history-days <n>]");
            Console.WriteLine("                  [--account <file>] [--jid <jid> --password <pw> [--ws <uri>]] [--insecure]");
            Console.WriteLine("                  [--sasl <mechanism>] [--trust-announcement] [--verbose]");
            Console.WriteLine();
            Console.WriteLine("Web server:");
            Console.WriteLine("  --port <number>   TCP port to listen on (default: 8080, or 8443 with TLS)");
            Console.WriteLine("  --any             listen on all addresses instead of 127.0.0.1");
            Console.WriteLine("  --dev [<dir>]     serve the frontend from the webpack output directory on disk");
            Console.WriteLine("                    (default: src/Frontend/dist) instead of the embedded bundle,");
            Console.WriteLine("                    and reload the page in the browser whenever it changes");
            Console.WriteLine("  --https           serve HTTPS with a self-signed certificate for localhost");
            Console.WriteLine($"  --cert <file>     serve HTTPS with the given PKCS#12 file; the password is read from");
            Console.WriteLine($"                    the environment variable {DevCertificate.PasswordVariable}");
            Console.WriteLine("  --cert-pem <file> serve HTTPS with a PEM certificate, e.g. Let's Encrypt's fullchain.pem");
            Console.WriteLine("  --key-pem <file>  its private key, e.g. privkey.pem; without it the key is expected");
            Console.WriteLine("                    in the certificate file itself");
            Console.WriteLine($"                    A certificate from a file is re-read when it changes on disk (at most");
            Console.WriteLine($"                    every {RotatingCertificate.DefaultCheckInterval.TotalSeconds:0}s), so a renewal takes effect without a restart.");
            Console.WriteLine();
            Console.WriteLine("Chat archive:");
            Console.WriteLine($"  --archive <dir>   where the conversations are kept (default: {ChatArchive.DefaultDirectoryName}/ below");
            Console.WriteLine($"                    {PrivatePaths.Directory()}): one directory per account, one per conversation,");
            Console.WriteLine("                    one file per month, and the shared files beside them in media/");
            Console.WriteLine("  --no-archive      keep nothing; what is said is gone when the process is");
            Console.WriteLine("  --no-media        write the conversations, but do not fetch the files shared in them");
            Console.WriteLine($"  --history-days <n>  how much of the archive is loaded at a start (default: {ChatArchive.DefaultHistoryWindow.TotalDays:0});");
            Console.WriteLine("                      older messages are loaded when the page is scrolled up to them");
            Console.WriteLine();
            Console.WriteLine("Passkeys:");
            Console.WriteLine("  --origin <url>    the address browsers reach this at, e.g. https://chat.example.org");
            Console.WriteLine("                    A passkey belongs to one name, so it has to be named. Without this,");
            Console.WriteLine("                    http://localhost:<port> is assumed - which is why a passkey appears");
            Console.WriteLine("                    when the page is opened as localhost and not as 127.0.0.1, and why");
            Console.WriteLine("                    --any needs this option to offer one at all.");
            Console.WriteLine();
            Console.WriteLine("Accounts:");
            Console.WriteLine($"  The accounts live in {PrivatePaths.Directory()}, in the database of");
            Console.WriteLine($"  Hermod's HTTPExtAPI. A first start makes one - user '{DefaultUsername}' - and shows its");
            Console.WriteLine("  password once. Change it on the settings page; sign-up is not offered.");
            Console.WriteLine();
            Console.WriteLine("XMPP account:");
            Console.WriteLine($"  --account <file>  where the account settings live (default: {AccountFile.DefaultFileName} below");
            Console.WriteLine($"                    {PrivatePaths.Directory()}); the account page reads and writes it");
            Console.WriteLine("  -j, --jid <jid>         an account for this run only, e.g. user@example.org (not saved to the file)");
            Console.WriteLine("  -p, --password <pw>     its password (visible in the process list - the account file is not)");
            Console.WriteLine("  -w, --ws <uri>          the WebSocket endpoint, e.g. wss://xmpp.example.org:5281/xmpp-websocket;");
            Console.WriteLine("                          without one the host-meta of the domain is asked (XEP-0156)");
            Console.WriteLine("      --insecure          allow a ws:// endpoint, for a server on the same machine");
            Console.WriteLine("      --sasl <mechanism>  the weakest SASL mechanism still accepted (default: SCRAM-SHA-256)");
            Console.WriteLine("      --trust-announcement");
            Console.WriteLine("                          carry on when the server signs a different mechanism list than the");
            Console.WriteLine("                          one that arrived (XEP-0474) - see XMPPConsole for when that is right");
            Console.WriteLine("  -v, --verbose           log every stanza");
            Console.WriteLine();
            Console.WriteLine("Without an account the site opens on the account page; set one up there and it is saved");
            Console.WriteLine("to the account file, so the next start goes straight to the chat.");
        }

        #endregion

    }

}
