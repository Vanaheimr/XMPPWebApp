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

using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Ratatoskr;

using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// What was said, kept: one directory per conversation, one file per month,
    /// one line per message - and the shared files beside them.
    /// </summary>
    /// <remarks>
    /// The <see cref="ChatStore"/> holds what is on screen and forgets it when
    /// the process ends. This is the other half: everything that goes through
    /// the store is written here, and at the next start the last month comes
    /// back out, with the months before it a scroll away.
    ///
    /// <b>The line is the message as the browser gets it</b> - the same JSON,
    /// one object per line (JSON Lines). It costs a few bytes over a bespoke
    /// text format and it buys the only property that matters for an archive
    /// read years later: what was written is what was shown, with nothing
    /// dropped in between and nothing to parse but a JSON object.
    ///
    /// <b>A line is appended, never changed.</b> A correction, a delivery
    /// receipt or a fetched file appends a new line for the same id, in the
    /// file of the month the message was written in. Reading applies them in
    /// order: the last line for an id wins. That way a writer never has to seek
    /// and a half-finished write can only cost the last line.
    ///
    /// <b>Nothing here may take the web app down.</b> An archive is a
    /// convenience; a full disk, a directory gone missing or a file locked by a
    /// backup must cost the line and nothing more. So the writing happens on a
    /// task of its own, fed by a queue: <see cref="Record"/> is called from
    /// inside the chat store's lock and must not so much as touch the disk
    /// there. Failures are reported once and then only counted.
    /// </remarks>
    public sealed class ChatArchive : IAsyncDisposable
    {

        #region Data

        /// <summary>
        /// The archive directory below the repository root, by default.
        /// </summary>
        public const           String    DefaultDirectoryName  = "chats";

        /// <summary>
        /// How much of the past is loaded into the chat store at a start, so
        /// that a browser opens on a conversation and not on a blank page.
        /// </summary>
        public static readonly TimeSpan  DefaultHistoryWindow  = TimeSpan.FromDays(31);

        /// <summary>
        /// How many older messages one scroll upwards asks for.
        /// </summary>
        public const           Int32     DefaultPageSize       = 100;

        /// <summary>
        /// How many files are fetched at the same time.
        /// </summary>
        /// <remarks>
        /// This number and <see cref="MediaStore.MaxBytes"/> are what bound the
        /// memory a download may occupy: three at 64 MiB, and a copy of each on
        /// the way out of the buffer. A file has to be whole before it can be
        /// written - an encrypted one has to be whole before its tag can even be
        /// checked - so the way to make that number smaller is to make one of
        /// these two smaller, not to stream.
        /// </remarks>
        private const          Int32     ParallelDownloads     = 3;

        /// <summary>
        /// How many messages are remembered as already fetched for.
        /// </summary>
        /// <remarks>
        /// The same reasoning, and the same forgetting, as
        /// <see cref="lastWritten"/>: whoever writes to this account decides
        /// how many entries appear here, so there has to be a number. Past it
        /// the set is emptied rather than trimmed, because what is being
        /// avoided is a second download of the same file and not a
        /// correctness problem - a message that comes round again after the
        /// forgetting is fetched a second time and written a second time, and
        /// the last line for it wins as always.
        /// </remarks>
        public  const          Int32     MaxRememberedFetches  = 4096;

        /// <summary>
        /// How many fetches may be outstanding before the rest are let go.
        /// </summary>
        /// <remarks>
        /// Only three run at once; the others sit in front of that semaphore,
        /// each holding on to its message and its URL. A peer sending ten
        /// thousand links would otherwise put ten thousand tasks in that
        /// queue, which is a queue nobody bounded. Past this number the link
        /// is not fetched at all - the message itself is archived either way,
        /// so what is lost is a copy of somebody else's file and not anything
        /// that was said.
        /// </remarks>
        public  const          Int32     MaxPendingFetches     = 64;

        private readonly Channel<ChatMessage>      queue;
        private readonly CancellationTokenSource   stopping   = new();
        private readonly Task                      writer;
        private readonly MediaStore?               media;
        private readonly ILogger                   logger;
        private readonly SemaphoreSlim             downloads  = new(ParallelDownloads, ParallelDownloads);
        private readonly Lock                      @lock      = new();

        /// <summary>
        /// The last line written per message, so that the very same line is not
        /// written twice - which it would be for a fetched file, whose new line
        /// comes both from here and back through the chat store.
        /// </summary>
        private readonly Dictionary<String, String>  lastWritten  = [];

        /// <summary>
        /// The messages a file has been fetched for, so that a message recorded
        /// again - a receipt, a correction - does not fetch it a second time.
        /// </summary>
        private readonly HashSet<String>             fetched      = [];

        private readonly List<Task>                  running      = [];

        /// <summary>
        /// How many links were let go because too many fetches were already
        /// outstanding.
        /// </summary>
        private          Int64                       declined     = 0;

        private Boolean  problemReported;
        private Int64    suppressedProblems;
        private Int64    recorded;
        private Int64    persisted;

        #endregion

        #region Properties

        /// <summary>
        /// The directory everything is written below.
        /// </summary>
        public String    Root           { get; }

        /// <summary>
        /// Whose conversations are being written, or null before an account is
        /// configured. Everything below the root is filed under it.
        /// </summary>
        public JID?      Account        { get; private set; }

        /// <summary>
        /// How many messages are currently remembered as fetched for, and how
        /// many fetches are outstanding. Both are bounded on purpose; see the
        /// constants they are bounded by.
        /// </summary>
        public Int32 RememberedFetches
        {
            get { lock (@lock) { return fetched.Count; } }
        }

        /// <summary>
        /// How many fetches have been started and not yet finished.
        /// </summary>
        public Int32 PendingFetches
        {
            get { lock (@lock) { return running.Count; } }
        }

        /// <summary>
        /// How many shared files were not fetched because too many fetches were
        /// already outstanding.
        /// </summary>
        public Int64 DeclinedFetches
            => Interlocked.Read(ref declined);

        /// <summary>
        /// Whether shared files are fetched and kept beside the conversations.
        /// </summary>
        public Boolean   KeepsMedia
            => media is not null;

        /// <summary>
        /// How many writes have failed after the first one, which was reported.
        /// </summary>
        public Int64     SuppressedProblems
            => Interlocked.Read(ref suppressedProblems);

        #endregion

        #region Events

        /// <summary>
        /// A shared file was fetched and now lies beside its conversation. The
        /// arguments are the conversation, the id of the message that handed it
        /// over, and the file.
        /// </summary>
        public event Action<JID, String, MediaRef>?  OnMediaStored;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Opens (and creates) an archive below the given directory.
        /// </summary>
        /// <param name="Root">
        /// Where everything goes. A relative path is resolved against the
        /// working directory here, once, so that a later change of the working
        /// directory cannot move the archive somewhere else mid-session.
        /// </param>
        /// <param name="KeepMedia">Whether to fetch the files shared in a conversation.</param>
        /// <param name="LoggerFactory">Where a problem with the archive is reported.</param>
        public ChatArchive(String           Root,
                           Boolean          KeepMedia       = true,
                           ILoggerFactory?  LoggerFactory   = null)
        {

            this.Root    = Path.GetFullPath(Root);
            this.logger  = LoggerFactory?.CreateLogger<ChatArchive>() ?? NullLogger<ChatArchive>.Instance;

            OwnerOnlyFile.CreateDirectory(this.Root);

            this.media   = KeepMedia
                               ? new MediaStore(this.Root)
                               : null;

            // Unbounded: dropping a message because the disk is slow is the one
            // outcome an archive may not have. The queue holds JSON-sized
            // objects and is drained by a task that does nothing else.
            this.queue   = Channel.CreateUnbounded<ChatMessage>(
                               new UnboundedChannelOptions {
                                   SingleReader  = true,
                                   SingleWriter  = false
                               }
                           );

            this.writer  = Task.Run(WriteLoopAsync);

        }

        #endregion


        #region UseAccount(Account)

        /// <summary>
        /// Whose conversations are written from now on. Everything queued
        /// before this was filed under the account that was in force then.
        /// </summary>
        public void UseAccount(JID? Account)
        {
            lock (@lock)
            {

                this.Account = Account?.Bare;

                // A different account is a different archive: what was written
                // twice is nothing to suppress, and what was fetched for the
                // old one says nothing about the new.
                lastWritten.Clear();
                fetched.    Clear();

            }
        }

        #endregion

        #region Record(Message)

        /// <summary>
        /// Puts a message - new, corrected, confirmed - in line to be written.
        /// </summary>
        /// <remarks>
        /// Called from inside the chat store's lock. It may not write, wait or
        /// throw here; all three would be paid for by every browser hanging on
        /// the event stream.
        /// </remarks>
        public void Record(ChatMessage Message)
        {

            if (Account is null)
                return;

            Interlocked.Increment(ref recorded);

            queue.Writer.TryWrite(Message);

        }

        #endregion

        #region FlushAsync(Timeout = null)

        /// <summary>
        /// Waits until what has been recorded so far has reached the disk.
        /// </summary>
        /// <remarks>
        /// Nothing in the running web app needs this - the queue is drained by
        /// itself and the shutdown waits for it. It is for whoever wants to
        /// read back what they just wrote, which is a test and a person at a
        /// console.
        /// </remarks>
        public async Task FlushAsync(TimeSpan? Timeout = null)
        {

            var deadline = DateTimeOffset.UtcNow + (Timeout ?? TimeSpan.FromSeconds(10));

            while (Interlocked.Read(ref persisted) < Interlocked.Read(ref recorded) &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(5);
            }

        }

        #endregion


        #region Conversations()

        /// <summary>
        /// Every conversation this account has an archive of.
        /// </summary>
        /// <remarks>
        /// The JID comes out of the file and not out of the directory name: a
        /// name went through <see cref="ChatArchivePaths.SafeName"/> and cannot
        /// always be turned back into the JID it was made from, while every
        /// line in the file names the conversation it belongs to.
        /// </remarks>
        public IReadOnlyList<JID> Conversations()
        {

            var account = Account;

            if (account is null)
                return [];

            var directory = ChatArchivePaths.AccountDirectory(Root, account.Value.ToString());

            if (!Directory.Exists(directory))
                return [];

            var found = new List<JID>();

            try
            {
                foreach (var conversation in Directory.EnumerateDirectories(directory))
                    if (TryReadConversationJID(conversation, out var jid))
                        found.Add(jid);
            }
            catch (Exception e)
            {
                logger.LogWarning("The archive in '{Directory}' could not be listed: {Error}", directory, e.Message);
            }

            return found;

        }

        #endregion

        #region Load(Peer, NotBefore, MaxMessages = 500)

        /// <summary>
        /// The recent past of one conversation: everything from
        /// <paramref name="NotBefore"/> on, oldest first, at most
        /// <paramref name="MaxMessages"/> of them - the newest ones when there
        /// are more.
        /// </summary>
        public IReadOnlyList<ChatMessage> Load(JID             Peer,
                                               DateTimeOffset  NotBefore,
                                               Int32           MaxMessages   = 500)
        {

            var found = new List<ChatMessage>();

            // Backwards through the months, because "the last 500" is what is
            // wanted and the newest month is where they are.
            foreach (var file in MonthFiles(Peer))
            {

                // The file of a month that ended before the window began holds
                // nothing that is wanted, and every file after it is older.
                if (file.Month.AddMonths(1) <= NotBefore)
                    break;

                found.InsertRange(0, Read(file.Path, Peer).Where(message => message.Timestamp >= NotBefore));

                if (found.Count >= MaxMessages)
                    break;

            }

            return found.Count > MaxMessages
                       ? found[^MaxMessages..]
                       : found;

        }

        #endregion

        #region LoadBefore(Peer, Before, Limit = DefaultPageSize)

        /// <summary>
        /// The messages of one conversation written before a point in time:
        /// the newest <paramref name="Limit"/> of them, oldest first, and
        /// whether there are older ones still.
        /// </summary>
        /// <remarks>
        /// This is what a scroll upwards asks for. It reads from the archive
        /// and leaves the chat store alone: what somebody scrolled back to is
        /// their browser's business, not the state of the web app.
        /// </remarks>
        public (IReadOnlyList<ChatMessage> Messages, Boolean HasMore) LoadBefore(JID             Peer,
                                                                                 DateTimeOffset  Before,
                                                                                 Int32           Limit   = DefaultPageSize)
        {

            if (Limit < 1)
                return ([], HasOlderThan(Peer, Before));

            var found = new List<ChatMessage>();

            foreach (var file in MonthFiles(Peer))
            {

                if (file.Month >= Before)
                    continue;

                found.InsertRange(0, Read(file.Path, Peer).Where(message => message.Timestamp < Before));

                // One more than asked for answers both questions at once: what
                // to hand over, and whether to offer another scroll.
                if (found.Count > Limit)
                    return (found[^Limit..], true);

            }

            return (found, false);

        }

        #endregion

        #region HasOlderThan(Peer, When)

        /// <summary>
        /// Whether the archive holds anything of this conversation written
        /// before the given time - which is what tells a browser whether
        /// scrolling up leads anywhere.
        /// </summary>
        public Boolean HasOlderThan(JID             Peer,
                                    DateTimeOffset  When)
        {

            foreach (var file in MonthFiles(Peer))
            {

                if (file.Month >= When)
                    continue;

                if (Read(file.Path, Peer).Any(message => message.Timestamp < When))
                    return true;

            }

            return false;

        }

        #endregion

        #region TryGetMedia(Peer, Name, out Path, out ContentType)

        /// <summary>
        /// The stored file of a conversation, and what it is to be served as.
        /// </summary>
        /// <remarks>
        /// <b>The name comes out of a URL, so it is a stranger's text twice
        /// over</b> - once from whoever shared the file, once from whoever
        /// asked for it. It is not combined into a path before
        /// <see cref="ChatArchivePaths.IsSafeName"/> has said that it is one
        /// segment, with no separator and no '..' in it.
        ///
        /// The content type is read off the extension, and the extension was
        /// this side's own doing: <see cref="MediaStore"/> only stores what its
        /// allow-list covers and gives the file the extension of the type the
        /// server announced. An extension outside that list therefore cannot
        /// occur - and if it ever does, it is served as a download rather than
        /// as something the browser opens.
        /// </remarks>
        public Boolean TryGetMedia(JID          Peer,
                                   String?      Name,
                                   out String?  Path,
                                   out String?  ContentType)
        {

            Path         = null;
            ContentType  = null;

            var account = Account;

            if (account is null || !ChatArchivePaths.IsSafeName(Name))
                return false;

            var path = System.IO.Path.Combine(
                           ChatArchivePaths.MediaDirectory(Root, account.Value.ToString(), Peer.Bare.ToString()),
                           Name
                       );

            if (!File.Exists(path))
                return false;

            Path         = path;
            ContentType  = MediaStore.ContentTypeForName(Name);
            return true;

        }

        #endregion


        #region (private) WriteLoopAsync()

        /// <summary>
        /// The one task that touches the log files. It drains what the queue
        /// holds and appends it, keeping the order the store raised it in.
        /// </summary>
        private async Task WriteLoopAsync()
        {

            try
            {

                while (await queue.Reader.WaitToReadAsync())
                {

                    // Everything for the same file in one append: a busy
                    // conversation should not be one open-write-close per word.
                    var batch = new List<ChatMessage>();

                    while (queue.Reader.TryRead(out var message))
                    {

                        batch.Add(message);

                        if (batch.Count >= 256)
                            break;

                    }

                    Write(batch);

                    Interlocked.Add(ref persisted, batch.Count);

                    foreach (var message in batch)
                        StartFetching(message);

                }

            }
            catch (Exception e)
            {
                logger.LogError("The chat archive stopped writing: {Error}", e.Message);
            }

        }

        #endregion

        #region (private) Write(Messages)

        private void Write(IReadOnlyList<ChatMessage> Messages)
        {

            var account = Account;

            if (account is null)
                return;

            String?              openPath  = null;
            StringBuilder?       pending   = null;

            foreach (var message in Messages)
            {

                var line = message.ToJSON().ToString(Formatting.None);
                var key  = Key(message);

                lock (@lock)
                {

                    // The same line twice says nothing new. It happens for a
                    // fetched file, whose line is written here and then comes
                    // back through the chat store.
                    if (lastWritten.TryGetValue(key, out var previous) && previous == line)
                        continue;

                    // Bounded, and roughly: this is a way of not writing the
                    // same line twice in a row, not a record of everything ever
                    // written.
                    if (lastWritten.Count > 4096)
                        lastWritten.Clear();

                    lastWritten[key] = line;

                }

                var path = ChatArchivePaths.LogFile(Root,
                                                    account.Value.ToString(),
                                                    message.Chat.ToString(),
                                                    message.Timestamp);

                if (path != openPath)
                {

                    Append(openPath, pending);

                    openPath  = path;
                    pending   = new StringBuilder();

                }

                pending!.Append(line).Append('\n');

            }

            Append(openPath, pending);

        }

        #endregion

        #region (private) Append(Path, Content)

        private void Append(String? Path, StringBuilder? Content)
        {

            if (Path is null || Content is null || Content.Length == 0)
                return;

            try
            {

                OwnerOnlyFile.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

                OwnerOnlyFile.Append(Path, Content.ToString());

            }
            catch (Exception e)
            {
                NoteProblem(Path, e);
            }

        }

        #endregion

        #region (private) NoteProblem(Path, Exception)

        /// <summary>
        /// Says once that the archive cannot be written, and counts the rest.
        /// A disk that is full stays full, and a line about it per message
        /// would be the second thing to go wrong.
        /// </summary>
        private void NoteProblem(String Path, Exception Exception)
        {

            lock (@lock)
            {

                if (problemReported)
                {
                    Interlocked.Increment(ref suppressedProblems);
                    return;
                }

                problemReported = true;

            }

            logger.LogError("The chat archive could not be written to '{Path}': {Error} " +
                            "(said once; further failures are only counted)",
                            Path, Exception.Message);

        }

        #endregion


        #region (private) StartFetching(Message)

        /// <summary>
        /// Fetches the file a message handed over, if it handed over one and
        /// nobody has fetched it yet.
        /// </summary>
        private void StartFetching(ChatMessage Message)
        {

            var account = Account;

            if (media   is null ||
                account is null ||
                Message.Media is not null ||
                stopping.IsCancellationRequested)
            {
                return;
            }

            if (MediaLinks.Detect(Message.Body) is not Uri url)
                return;

            lock (@lock)
            {

                running.RemoveAll(task => task.IsCompleted);

                // Before the set is written to, not after: a link that is let
                // go must not be remembered as fetched, or it would never be
                // fetched again either.
                if (running.Count >= MaxPendingFetches)
                {

                    if (Interlocked.Increment(ref declined) == 1)
                        logger.LogWarning("More than {Pending} files were waiting to be fetched; further shared files are left where they are. The messages themselves are archived as always.",
                                          MaxPendingFetches);

                    return;

                }

                if (!fetched.Add(Key(Message)))
                    return;

                if (fetched.Count > MaxRememberedFetches)
                    fetched.Clear();

                running.Add(FetchAsync(account.Value, Message, url));

            }

        }

        #endregion

        #region (private static) Key(Message)

        /// <summary>
        /// What names one message within the archive. The NUL keeps a chat
        /// whose JID ends in an id from meeting an id that begins with one.
        /// </summary>
        private static String Key(ChatMessage Message)
            => String.Concat(Message.Chat.ToString(), "\0", Message.Id);

        #endregion

        #region (private) FetchAsync(Account, Message, URL)

        private async Task FetchAsync(JID          Account,
                                      ChatMessage  Message,
                                      Uri          URL)
        {

            try
            {

                await downloads.WaitAsync(stopping.Token);

                try
                {

                    var (stored, problem) = await media!.FetchAsync(Account,
                                                                    Message.Chat,
                                                                    URL,
                                                                    DateTimeOffset.UtcNow,
                                                                    stopping.Token);

                    if (stored is null)
                    {
                        // Not an error of this program: somebody shared
                        // something that is gone, or too big, or not a kind of
                        // file kept here.
                        logger.LogInformation("The file {URL} shared in {Chat} was not stored: {Problem}",
                                              URL, Message.Chat, problem);
                        return;
                    }

                    logger.LogInformation("Stored {Size} bytes of {Type} from {Chat} as '{Name}'",
                                          stored.Size, stored.ContentType, Message.Chat, stored.Name);

                    // Written from here as well as announced, so that the
                    // archive keeps it even when the message has meanwhile
                    // fallen out of the chat store.
                    Record(Message with { Media = stored });

                    OnMediaStored?.Invoke(Message.Chat, Message.Id, stored);

                }
                finally
                {
                    downloads.Release();
                }

            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception e)
            {
                logger.LogWarning("Fetching {URL} for {Chat} failed: {Error}", URL, Message.Chat, e.Message);
            }

        }

        #endregion


        #region (private) MonthFiles(Peer)

        /// <summary>
        /// The month files of one conversation, the newest month first.
        /// </summary>
        private IEnumerable<(String Path, DateTimeOffset Month)> MonthFiles(JID Peer)
        {

            var account = Account;

            if (account is null)
                yield break;

            var directory = ChatArchivePaths.ConversationDirectory(Root,
                                                                   account.Value.ToString(),
                                                                   Peer.Bare.ToString());

            String[] files;

            try
            {

                if (!Directory.Exists(directory))
                    yield break;

                files = Directory.GetFiles(directory, "*" + ChatArchivePaths.LogFileExtension);

            }
            catch (Exception e)
            {
                logger.LogWarning("The archive of {Chat} could not be listed: {Error}", Peer, e.Message);
                yield break;
            }

            var months = new List<(String Path, DateTimeOffset Month)>();

            foreach (var file in files)
                if (ChatArchivePaths.TryParseMonth(file, out var month))
                    months.Add((file, month));

            foreach (var month in months.OrderByDescending(entry => entry.Month))
                yield return month;

        }

        #endregion

        #region (private) Read(Path, Peer)

        /// <summary>
        /// One month of one conversation, oldest first.
        /// </summary>
        /// <remarks>
        /// The last line for an id wins: that is how a correction, a receipt
        /// and a fetched file reach a message that was written in the same
        /// month - by being appended after it. The order of the conversation is
        /// the order the first line of each message had, because a later line
        /// for the same id replaces the message in place.
        ///
        /// A line that is not a message is skipped rather than refused. Half a
        /// line at the end of a file is what a process killed mid-write leaves,
        /// and the months before it are still worth reading.
        /// </remarks>
        private IReadOnlyList<ChatMessage> Read(String Path, JID Peer)
        {

            var order    = new List<String>();
            var byId     = new Dictionary<String, ChatMessage>();
            var damaged  = 0;

            try
            {

                foreach (var line in File.ReadLines(Path, Encoding.UTF8))
                {

                    if (line.Length == 0 || line[0] != '{')
                        continue;

                    ChatMessage? message;

                    try
                    {

                        if (!ChatMessage.TryParse(JObject.Parse(line), Peer, out message))
                        {
                            damaged++;
                            continue;
                        }

                    }
                    catch (JsonException)
                    {
                        damaged++;
                        continue;
                    }

                    if (!byId.ContainsKey(message.Id))
                        order.Add(message.Id);

                    byId[message.Id] = message;

                }

            }
            catch (Exception e)
            {
                logger.LogWarning("'{Path}' could not be read: {Error}", Path, e.Message);
                return [];
            }

            if (damaged > 0)
                logger.LogWarning("{Count} unreadable line(s) in '{Path}' were skipped", damaged, Path);

            return order.Select(id => byId[id]).
                         OrderBy(message => message.Timestamp).
                         ToList();

        }

        #endregion

        #region (private) TryReadConversationJID(Directory, out JID)

        /// <summary>
        /// Whose conversation an archive directory holds, read off the first
        /// message in it.
        /// </summary>
        private Boolean TryReadConversationJID(String Directory, out JID Peer)
        {

            Peer = default;

            try
            {

                foreach (var file in System.IO.Directory.
                                         GetFiles(Directory, "*" + ChatArchivePaths.LogFileExtension).
                                         OrderDescending())
                {

                    foreach (var line in File.ReadLines(file, Encoding.UTF8))
                    {

                        if (line.Length == 0 || line[0] != '{')
                            continue;

                        try
                        {

                            if (JObject.Parse(line).Value<String>("chat") is String text &&
                                JID.TryParse(text, out var jid) &&
                                jid.Localpart is not null)
                            {
                                Peer = jid.Bare;
                                return true;
                            }

                        }
                        catch (JsonException)
                        { }

                    }

                }

            }
            catch (Exception e)
            {
                logger.LogWarning("'{Directory}' could not be read: {Error}", Directory, e.Message);
            }

            return false;

        }

        #endregion


        #region DisposeAsync()

        /// <summary>
        /// Writes what is still queued, then stops. A message that went through
        /// the store a moment before the end belongs in the archive as much as
        /// any other.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            queue.Writer.TryComplete();

            try
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e)
            {
                logger.LogWarning("The chat archive did not finish writing: {Error}", e.Message);
            }

            await stopping.CancelAsync();

            Task[] pending;

            lock (@lock)
                pending = [.. running];

            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // A download that does not end in time is dropped; the file it
                // would have stored is not worth holding up the shutdown.
            }

            stopping.Dispose();
            downloads.Dispose();
            media?.Dispose();

        }

        #endregion

    }

}
