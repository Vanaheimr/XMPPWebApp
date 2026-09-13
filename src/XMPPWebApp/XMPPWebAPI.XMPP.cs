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

            Chats.AddIncoming(
                Message.FromBareJid,
                Message.From.ToString(),
                Message.MessageId,
                Message.Body,
                new DateTimeOffset(Message.Timestamp),
                Delayed:   Message.IsDelayed,
                Corrects:  Message.ReplacesId
            );

        }

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
