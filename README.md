# XMPPWebApp

[![CI](https://github.com/Vanaheimr/XMPPWebApp/actions/workflows/ci.yml/badge.svg)](https://github.com/Vanaheimr/XMPPWebApp/actions/workflows/ci.yml)
[![Nightly](https://github.com/Vanaheimr/XMPPWebApp/actions/workflows/nightly.yml/badge.svg)](https://github.com/Vanaheimr/XMPPWebApp/actions/workflows/nightly.yml)

An XMPP client as a web page: the same thing
[XMPPConsole](https://github.com/Vanaheimr/XMPPConsole) is for the command
line, only that you open it in a browser. The web app signs in to **one XMPP
account** over WebSocket (RFC 7395), keeps the conversations of that account —
on screen and on disk — and shows them to whoever signs in to the page: the
list of chats on the left, the open conversation on the right, a box to type
in at the bottom.

The protocol does not live here. It lives in
**[Ratatoskr](https://github.com/Vanaheimr/Ratatoskr)**, the HTTP server in
**[Hermod](https://github.com/Vanaheimr/Hermod)**, both pinned submodules
under `libs/`. What is in this repository is the front end: a C# process that
holds the XMPP connection and answers a small JSON API, and a TypeScript page
that talks to it.

> **Maturity: beta.** What a stranger can reach is tested from outside, against
> a running server on a real port: that a password on file does not follow a
> changed endpoint, that an event stream is asked again for every event it
> carries, that a sign-in is rationed before it is verified rather than after,
> and that a file name never becomes a path. The suite runs on every push, on
> Windows and on Debian 13, because two things here answer differently per
> platform — a certificate chain and the rules a file name has to obey.
>
> What is open on purpose, and why this is not *stable*: everything said is
> kept in the clear. Owner-only (0600, in the per-user data directory) and
> nobody else's business, but in the clear — that is a decision, not an
> oversight, and it means the machine this runs on is the trust boundary. There
> is no OMEMO in the page and no MAM, and one account with one login is the
> whole model. A web client for the person who runs it; not a service for
> other people.

---

## Contents

- [What it does](#what-it-does)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Command line options](#command-line-options)
- [How it works](#how-it-works)
- [Security notes](#security-notes)
- [Development](#development)
- [Tests](#tests)
- [Repository layout](#repository-layout)
- [License](#license)

---

## What it does

- **Set up on a page.** A fresh start has no account and opens on an account
  page: JID, password, optionally the WebSocket endpoint, and a few knobs
  (minimum SASL mechanism, allow `ws://`, trust a changed announcement). Save,
  and the web app writes the settings to a git-ignored file, connects with
  SCRAM-SHA-256, and reconnects after a break. The next start reads the file
  and goes straight to the chat; a gear in the corner returns to the page to
  change the account or reconnect.
- **One login for the page, configurable too.** Whoever opens the site sees a
  username and password form. A correct pair gets an HttpOnly session cookie,
  and with it the settings page and the chats. The accounts are Hermod's
  `HTTPExtAPI`: its database, its sign-in, its password rules — a PBKDF2 hash
  and never a password — and its passkey support waiting behind one setting.
  This program makes exactly one account on its first start, prints the password
  once on the console, and offers no sign-up. The password is changed on the
  settings page, which ends every other session.
- **A Jabber client's screen.** Contacts and conversations on the left, sorted
  by their last activity, with presence, status text, an unread badge and a
  typing indicator. The open conversation on the right, with day separators,
  delivery and read confirmations (XEP-0184, XEP-0333), corrections applied in
  place (XEP-0308), messages from your other devices (XEP-0280), and late
  deliveries where they were written (XEP-0203). Enter sends, Shift+Enter
  breaks the line; the far end sees when you type (XEP-0085).
- **Pictures inline.** A message that is nothing but one `https://` URL ending
  in `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.avif` or `.bmp` is shown as
  the picture it points to, linked to the original. That is how a client hands
  over an upload (XEP-0363). A link inside a sentence stays a link.
- **Everything that was said, kept.** Every message goes into an archive on
  disk: one directory per account, one per conversation, one file per month,
  one line of JSON per message — the very line the browser was shown. A
  correction, a delivery receipt or a fetched file is appended rather than
  written over, and the last line for a message wins when it is read back.
  The last month comes back at every start, so a browser opens on a
  conversation and not on a blank page; older months are read when the page is
  scrolled up to them, a page at a time.
- **The shared files beside them.** A message that hands over a file — one
  `https://` URL and nothing else, or an `aesgcm://` URL (XEP-0454), which the
  browser cannot open at all — has the file fetched into `media/` beside the
  conversation, named for the second it arrived: `20260913T071848Z_photo.jpg`.
  The page then shows that copy instead of the original: it is still there
  when the upload has expired, an encrypted one is decrypted on the way in,
  and no outside host learns who is reading. What is fetched, and what it may
  be served back as, is narrow on purpose — see [Security notes](#security-notes).
- **Contacts.** Start a chat with any JID, add somebody as a contact, accept or
  deny a contact request, remove a contact.
- **Unicode.** Every script and every emoji, in both directions. What a
  browser can type and XML 1.0 cannot carry — a NUL, a form feed — is dropped
  before it can end the stream.
- **Live.** One Server-Sent Events stream per browser carries every message,
  presence change, typing state and connection change as it happens; the page
  never polls.

Not implemented, as in the console: MUC/MIX group chat, MAM history (the
archive here is this web app's own, not the server's), HTTP file upload, OMEMO
in the page, avatars. The full picture of what the library
speaks is in the
[README of XMPPConsole](https://github.com/Vanaheimr/XMPPConsole#what-it-speaks-today).

## Requirements

- **.NET SDK 10.0** or newer
- **Node.js 22** or newer with npm, for the frontend bundle
- An XMPP account on a server that offers **XMPP over WebSocket** (RFC 7395)

## Getting started

Ratatoskr, Hermod and Styx are **submodules** under `libs/`, so one clone is
the whole build:

```bash
git clone --recurse-submodules https://github.com/Vanaheimr/XMPPWebApp.git
```

If you already cloned without them:

```bash
git submodule update --init
```

Nothing has to be edited before the first run: the account of the page is made
at the first start and the XMPP account is set up in the browser. Build and run — the first
`dotnet build` runs `npm ci` and `npm run build` for you and embeds the bundle
into the assembly; the frontend is rebuilt whenever one of its inputs
changed.

```bash
dotnet run --project src\XMPPWebApp\XMPPWebApp.csproj
```

The first start finds no web login and makes one up, printing it once:

```
  ┌─ First start: there was no web login, so one was made up for you ─────────
  │  user      admin
  │  password  lYWeZdLnllssGvXZKorb14zJ
```

Open <http://127.0.0.1:8080/>, sign in with that, and — on a fresh start — the
settings page opens. Change the password there to something you can remember;
below it the XMPP account is waiting. Enter the JID, password and, optionally,
the WebSocket endpoint, save, and the chats appear. The XMPP account is written
to `xmpp-account.json` and the accounts of the page to their own database, both
in the per-user application data
directory — `%LOCALAPPDATA%\XMPPWebApp` on Windows, `~/.local/share/XMPPWebApp`
elsewhere — so the next start goes straight to the chat. They used to live below
the repository root; a checkout that still has them there keeps working and says
so on the console once, because a program may point out where its files belong
but should not move somebody's files for them.

To point the web app at an account without the page — for one run, without
writing the file — pass it on the command line:

```bash
dotnet run --project src\XMPPWebApp\XMPPWebApp.csproj -- --jid user@example.org --password secret --ws wss://xmpp.example.org:5281/xmpp-websocket
```

## Command line options

| Option | Meaning |
|---|---|
| `--port <number>` | TCP port of the web server, default 8080 (8443 with TLS) |
| `--any` | listen on all addresses instead of 127.0.0.1 |
| `--https` | serve HTTPS with a self-signed certificate for localhost, created at start (browsers warn about it) |
| `--cert <file.pfx>` | serve HTTPS with the given PKCS#12 file; its password is read from the environment variable `XMPPWEBAPP_CERT_PASSWORD` |
| `--cert-pem <file>` | serve HTTPS with a PEM certificate, e.g. Let's Encrypt's `fullchain.pem` |
| `--key-pem <file>` | its private key, e.g. `privkey.pem`; without it the key is expected in the certificate file itself |
| `--dev [<dir>]` | serve the frontend from the webpack output directory on disk (default `src/Frontend/dist`) and reload the page whenever it changes, see [Development](#development) |
| `--account <file>` | where the account settings live (default: `xmpp-account.json` in the application data directory); the settings page reads and writes this file |
| `--archive <dir>` | where the conversations are kept (default: `chats/` there as well) |
| `--no-archive` | keep nothing: what is said is gone when the process is |
| `--no-media` | write the conversations, but do not fetch the files shared in them |
| `--history-days <n>` | how much of the archive is loaded at a start, default 31; older messages are loaded when the page is scrolled up to them |
| `-j`, `--jid <jid>` | an XMPP account for this run only, not written to the file |
| `-p`, `--password <pw>` | its password (visible in the process list — the file is not) |
| `-w`, `--ws <uri>` | the WebSocket endpoint; without one the host-meta of the domain is asked (XEP-0156), then `wss://<domain>:5443/ws` |
| `--insecure` | allow a `ws://` endpoint, for a server on the same machine |
| `--sasl <mechanism>` | the weakest SASL mechanism still accepted, default `SCRAM-SHA-256`; lower it only for a server that cannot: `--sasl SCRAM-SHA-1` |
| `--trust-announcement` | carry on when the server signs a different mechanism list than the one that arrived (XEP-0474); see XMPPConsole for when that is right |
| `-v`, `--verbose` | log every stanza |
| `-h`, `--help` | show the help and exit |

A `ws://` endpoint is refused unless `--insecure` says otherwise, for the
reason the [console's README](https://github.com/Vanaheimr/XMPPConsole#why-ws-is-refused)
gives: over plain WebSocket a man in the middle strips the SASL announcement
down to PLAIN, and PLAIN is the password itself.

## How it works

Three parts, and the browser only ever sees the last one:

| Part | Where | Task |
|---|---|---|
| `XMPPClient` | Ratatoskr | The XMPP connection: WebSocket, SASL, roster, carbons, receipts, stream management |
| `XMPPWebAPI` | `src/XMPPWebApp` | Turns the client's events into a `ChatStore` and the store into JSON and Server-Sent Events; checks the session cookie on every request |
| the page | `src/Frontend` | A TypeScript single-page app that loads snapshots, listens to the stream, and draws |

The **chat store** holds one conversation per far end, in memory: the
messages since the process started, the contact's presence and status, the
unread count, what the far end is doing right now. Every change bumps a
sequence number and is published to every browser on the stream. A browser
that loads a snapshot remembers its sequence and ignores older events — which
is what lets Hermod replay its cached events to a reconnecting browser without
anything appearing twice.

The **archive** is the other half of the store: everything that goes through
the store is written to disk, and at the next start the last month comes back
out of it.

```
chats/
  me@example.org/                             one directory per account
    alice@example.org/                        one per conversation
      alice@example.org_2026-09.jsonl         one per month, one line per message
      alice@example.org_2026-10.jsonl
      media/
        20260913T071848Z_photo.jpg            what arrived, when it arrived
```

A line is the message as the browser got it — the same JSON, one object per
line, so a message containing line breaks is still one line and a year-old
conversation needs nothing but a JSON parser to read. A line is appended and
never changed: a correction, a receipt or a fetched file appends another line
for the same message, in the file of the month the message was *written* in,
and reading applies them in order, so the last line for a message wins. The
directory name is the JID with everything that is not a letter, a digit or
`@.-_` replaced by `_` — a JID is a stranger's text and a resource may contain
`..` or `\`.

Writing happens on a task of its own, fed by a queue: the store raises its
events while holding its lock, and nothing there may touch a disk. A full
disk, a directory gone missing or a file locked by a backup therefore costs
the line and nothing else; it is said once in the log and then only counted.

Loading has two halves. At a start, and after an account change, the last
`--history-days` of every archived conversation go into the store — a
conversation that has been quiet for longer contributes its last 20 messages
so that it is still in the list at all. Nothing loaded counts as unread.
Everything older is read when the page asks for it:
`GET /chats/{jid}/messages?before=<timestamp>` reads backwards through the
month files, hands over a page and says whether there is more; the browser
prepends it and puts the scroll position back where it was, so that the line
somebody was reading stays under their eyes.

The accounts are **`HTTPExtAPI`'s**, below `/api` — not below `/v1`, because the
version belongs to this application's own API and not to Hermod's:

```
POST /auth/login            {"login","password"}        the session cookie, or 401
POST /auth/logout                                       ends the session
GET  /auth/me                                           who is signed in
PUT  /auth/me               {"displayName"}             rename yourself; the username does not change
POST /auth/password         {"currentPassword",…}       change it; ends every other session
```

The **JSON API** of this application, below `/api/v1`:

```
GET  /status                                            the XMPP connection, contacts, chats, unread
POST /connection/reconnect                              connect again after the client gave up
GET  /account                                           the account without the password, and the connection
PUT  /account               {"jid","password",…}        save the account, (re)connect; empty password keeps the stored one
DELETE /account                                         forget the account, delete the file, disconnect
GET  /chats                                             every conversation, most recent first
POST /chats                 {"jid"}                     start a conversation
GET  /chats/{jid}/messages                              the conversation; reading it marks it read
GET  /chats/{jid}/messages?before=<ts>&limit=<n>        older messages, straight from the archive
POST /chats/{jid}/messages  {"body"}                    send
GET  /chats/{jid}/media/{name}                          a file that was shared here and fetched
POST /chats/{jid}/read                                  mark as read
POST /chats/{jid}/state     {"state"}                   composing, paused, active, inactive, gone
POST /chats/{jid}/contact   {"action"}                  add, accept, deny, remove
GET  /events                                            the Server-Sent Events stream
```

Everything except the sign-in needs the cookie; every state-changing request
is refused when the browser says it came from another site.

## Security notes

- **The account file holds a working password.** `xmpp-account.json`, written
  by the account page, keeps the XMPP password in plain text — the same as the
  old constant, only in a file that is git-ignored rather than in the source.
  On Unix it is created readable by its owner alone (0600); on Windows the
  directory decides, which is why the directory moved — see the next point.
  `--password` on the command line is no better than the file and worse than the
  page: it lands in the process list.
- **What is private is kept where the system keeps private things.**
  `%LOCALAPPDATA%\XMPPWebApp` on Windows, `~/.local/share/XMPPWebApp` elsewhere.
  It used to be the repository root, and a working copy is not a private place:
  on Windows it is often not private at all, because a checkout on a data drive
  inherits that drive's rights. On the machine this was written on that meant
  `Users: ReadAndExecute` and `Authenticated Users: Modify` on a file holding an
  XMPP password in the clear, while the same file under the profile has neither
  entry. An ACL on each file would have been the other way to fix it, and the
  worse one: it covers what this program writes and nothing else, while the
  directory decides for everything that ever lands beside it.
- **The login of the page is the whole of its own security, and it is not this
  program's code.** The accounts are Hermod's `HTTPExtAPI`: a PBKDF2-SHA256 PHC
  string at 600 000 iterations, never a password in the clear, because this one
  only ever has to be recognised; a hash computed even for a login nobody has,
  so that the answer does not say whether an account exists; a random token in
  an `HttpOnly; SameSite=strict` cookie, `secure` when the server speaks TLS.
  Changing the password asks for the current one and ends every other session.

  Two numbers are this program's own, because `HTTPExtAPI` leaves them open and
  its defaults suit a service rather than a page holding somebody's chat
  archive: a session ends after **12 hours** without use and after **7 days**
  at the latest. Left alone it would be thirty days and no idle timeout at
  all.
- **Signing in is rationed, because verifying a password is expensive on
  purpose.** 600 000 rounds of PBKDF2 are what make the stored hash costly to
  attack offline, and the same number is what makes `POST /api/v1/auth/login`
  costly to answer for anybody who can reach the port. The half second above is
  no defence against that: it delays the answer, not the attempt, and requests
  are served concurrently — a thousand may be in flight, each waiting out its
  own half second. So two limits sit in front of the hashing rather than behind
  it. A ration per source (Hermod's token bucket: ten attempts, then one every
  ninety seconds) answers 429 with `Retry-After`, which is what stops guessing.
  A ceiling of two simultaneous verifications answers 503 to whatever does not
  fit, which is what stops ten thousand sources — each with a ration of its own
  — from turning every core of the machine into a password hasher. Two things
  this does not solve, said rather than left to be found: behind a reverse proxy
  every request arrives from the same address and shares one ration, since
  `X-Forwarded-For` is deliberately not read (a header anybody may write is a
  ration anybody may sidestep); and a distributed attack wide enough to fill the
  bucket registry ends in everybody being refused rather than in an unbounded
  table — the right way round for a program that serves one person, but a
  lockout and not a shrug.
- **An open event stream is asked again for every event it carries.** Every
  other route answers one request, and the check it made is exactly as old as
  the answer. The stream at `/api/v1/events` stays open for hours and keeps
  delivering, so a session checked only when it opened would mean that signing
  out, changing the password or running out of time ends the right to ask for
  the conversations while leaving a channel open that keeps handing them over —
  "ends every other session" above would have been true of the next request and
  of nothing else. The check reads the session without touching it, which
  matters more than it sounds: an ordinary lookup slides the idle timeout, so a
  stream that checked itself that way would keep renewing its own session, and
  twelve hours unused would quietly become twelve hours after the browser was
  closed.
- **The stored password only ever goes back where it came from.** The account
  page is never given the password — it learns that one is set, no more — and
  an empty password field on a save keeps the one on file. That convenience
  ends where the destination changes: a save that names another JID or another
  WebSocket endpoint has to carry the password again. Without that rule the
  first half is theatre, because connecting *is* sending: SCRAM proves
  knowledge of the password to whoever answers, and under SASL PLAIN it travels
  verbatim. Whoever held a session could otherwise read a password they were
  never shown — name their own server, save, wait for the login. Both halves of
  "the destination" are checked, and the second is the one that is easy to
  miss: no endpoint means the domain of the JID is asked for one (XEP-0156), so
  moving the JID alone moves the login just as surely. The resource is not part
  of it. Lowering the SASL floor on its own is still allowed: it weakens how the
  password is proved, but only towards the server it was stored for.

- **Nothing that came over the wire is ever put into the page as HTML.**
  Names, status texts and message bodies become text nodes; a body becomes a
  picture or a link only through the two rules in `src/Frontend/src/chat/links.ts`,
  which build DOM nodes and set URLs the URL parser accepted with an `http` or
  `https` scheme. A `javascript:` or `data:` URL cannot pass either rule. On
  top of that the page is served with a Content-Security-Policy that forbids
  inline scripts and foreign scripts, and opens `img-src` for `https:` and
  nothing else.
- **A picture is a request from the reader's browser to whoever sent the
  URL.** The browser sends no referrer along; it does send the request. That
  is the same trade every chat client makes that shows pictures inline, and
  the reason the rule is narrow: only a message that is nothing but the URL.
  Once the file has been fetched into the archive the page shows that copy
  instead, and then no request leaves at all.
- **Fetching a shared file turns a message into a network request, so the
  sender decides what this machine fetches.** The rules are the same as
  XMPPConsole's, and each is there for one attack: `https` only, no address
  that belongs to this machine or to a private network (every address a name
  resolves to, not the first), redirects followed by hand and checked at every
  hop, at most 64 MB, at most two minutes. One gap is named rather than
  papered over: the host is resolved, checked, and then resolved a second time
  by the HTTP client, so a name that answers differently the second time gets
  through (DNS rebinding). Run with `--no-media` where that matters.
- **A stored file comes back out of this program's own origin**, which is a
  security decision and not a convenience: a file the browser treats as a
  document would be script running as this page. So only image, video and
  audio types are stored at all — `image/svg+xml` is *not* among them, an SVG
  being a document with scripts in it — the file is given the extension of the
  type the server announced when its name disagrees, and what is served is
  read off that extension with `nosniff`, never guessed from the content.
  Anything whose extension is not on that list is served as a download.
  Reading a stored file needs the session, like everything else below
  `/api/v1`: the pictures of a conversation are the conversation.
- **What a peer can make grow has a number next to it.** How many messages
  arrive, and how many of them hand over a file, is decided by whoever is
  writing to this account — so every table that grows one entry per such message
  needs a limit, and the disk needs one too. The files of an account may take up
  2 GiB together; past that, fetching stops and nothing else happens. Nothing
  already written is deleted and nothing said is lost — the conversations keep
  being archived and the files stay where they were shared — because an archive
  whose promise is that everything said is kept may not start deleting to make
  room. Alongside that: at most 4096 messages are remembered as already fetched
  for, at most 64 fetches may be outstanding before further links are left
  alone, and three downloads run at once. That last number and the 64 MiB per
  file are together what bounds the memory a download occupies: a file has to be
  whole before it can be written, and an encrypted one has to be whole before
  its tag can even be checked, so the way to make that smaller is a smaller
  number and not a stream.

- **The archive is the most private thing this program writes.** Everything
  that was ever said, in the clear, plus the files. On Unix its directories are
  created 0700 and its files 0600, which they were not at first: the credentials
  were owner-only on a server while the conversations beside them took whatever
  the umask happened to be, and 0644 is a readable conversation. On Windows the
  directory decides, as ever. `--no-archive` keeps nothing.
- **A renewed certificate is picked up without a restart.** A certificate
  given with `--cert` or `--cert-pem` is re-read when its file changes on
  disk - Hermod asks for the certificate per accepted connection, so the last
  write time and the length of the files are compared there, at most once
  every five seconds. That is a comparison and not a `FileSystemWatcher` on
  purpose: a watcher answers "did something happen while I was listening", and
  an event lost to a buffer overflow, a container bind mount or a replaced
  symlink is lost for good - the price being an expired certificate weeks
  later, which nobody notices until a browser does. A load that fails changes
  nothing: a renewal caught halfway through writing a file leaves the
  certificate in force, says so once, and is tried again.
- **The intermediate chain is handed on.** A `fullchain.pem` is read whole:
  the first certificate is the leaf, everything after it becomes
  `additionalCertificates` on the `SslStreamCertificateContext`, which is what
  RFC 8446 4.4.2 asks a server to send. A PKCS#12 holding more than one
  certificate is taken apart the same way, the one with the private key being
  this server's. A self-signed certificate is dropped rather than sent: a root
  on the wire is bytes the client either already has or is not going to trust
  because it arrived.

  Saying that at all needed something new in Hermod, whose
  `ServerCertificateSelectorDelegate` can only hand over a single certificate -
  so `ServerCertificateChainSelector` was added beside it, and that is what
  this program sets. What is verified there is the Hermod half: the selector is
  asked for every accepted connection, it takes precedence over the narrow one,
  and a handshake with intermediates completes.

  What each platform then puts on the wire is deliberately not claimed here. On
  Windows the intermediates did not appear in a local test, .NET apparently
  withholding them when the chain does not validate to a locally trusted root -
  and a client on the same machine cannot settle it either, because Schannel
  builds its own chain: `ChainPolicy.ExtraStore` stays empty and `ChainElements`
  shows what the machine could assemble, not what arrived. So a machine that has
  just minted its own test intermediate finds it again whether or not the server
  sent it. `openssl s_client -showcerts` against the real deployment is the one
  honest check, and worth doing once after the first renewal.
- **Over plain HTTP the cookie travels readable.** Use `--https`, `--cert` or
  `--cert-pem` for anything but a machine you sit at.

## Development

Two processes, one of them webpack:

```bash
npm --prefix src\Frontend run watch
```

```bash
dotnet run --project src\XMPPWebApp\XMPPWebApp.csproj -- --dev
```

With `--dev` the server delivers the frontend from `src/Frontend/dist` on disk
instead of the embedded bundle, watches that directory, and publishes a
`reload` event at `/dev/reload` whenever webpack wrote a new one — the page
reloads itself. A backend rebuild is picked up too: the page reloads when the
stream comes back.

`dotnet build -p:SkipFrontendBuild=true` builds the backend alone and embeds
whatever `dist/` currently holds.

## Tests

Two suites, one per side, and both about the decisions this application makes
for itself rather than about the protocol or the server underneath.

```bash
dotnet test src\XMPPWebApp.Tests\XMPPWebApp.Tests.csproj
```

The C# side: what a conversation keeps and in which order (corrections,
late deliveries, receipts, the unread count, the sequence numbers a browser
relies on), who may use the page and what travels in the cookie, which
characters a browser may type and an XML stream may not carry, which endpoints
the web app will open at all, the account settings — validation, the password
that goes to the file and never to the browser, and the round-trip through the
account file — and the web login: that only the right pair verifies, that the
file keeps a hash and not a password, and that changing the login ends every
session but the one that changed it.

The TLS certificate has one too, because it is the part of the setup that
changes while the process runs: that a replaced file is picked up, that a
half-written or vanished one leaves the certificate in force, that a
file rewritten with the same content is not swapped for itself, that the check
interval keeps the connection path off the disk, and that two hundred threads
asking at once see one certificate and one rotation.

The archive has a suite of its own, because its mistakes are the permanent
ones: that a message lands in the directory of its conversation and the file
of its month, that a message with line breaks is still one line, that a later
line for the same message wins, that a window and a scroll into the past
return what they should and say whether there is more, that an account change
changes the archive, that a half-written line does not cost the rest of the
month — and the two things a path built from a stranger's text must never do.

```bash
npm --prefix src\Frontend test
```

The page's side: which body is a picture and which is a link, that a
`javascript:` URL is neither, that a broken picture falls back to its link,
and — for a file the archive fetched — that what is shown is this web app's
own copy and that the address it came from appears nowhere on the page. Node runs the TypeScript directly; the few DOM calls the rules make are
answered by a stand-in that has no `innerHTML`, so that reaching for it would
fail the test.

And the API as a browser meets it: a real server on a real port, driven over
HTTP. Everything else here tests a decision, and a decision that is right and
never asked is worth nothing — so this is where the four gates are held to
their places. That a stored password does not follow a changed endpoint. That
an event stream stops carrying events the moment its session is signed out —
two browsers, one of which goes on receiving, because silence that nobody
contradicts proves nothing. That the eleventh sign-in attempt is refused faster
than a wrong password is refused, which is how you tell a gate in front of the
work from one behind it. And that a name in a media URL never becomes a path.
Open any of those three gates in the handler and something in that fixture goes
red; that was checked by opening them.

The protocol is not checked here. Its suite lives with
[Ratatoskr](https://github.com/Vanaheimr/Ratatoskr), in the submodule.

Both suites run on every push, on Windows and on Debian 13, against the
submodule revisions this commit pins — that is what the CI badge stands for.
The platforms are two because two things here are platform-dependent: a
certificate chain is built by Schannel on the one and by OpenSSL on the other,
and the rules a file name has to obey are Windows' rules.

The nightly badge stands for something the gate cannot say. A pin does not move
on its own, so no push can ever discover that Hermod or Ratatoskr has changed
underneath this program. Once a night the suite therefore runs a second time
with the submodules moved to the tip of their branches. That run is allowed to
fail without failing the night: it does not mean this commit is broken — it
builds against its pins, which is what anybody cloning gets — it means the next
submodule bump has work in it.

## Repository layout

```
XMPPWebApp.slnx
src/XMPPWebApp/                  the C# process (net10.0)
    Program.cs                   the web login, arguments, HTTP server, account file
    XMPPWebAPI.cs                the JSON API and the event stream
    XMPPWebAPI.XMPP.cs           the account, the client made from it, which XMPP events end up where
    Account/AccountSettings.cs   one account: what the page asks for and the file keeps
    Account/AccountFile.cs       reading and writing the account file
    Account/WebLoginSettings.cs  the web login: one username, one hashed password
    Account/WebLoginFile.cs      reading and writing the web login file
    Account/OwnerOnlyFile.cs     writing a file only its owner may read
    XMPPWebAPI.WebLogin.cs       the routes that change the web login
    WebSessions.cs               the web login and the session cookie
    Chats/ChatStore.cs           the conversations in memory
    Chats/ChatMessage.cs         one line, one conversation, as JSON
    Chats/ChatArchive.cs         the conversations on disk: writing, and reading back
    Chats/ChatArchivePaths.cs    where a conversation goes, and what a JID may become
    Chats/MediaLinks.cs          which link in a body is a shared file
    Chats/MediaStore.cs          fetching one, and refusing most others
    XmlText.cs                   what XML 1.0 will not carry
    EndpointPolicy.cs            wss:// only, unless --insecure
    DevAPI.cs                    /dev/reload for the development loop
    DevCertificate.cs            a self-signed certificate for --https
    RotatingCertificate.cs       the TLS certificate, re-read when it is renewed
src/XMPPWebApp.Tests/            NUnit
src/Frontend/                    npm project (webpack, TypeScript, SCSS)
    src/pages/chat.ts            the chat page
    src/pages/account.ts         the settings page: the XMPP account and the web login
    src/pages/login.ts           the sign-in page
    src/chat/store.ts            the browser's copy of the chats, fed by the stream
    src/chat/links.ts            which body is a picture, which is a link, and the archived file
    src/api/client.ts            the JSON API
    tests/                       node --test
    dist/                        webpack output (generated, git-ignored)
libs/Ratatoskr, libs/Hermod, libs/Styx
                                 git submodules, pinned
```

The dependency chain runs `XMPPWebApp → Ratatoskr → Hermod → Styx`. The
namespace of this application is `org.GraphDefined.Vanaheimr.XMPPWebApp`; the
protocol types come from `org.GraphDefined.Vanaheimr.Ratatoskr`.

## License

Apache License 2.0, like the other [Vanaheimr](https://github.com/Vanaheimr)
projects.
