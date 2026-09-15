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

using Microsoft.Extensions.Logging;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Chats;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// The XMPP side of the API: the account, the client made from it, how
    /// the connection is made, and which events of the <see cref="XMPPClient"/>
    /// end up in the chat store.
    /// </summary>
    /// <remarks>
    /// This is the counterpart of WireUpUserInterface in XMPPConsole. There
    /// every event became a line on the console; here it becomes a change of
    /// the chat store, which the store turns into an event for the browsers.
    /// The handlers do nothing that could block the receive loop: the store
    /// takes a lock for a few microseconds, and publishing is fire-and-forget.
    ///
    /// The client is not for the lifetime of the process: the account page
    /// replaces it. Every handler therefore compares the client that raised
    /// the event against the current one - a client on its way out must not
    /// write into the store of its successor.
    /// </remarks>
    public sealed partial class XMPPWebAPI
    {

        #region Data

        /// <summary>
        /// How many messages are loaded for a conversation that has nothing
        /// inside the history window - enough for the list to show what it was
        /// about, and no more.
        /// </summary>
        public const Int32 QuietConversationMessages = 20;

        // One account change at a time: writing the file, dropping the old
        // client and building the new one is a sequence a second request must
        // not interleave with.
        private readonly SemaphoreSlim  applying           = new(1, 1);
        private          XMPPClient?    connectingClient;

        // Switching OMEMO on is reached from every transition into Connected,
        // and a reconnect can produce several of those in a row. The gate makes
        // sure the key material is read and the device list published once.
        private readonly SemaphoreSlim  switchingOnOmemo   = new(1, 1);

        #endregion

        #region Properties

        /// <summary>
        /// The file the account settings live in.
        /// </summary>
        public AccountFile       AccountFile            { get; }

        /// <summary>
        /// The account in use, or null when none is configured yet.
        /// </summary>
        public AccountSettings?  Settings               { get; private set; }

        /// <summary>
        /// Where the account in use came from.
        /// </summary>
        public AccountSource     Source                 { get; private set; }

        /// <summary>
        /// The XMPP client of the account in use, or null when none is
        /// configured yet.
        /// </summary>
        public XMPPClient?       Client                 { get; private set; }

        /// <summary>
        /// Why the last attempt to connect failed, or null.
        /// </summary>
        public String?           LastConnectionError    { get; private set; }

        /// <summary>
        /// When the current connection was established.
        /// </summary>
        public DateTimeOffset?   ConnectedAt            { get; private set; }

        /// <summary>
        /// XEP-0384: where the OMEMO keys and sessions are kept, or null to do
        /// no OMEMO at all.
        /// </summary>
        /// <remarks>
        /// The same shape as <see cref="Archive"/>: a place to keep things, or
        /// nothing and the feature is not there. It is a place and not a
        /// Boolean because OMEMO without one would be worse than none - a
        /// device whose keys do not survive the process has a new fingerprint
        /// at every start, and every comparison anybody ever makes with it is
        /// worthless.
        /// </remarks>
        public String?           OmemoDirectory         { get; }

        #endregion


        #region ConnectAsync(CancellationToken = default)

        /// <summary>
        /// Connect the current client to its XMPP server. A failure is not an
        /// exception here but a state: it is kept in
        /// <see cref="LastConnectionError"/>, shown to the browsers, and the
        /// web app keeps running - a page that says why it is not connected
        /// is worth more than a process that is gone. Nothing happens without
        /// an account.
        /// </summary>
        public async Task ConnectAsync(CancellationToken CancellationToken = default)
        {

            var client = Client;

            if (client is null)
                return;

            // One attempt per client at a time; an attempt for a client that
            // was replaced meanwhile does not hold the new one back.
            if (Interlocked.CompareExchange(ref connectingClient, client, null) is not null)
                return;

            try
            {

                if (ReferenceEquals(client, Client))
                    LastConnectionError = null;

                await client.ConnectAsync(CancellationToken);

            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (SaslDowngradeException e) when (ReferenceEquals(client, Client))
            {

                // The same three causes as in XMPPConsole, and the same care:
                // only one of them is answered by lowering the bar.
                LastConnectionError = e.Cause switch {

                    SaslDowngradeCause.BelowConfiguredMinimum
                        => $"{e.Message} If {e.Offered} is really all the server offers, lower the minimum " +
                           $"SASL mechanism to {e.Offered} on the account page.",

                    SaslDowngradeCause.BelowPinnedMechanism
                        => $"{e.Message} This server logged in with {e.Demanded} before; a server does not lose a " +
                           "mechanism by itself - treat this as a man in the middle until you know otherwise.",

                    SaslDowngradeCause.ForgedAnnouncement
                        => $"{e.Message} Either something in between changed the mechanism list in flight, or the " +
                           "server implements a later revision of XEP-0474 than this client; if you have " +
                           "established that it is the latter, allow it on the account page.",

                    _   => e.Message

                };

                logger.LogError("XMPP sign-in refused: {Error}", LastConnectionError);
                PublishConnection(ConnectionState.Connecting, ConnectionState.Disconnected);

            }
            catch (Exception e) when (ReferenceEquals(client, Client))
            {

                LastConnectionError = e.InnerException is not null
                                          ? $"{e.Message} ({e.InnerException.Message})"
                                          : e.Message;

                logger.LogError("XMPP connection failed: {Error}", LastConnectionError);
                PublishConnection(ConnectionState.Connecting, ConnectionState.Disconnected);

            }
            catch (Exception e)
            {
                // A client on its way out: whatever it says is history.
                logger.LogDebug("A replaced client's connection attempt ended: {Error}", e.Message);
            }
            finally
            {
                Interlocked.CompareExchange(ref connectingClient, null, client);
            }

        }

        #endregion

        #region ApplyAccountAsync(Settings, Save = true)

        /// <summary>
        /// Switch to another account: write the file, drop the old connection,
        /// forget the old conversations when the account is a different one,
        /// and connect anew in the background.
        /// </summary>
        /// <param name="Settings">The account to use from now on.</param>
        /// <param name="Save">Whether to write the account file; the command
        /// line overrides for one run without touching the file.</param>
        public async Task ApplyAccountAsync(AccountSettings  Settings,
                                            Boolean          Save   = true)
        {

            await applying.WaitAsync();

            try
            {

                if (Save)
                    AccountFile.Save(Settings);

                // A different account is a different world: what was said to
                // the old one is nothing the new one should show. The same
                // account reconnecting keeps its conversations.
                var sameAccount = this.Settings is not null &&
                                  this.Settings.BareJID == Settings.BareJID;

                await ReplaceClientAsync(WireUp(Settings), Settings, Save ? AccountSource.File : AccountSource.Arguments);

                if (!sameAccount)
                {
                    Chats.Clear();
                    LoadHistory();
                }

            }
            finally
            {
                applying.Release();
            }

            // Outside the lock: connecting can take a while, and a second
            // account change should be able to interrupt it.
            _ = ConnectAsync();

        }

        #endregion

        #region ForgetAccountAsync()

        /// <summary>
        /// Drop the account entirely: disconnect, delete the file, forget the
        /// conversations. The next start - and the account page - ask again.
        /// </summary>
        public async Task ForgetAccountAsync()
        {

            await applying.WaitAsync();

            try
            {

                AccountFile.Delete();

                await ReplaceClientAsync(null, null, AccountSource.None);

                Chats.Clear();

                ConnectedAt          = null;
                LastConnectionError  = null;

                PublishConnection(ConnectionState.Disconnected, ConnectionState.Disconnected);

            }
            finally
            {
                applying.Release();
            }

        }

        #endregion

        #region LoadHistory()

        /// <summary>
        /// Puts the recent past back into the chat store: every conversation
        /// the archive holds for the account in use, as far back as
        /// <see cref="HistoryWindow"/> reaches. What lies before that stays on
        /// disk until a browser scrolls up to it.
        /// </summary>
        /// <remarks>
        /// Called at a start and after an account change, before the browsers
        /// ask - so this reads files on the thread it is called on rather than
        /// handing the browsers an empty page and filling it in afterwards.
        /// A month of conversations is a few megabytes of JSON; the wait is
        /// shorter than the XMPP connection that follows it.
        /// </remarks>
        /// <returns>How many messages came back.</returns>
        public Int32 LoadHistory()
        {

            if (Archive is null || Settings is null)
                return 0;

            var since         = DateTimeOffset.UtcNow - HistoryWindow;
            var messageCount  = 0;
            var chatCount     = 0;

            foreach (var peer in Archive.Conversations())
            {

                var messages = Archive.Load(peer, since, MaxHistoryPageSize);

                // A conversation nobody has said anything in for longer than
                // the window is still a conversation: without a line of it, it
                // would not be in the list at all - and what is not in the list
                // cannot be scrolled back either.
                if (messages.Count == 0)
                    messages = Archive.LoadBefore(peer, DateTimeOffset.UtcNow, QuietConversationMessages).Messages;

                if (messages.Count == 0)
                    continue;

                Chats.LoadHistory(peer, messages);

                messageCount += messages.Count;
                chatCount++;

            }

            if (messageCount > 0)
                logger.LogInformation("{Messages} archived message(s) in {Chats} conversation(s) were loaded from '{Root}'",
                                      messageCount, chatCount, Archive.Root);

            return messageCount;

        }

        #endregion

        #region (private) ReplaceClientAsync(NewClient, NewSettings, NewSource)

        /// <summary>
        /// Put a new client (or none) in place of the old one and tear the old
        /// one down. The old client is dropped from <see cref="Client"/> first,
        /// so that its dying events are ignored by the guarded handlers.
        /// </summary>
        private async Task ReplaceClientAsync(XMPPClient?       NewClient,
                                              AccountSettings?  NewSettings,
                                              AccountSource     NewSource)
        {

            var old = Client;

            Client       = NewClient;
            Settings     = NewSettings;
            Source       = NewSource;
            ConnectedAt  = null;

            // The archive is filed by account, so it has to learn of the change
            // before the first message of the new one arrives.
            Archive?.UseAccount(NewSettings?.BareJID);

            ApplyPlaintextChats(NewSettings?.BareJID);

            if (old is not null)
            {

                try
                {
                    await old.DisposeAsync();
                }
                catch (Exception e)
                {
                    logger.LogDebug("The previous client did not close cleanly: {Error}", e.Message);
                }

                // Free the connecting slot if the old client still held it, so
                // that the new client's ConnectAsync is not turned away by an
                // attempt that belongs to a client already gone.
                Interlocked.CompareExchange(ref connectingClient, null, old);

            }

        }

        #endregion

        #region (private) StartConnecting()

        /// <summary>
        /// Connect in the background, unless an attempt for the current client
        /// is already running or there is no account.
        /// </summary>
        private Boolean StartConnecting()
        {

            if (Client is null || Volatile.Read(ref connectingClient) is not null)
                return false;

            _ = ConnectAsync();
            return true;

        }

        #endregion


        #region (private) WireUp(Settings)

        /// <summary>
        /// A client for the given account, with its events forwarded into the
        /// chat store - but only while it is the current client.
        /// </summary>
        private XMPPClient WireUp(AccountSettings Settings)
        {

            var client = Settings.CreateClient(loggerFactory);

            client.OnMessage             += (timestamp, sender, message,      ct) => { if (Mine(sender)) HandleMessage    (message);      return Task.CompletedTask; };
            client.OnEncryptedMessage    += (timestamp, sender, message, omemo, ct) => { if (Mine(sender)) HandleEncrypted(message, omemo); return Task.CompletedTask; };
            client.OnOmemoIdentityChanged += (timestamp, sender, change,   ct) => { if (Mine(sender)) HandleIdentityChanged(change);   return Task.CompletedTask; };
            client.OnCarbonMessage       += (timestamp, sender, carbon,       ct) => { if (Mine(sender)) HandleCarbon     (carbon);       return Task.CompletedTask; };
            client.OnChatState           += (timestamp, sender, from, state,  ct) => { if (Mine(sender)) HandleChatState  (from, state);  return Task.CompletedTask; };
            client.OnChatMarker          += (timestamp, sender, marker,       ct) => { if (Mine(sender)) HandleChatMarker (marker);       return Task.CompletedTask; };
            client.OnReceiptReceived     += (timestamp, sender, from, id,     ct) => { if (Mine(sender)) HandleReceipt    (from, id);     return Task.CompletedTask; };
            client.OnPresenceChanged     += (timestamp, sender, from, type,   ct) => { if (Mine(sender)) HandlePresence   (sender, from); return Task.CompletedTask; };
            client.OnStateChanged        += (timestamp, sender, old, current, ct) => { if (Mine(sender)) HandleState      (sender, old, current); return Task.CompletedTask; };

            client.OnRosterItemAdded     += (timestamp, sender, item,         ct) => { if (Mine(sender)) Chats.SetContact(item.BareJid, item.Name, item.Subscription); return Task.CompletedTask; };
            client.Roster.OnItemUpdated  += (timestamp, sender, item,         ct) => { if (Mine(client)) Chats.SetContact(item.BareJid, item.Name, item.Subscription); return Task.CompletedTask; };
            client.OnRosterItemRemoved   += (timestamp, sender, jid,          ct) => { if (Mine(sender)) Chats.RemoveContact(jid);                                      return Task.CompletedTask; };
            client.OnSubscriptionRequest += (timestamp, sender, from, status, ct) => { if (Mine(sender)) Chats.SetPendingRequest(from.Bare, true);                      return Task.CompletedTask; };

            client.OnError               += (timestamp, sender, error,        ct) => { if (Mine(sender)) PublishNotice("error",   error);                                                 return Task.CompletedTask; };
            client.OnSpoofingAttempt     += (timestamp, sender, details,      ct) => { if (Mine(sender)) PublishNotice("warning", $"Spoofing attempt fended off: {details}");             return Task.CompletedTask; };
            client.OnStanzaError         += (timestamp, sender, from, error,  ct) => { if (Mine(sender)) PublishNotice("error",   $"{from?.ToString() ?? "The server"} refused a stanza: {error}"); return Task.CompletedTask; };
            client.OnStreamError         += (timestamp, sender, error,        ct) => { if (Mine(sender)) PublishNotice("error",   $"The server ended the stream: {error}");               return Task.CompletedTask; };

            return client;

        }

        /// <summary>
        /// Whether this object is the current client - the roster events name
        /// the roster as their sender, so the client is passed for those.
        /// </summary>
        private Boolean Mine(Object? Sender)
            => ReferenceEquals(Sender, Client) ||
               (Client is not null && ReferenceEquals(Sender, Client.Roster));

        #endregion


        #region (private) HandleMessage   (Message)

        private void HandleMessage(XMPPMessage Message)
        {

            // Chat states and receipts travel in messages without a body;
            // they have events of their own. A room is not a conversation
            // this client knows how to hold.
            if (String.IsNullOrEmpty(Message.Body) ||
                Message.Type is MessageType.GroupChat or MessageType.Error)
            {
                return;
            }

            // XEP-0461: Text and not Body. An answer carries the text it
            // answers a second time, as "> " lines, for clients that cannot
            // follow the reference - and putting both into the archive files the
            // same sentence twice, once as somebody's line and once inside the
            // answer to it. The quotation is kept beside the text instead,
            // because it is sometimes the only copy of what is being answered.
            Chats.AddIncoming(
                Message.FromBareJid,
                Message.From.ToString(),
                Message.MessageId,
                Message.Text,
                new DateTimeOffset(Message.Timestamp),
                Delayed:    Message.IsDelayed,
                Corrects:   Message.ReplacesId,
                RepliesTo:  Message.RepliesTo?.Id,
                Quote:      Message.Quote
            );

        }

        #endregion

        #region (private) HandleEncrypted (Message, Omemo)

        /// <summary>
        /// A message that arrived OMEMO-encrypted and has been decrypted
        /// (XEP-0384).
        /// </summary>
        /// <remarks>
        /// <b>The one thing that has to be decided here is which way it went.</b>
        /// An encrypted message reaches this from two places: addressed to this
        /// device, and wrapped in a carbon of another device of our own. In the
        /// second case it may be one the other device <i>sent</i> - OMEMO
        /// encrypts to one's own further devices, which is the whole reason the
        /// carbon can be read at all - and then the sender is this very account.
        /// Filing that as incoming would open a conversation with oneself and
        /// put one's own sentence into it, attributed to a stranger.
        ///
        /// So the sender decides: our own address means it went out, and the
        /// conversation is the one it was addressed to. That sender is not the
        /// one from the stanza but the one out of the encrypted envelope, which
        /// the library compared against it (XEP-0420) - the outer one anybody
        /// can write.
        ///
        /// <b>What arrives here is poorer than what the ordinary path gets.</b>
        /// The decrypting side of Ratatoskr builds its message without the
        /// delivered-late stamp of XEP-0203 and without the correction id of
        /// XEP-0308, so an encrypted message that was held shows the time it
        /// arrived, and an encrypted correction becomes a second line instead of
        /// replacing the first. That is a gap in the library and not something
        /// to paper over here; it is written down rather than worked around
        /// silently.
        ///
        /// <b>What reaches this method is
        /// <see cref="OmemoIdentityCheck.New"/> or
        /// <see cref="OmemoIdentityCheck.Known"/>, never
        /// <see cref="OmemoIdentityCheck.Changed"/>.</b> A device reporting with
        /// a second key has its message refused - a program cannot tell a new
        /// installation from somebody pushing in between - so there is no line
        /// to mark. That case arrives on an event of its own; see
        /// <see cref="HandleIdentityChanged"/>.
        /// </remarks>
        private void HandleEncrypted(XMPPMessage     Message,
                                     OmemoDecrypted  Omemo)
        {

            if (String.IsNullOrEmpty(Message.Body))
                return;

            var now                = DateTimeOffset.UtcNow;
            var (chat, outgoing)   = EncryptedBelongsTo(Message, Settings?.BareJID);

            if (outgoing)
            {

                Chats.AddOutgoing(
                    chat,
                    Message.MessageId ?? Guid.NewGuid().ToString("N"),
                    Message.Body,
                    now,
                    Carbon:    true,
                    Identity:  Omemo.IdentityCheck
                );

                return;

            }

            Chats.AddIncoming(
                chat,
                Message.From.ToString(),
                Message.MessageId,
                Message.Body,
                now,
                Identity:  Omemo.IdentityCheck
            );

        }

        #endregion

        #region (private) HandleIdentityChanged(Change)

        /// <summary>
        /// XEP-0384: a device that has written before reports with a different
        /// identity key - and its message was refused.
        /// </summary>
        /// <remarks>
        /// <b>This is what blind trust is paid for with.</b> Reading the first
        /// message from a device without anybody comparing a fingerprint is a
        /// deliberate trade, and what it is traded against is noticing a change
        /// afterwards. Without this the device would simply stop arriving,
        /// which from the outside is what nobody writing looks like.
        ///
        /// A notice and not a line in a conversation, because there is no line:
        /// the message that carried the new key was refused and no text of it
        /// exists on this side. Putting it in a chat would mean inventing one.
        ///
        /// Both fingerprints go into the text. The one on file is the one
        /// somebody may once have read out to the person at the other end, and
        /// without it the reader has nothing to compare against - a notice
        /// naming only the new key says "something changed" and leaves them no
        /// way to find out what.
        ///
        /// <b>Nothing is decided here, and there is nothing to decide with.</b>
        /// This version has no way to accept a new key, and inventing a button
        /// for it would be the worst of both: a decision asked of somebody who
        /// has not been given the means to make it. What it can do is say so,
        /// name the two fingerprints, and point at the one way of settling it
        /// that works - asking the person through some other channel.
        /// </remarks>
        private void HandleIdentityChanged(OmemoIdentityChanged Change)
        {

            logger.LogWarning("OMEMO: {Jid} writes from device {Device} with the key {Offered}, " +
                              "which is not the {Known} on file; the message was refused",
                              Change.Jid, Change.DeviceId, Change.OfferedFingerprint, Change.KnownFingerprint);

            PublishNotice("warning",
                          $"{Change.Jid} wrote from device {Change.DeviceId} with a key this side has not seen " +
                          $"before, and the message was not accepted. On file: {Groups(Change.KnownFingerprint)}. " +
                          $"Offered now: {Groups(Change.OfferedFingerprint)}. Either they set that device up anew, " +
                          "or somebody is writing as them - from here the two look the same, so ask them through " +
                          "another channel.");

        }

        /// <summary>
        /// A fingerprint in groups of eight.
        /// </summary>
        /// <remarks>
        /// Sixty-four characters in one run is not something a human being
        /// compares, and comparing it is the only thing it is for. The same
        /// grouping the settings page uses for this side's own.
        /// </remarks>
        private static String Groups(String Fingerprint)

            => String.Join(' ',
                           Enumerable.Range(0, (Fingerprint.Length + 7) / 8)
                                     .Select(group => Fingerprint.Substring(group * 8,
                                                                            Math.Min(8, Fingerprint.Length - group * 8))));

        #endregion

        #region (private) EncryptedBelongsTo(Message, OwnBareJID)

        /// <summary>
        /// Which conversation a decrypted message belongs in, and whether it
        /// went out rather than came in.
        /// </summary>
        /// <remarks>
        /// Apart because it is the one decision in the encrypted path that can
        /// be got wrong quietly - and the wrong answer is not a missing message
        /// but a conversation with oneself, holding one's own sentences under a
        /// stranger's name.
        /// </remarks>
        /// <param name="Message">The decrypted message, whose sender the library compared against the envelope.</param>
        /// <param name="OwnBareJID">This account, or null when none is configured - then nothing can be ours.</param>
        internal static (JID Chat, Boolean Outgoing) EncryptedBelongsTo(XMPPMessage  Message,
                                                                       JID?         OwnBareJID)

            => OwnBareJID is not null && Message.From.Bare == OwnBareJID

                   // Our own address on the inside of a carbon: another device
                   // of ours wrote this, and the conversation is the one it
                   // wrote to.
                   ? (Message.To.Bare,   true)

                   : (Message.From.Bare, false);

        #endregion

        #region (private) SwitchOmemoOnAsync(Client)

        /// <summary>
        /// XEP-0384: reads this account's key material off the disk, publishes
        /// the device and its bundle, and from then on encrypted messages can
        /// be read.
        /// </summary>
        /// <remarks>
        /// <b>Reached from every transition into Connected and not from
        /// ConnectAsync</b>, because a connection that only came about at the
        /// third attempt was never in ConnectAsync's successful path. The gate
        /// and <see cref="XMPPClient.OmemoEnabled"/> make the repetition
        /// harmless: what would otherwise be repeated is the publishing of a
        /// device list, and that is the one operation in OMEMO which hurts
        /// other people's clients when it goes wrong.
        ///
        /// <b>Nothing here can fail the connection.</b> A web app that refused
        /// to sign in because a key file is unreadable would have turned an
        /// encryption that was a bonus into a dependency. It says so instead -
        /// in the log and in the corner of the page - and goes on in the clear.
        ///
        /// <b>What this does is visible from outside.</b> A new device appears
        /// in the account's OMEMO device list, and the clients of every contact
        /// start encrypting to it. Which is why it is possible not to: without
        /// a directory to keep the keys in, this returns at once and the
        /// account never learns of this device. See --no-omemo.
        /// </remarks>
        private async Task SwitchOmemoOnAsync(XMPPClient Client)
        {

            if (OmemoDirectory is null || Client.OmemoEnabled)
                return;

            await switchingOnOmemo.WaitAsync();

            try
            {

                // Both again, inside the gate: the check above keeps the
                // ordinary case out of the semaphore, this one is the one that
                // holds. And a client replaced while we waited is a client
                // whose keys are no longer this account's.
                if (Client.OmemoEnabled || !ReferenceEquals(Client, this.Client))
                    return;

                // The directory before the file: OmemoFileStore creates what is
                // missing, but with whatever the umask gives. What lies in
                // there is the identity key and every chain key of every
                // session - on Unix that wants 0700 around it, and this is the
                // call that sets it.
                OwnerOnlyFile.CreateDirectory(OmemoDirectory);

                var store = new OmemoFileStore(OmemoStorePath(Client.BareJid));

                if (!await Client.EnableOmemoAsync(store))
                {

                    logger.LogWarning("OMEMO: the device list or the bundle was not accepted by the server; " +
                                      "encrypted messages cannot be read");

                    PublishNotice("warning",
                                  "This device could not be announced for encrypted messages: the server did not " +
                                  "accept the OMEMO device list. Whatever is sent encrypted will not be readable here.");

                    return;

                }

                // Blind trust before verification, said out loud although it is
                // the default of the library as well. It decides what happens
                // with a device nobody has compared a fingerprint with, and a
                // security decision that is only a default is one nobody made.
                //
                // For receiving it changes nothing - a message that decrypts is
                // shown whatever one thinks of the device it came from, and the
                // rating travels with the line. It is the switch that will
                // decide the sending, and it is set where the decision belongs.
                Client.Omemo!.TrustNewDevicesBlindly = true;

                logger.LogInformation("OMEMO: device {Device} announced, fingerprint {Fingerprint}",
                                      Client.Omemo.Identity.DeviceId,
                                      Client.Omemo.Fingerprint);

                // So that the page learns the fingerprint without a reload.
                PublishConnection(Client.State, Client.State);

            }
            catch (Exception e)
            {

                logger.LogError("OMEMO could not be switched on: {Error}", e.Message);

                PublishNotice("warning",
                              $"Encrypted messages cannot be read: {e.Message}");

            }
            finally
            {
                switchingOnOmemo.Release();
            }

        }

        #endregion

        #region (private) OmemoStorePath  (BareJID)

        /// <summary>
        /// Where one account's OMEMO keys and sessions live.
        /// </summary>
        /// <remarks>
        /// One file per account, named after it, because the account page can
        /// change the account while the program runs - and a device identity is
        /// the identity <i>of an account</i>. One file for both would hand the
        /// second account the first one's sessions, which decrypt nothing and
        /// would then be published as its own.
        ///
        /// Through SafeName like every other JID that becomes a path: the
        /// account comes out of a file somebody typed into.
        /// </remarks>
        private String OmemoStorePath(JID BareJID)

            => Path.Combine(OmemoDirectory ?? PrivatePaths.Directory(),
                            ChatArchivePaths.SafeName(BareJID.Bare.ToString()) + ".json");

        #endregion

        #region (private) ApplyPlaintextChats(Account)

        /// <summary>
        /// Puts the stored "write this one in the clear" switches into the chat
        /// store for the account now in use.
        /// </summary>
        /// <remarks>
        /// <b>Cleared first.</b> The switches belong to an account, and the one
        /// leaving takes its own with it - carrying them over would silently
        /// turn encryption off for a contact of the new account who happens to
        /// share an address with a contact of the old one.
        /// </remarks>
        private void ApplyPlaintextChats(JID? Account)
        {

            if (PlaintextChats is null)
                return;

            foreach (var chat in Chats.PlaintextConversations())
                Chats.SetEncryption(chat, true);

            // JID is a struct, so the null check does not narrow it - Value is
            // what the account actually is.
            if (Account is not JID account)
                return;

            foreach (var chat in PlaintextChats.Chats(account))
                Chats.SetEncryption(chat, false);

        }

        #endregion

        #region (internal) HowToSend(CanEncrypt, TurnedOff, WasEncryptedBefore)

        /// <summary>
        /// What a message to one conversation travels as.
        /// </summary>
        public enum Delivery
        {

            /// <summary>OMEMO (XEP-0384).</summary>
            Encrypted,

            /// <summary>In the clear, and the line will say so.</summary>
            Plain,

            /// <summary>Not at all - see <see cref="HowToSend"/>.</summary>
            Refused

        }

        /// <summary>
        /// The whole sending policy, in one expression.
        /// </summary>
        /// <remarks>
        /// <b>Automatic, with one thing it will not do.</b> Encrypt when the far
        /// end can read it, otherwise write in the clear and let the line say so
        /// - a lock per message rather than per conversation, because this
        /// program does both and a lock on the conversation would be wrong for
        /// half the lines in it.
        ///
        /// The exception is the case that matters: a conversation that <i>has</i>
        /// been encrypted and suddenly cannot be. Falling back there would hand
        /// whoever took the recipient's device list out of the way exactly what
        /// they were after, and it would look to the writer like an ordinary
        /// message. So it refuses instead, and says why.
        ///
        /// <b>Off outranks all of it</b>, including the refusal. There are
        /// clients whose OMEMO is broken in ways no correctness on this side
        /// repairs, and the answer to those is a switch rather than a contact
        /// who cannot be written to. Somebody who turns it off has been told
        /// what they are turning off.
        ///
        /// This is asked twice for one message, which is why it is a function
        /// and not a branch: once before sending, with what the client can do,
        /// and again if the encrypted message turns out to have reached nobody -
        /// then with <paramref name="CanEncrypt"/> false, because that is what
        /// it has just been shown to be.
        /// </remarks>
        /// <param name="CanEncrypt">Whether OMEMO is on and the far end reachable by it.</param>
        /// <param name="TurnedOff">Whether somebody turned encryption off for this conversation.</param>
        /// <param name="WasEncryptedBefore">Whether anything in this conversation ever travelled encrypted.</param>
        internal static Delivery HowToSend(Boolean  CanEncrypt,
                                           Boolean  TurnedOff,
                                           Boolean  WasEncryptedBefore)

            => TurnedOff           ? Delivery.Plain
             : CanEncrypt          ? Delivery.Encrypted
             : WasEncryptedBefore  ? Delivery.Refused
             :                       Delivery.Plain;

        #endregion

        #region (private) HandleCarbon    (Carbon)

        /// <summary>
        /// The same conversation seen from another device of our own: it goes
        /// into the same chat, under the far end, whichever way it went.
        /// </summary>
        private void HandleCarbon(CarbonMessage Carbon)
        {

            if (String.IsNullOrEmpty(Carbon.Body))
                return;

            if (Carbon.IsSent)
                Chats.AddOutgoing(
                    Carbon.OriginalTo.Bare,
                    Carbon.MessageId ?? Guid.NewGuid().ToString("N"),
                    Carbon.Body,
                    new DateTimeOffset(Carbon.ReceivedAt),
                    Carbon:  true
                );

            else
                Chats.AddIncoming(
                    Carbon.OriginalFrom.Bare,
                    Carbon.OriginalFrom.ToString(),
                    Carbon.MessageId,
                    Carbon.Body,
                    new DateTimeOffset(Carbon.ReceivedAt),
                    Carbon:  true
                );

        }

        #endregion

        #region (private) HandleChatState (From, State)

        private void HandleChatState(JID From, ChatState State)

            => Chats.SetPeerChatState(From.Bare,
                                      State == ChatState.Active ? null : State);

        #endregion

        #region (private) HandleChatMarker(Marker)

        private void HandleChatMarker(ChatMarker Marker)
        {

            if (!JID.TryParse(Marker.From, out var from))
                return;

            switch (Marker.Type)
            {

                case ChatMarkerType.Received:
                    Chats.MarkDelivered(from.Bare, Marker.MessageId);
                    break;

                case ChatMarkerType.Displayed:
                case ChatMarkerType.Acknowledged:
                    Chats.MarkDisplayed(from.Bare, Marker.MessageId);
                    break;

            }

        }

        #endregion

        #region (private) HandleReceipt   (From, MessageId)

        private void HandleReceipt(JID From, String MessageId)
            => Chats.MarkDelivered(From.Bare, MessageId);

        #endregion

        #region (private) HandlePresence  (Client, From)

        /// <summary>
        /// The event itself carries only the type; the show and the status
        /// text are in the roster, which the connection updated before this
        /// runs.
        /// </summary>
        private void HandlePresence(XMPPClient Client, JID From)
        {

            var bare = From.Bare;

            if (bare == Client.BareJid)
                return;

            var item = Client.Roster.GetItem(bare);

            Chats.SetPresence(
                bare,
                item?.Presence       ?? PresenceState.Offline,
                item?.PresenceStatus
            );

        }

        #endregion

        #region (private) HandleState     (Client, Old, New)

        private void HandleState(XMPPClient Client, ConnectionState Old, ConnectionState New)
        {

            if (New == ConnectionState.Connected)
            {

                ConnectedAt          = DateTimeOffset.UtcNow;
                LastConnectionError  = null;

                // Whatever the roster holds at this point goes into the list;
                // whatever arrives later comes through the roster events.
                foreach (var item in Client.Roster.Items)
                {
                    Chats.SetContact (item.BareJid, item.Name, item.Subscription);
                    Chats.SetPresence(item.BareJid, item.Presence, item.PresenceStatus);
                }

                foreach (var pending in Client.PendingSubscriptions)
                    Chats.SetPendingRequest(pending.Bare, true);

                // Not awaited, and it must not be: this runs inside the
                // connection's own state change, and switching OMEMO on is two
                // round trips to the server. Whatever goes wrong in there is
                // handled in there - the connection is up either way.
                _ = SwitchOmemoOnAsync(Client);

            }

            else if (New == ConnectionState.Disconnected)
            {

                ConnectedAt = null;

                // Nobody is online as far as this side can tell any more.
                foreach (var item in Client.Roster.Items)
                    Chats.SetPresence(item.BareJid, PresenceState.Offline, null);

            }

            PublishConnection(Old, New);

        }

        #endregion


        #region (private) PublishConnection(Old, New) / PublishNotice(Level, Text)

        private void PublishConnection(ConnectionState  Old,
                                       ConnectionState  New)

            => Publish("connection",
                       Chats.Sequence,
                       new JObject(
                           new JProperty("previous",    Old.ToString().ToLowerInvariant()),
                           new JProperty("connection",  ConnectionJSON(New)),
                           new JProperty("account",     AccountJSON())
                       ));


        private void PublishNotice(String  Level,
                                   String  Text)

            => Publish("notice",
                       Chats.Sequence,
                       new JObject(
                           new JProperty("level",      Level),
                           new JProperty("text",       Text),
                           new JProperty("timestamp",  DateTimeOffset.UtcNow.ToString("o"))
                       ));

        #endregion

        #region (private) ConnectionJSON(State = null)

        /// <summary>
        /// The connection as the browser shows it in the corner. Without an
        /// account it is "unconfigured", which is how the page knows to open
        /// the account page instead of the chat.
        /// </summary>
        private JObject ConnectionJSON(ConnectionState? State = null)
        {

            var client = Client;

            if (client is null)
                return new JObject(
                           new JProperty("state",  "unconfigured"),
                           new JProperty("error",  LastConnectionError)
                       );

            var state = State ?? client.State;

            return new JObject(
                       new JProperty("state",             state.ToString().ToLowerInvariant()),
                       new JProperty("jid",               client.BareJid.ToString()),
                       new JProperty("fullJid",           state == ConnectionState.Connected ? client.FullJid.ToString() : null),
                       new JProperty("websocket",         WebSocketURI(client)),
                       new JProperty("carbons",           client.CarbonsEnabled),
                       new JProperty("streamManagement",  client.StreamManagement?.IsEnabled == true),
                       new JProperty("omemo",             OmemoJSON(client)),
                       new JProperty("connectedAt",       ConnectedAt?.ToString("o")),
                       new JProperty("error",             LastConnectionError)
                   );

        }

        private static String? WebSocketURI(XMPPClient Client)
        {
            try
            {
                return Client.WebSocketUri.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// XEP-0384: what this side can and cannot do with encryption.
        /// </summary>
        /// <remarks>
        /// Reading and writing are named separately although they are now the
        /// same answer, because they are different capabilities and one of them
        /// could stop working on its own. What this cannot say is whether a
        /// <i>particular</i> message will be encrypted - that depends on the
        /// devices of the far end and on the switch for that conversation, and
        /// it is answered per line rather than guessed at here.
        /// </remarks>
        private JObject OmemoJSON(XMPPClient Client)

            => new (
                   new JProperty("configured",   OmemoDirectory is not null),
                   new JProperty("receiving",    Client.OmemoEnabled),
                   new JProperty("sending",      Client.OmemoEnabled),
                   new JProperty("deviceId",     Client.Omemo?.Identity.DeviceId),
                   new JProperty("fingerprint",  Client.Omemo?.Fingerprint)
               );

        #endregion

        #region (private) AccountJSON()

        /// <summary>
        /// The account settings without the password, or null when none is
        /// configured yet. <see cref="AccountResponseJSON"/> in XMPPWebAPI.cs
        /// wraps it with the connection for the account routes.
        /// </summary>
        private JObject? AccountJSON()
            => Settings?.ToJSON(IncludePassword: false);

        #endregion

    }

}
