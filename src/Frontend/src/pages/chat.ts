import { api, type Chat, type ContactAction, type Message } from '../api/client';
import { auth } from '../auth';
import { renderBody } from '../chat/links';
import { store, type StoreEvent } from '../chat/store';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { errorMessage, formatBytes, formatDay, formatListTime, formatTime, presenceLabel, preview, sameDay } from '../ui';

// The chat page: the list of conversations on the left, the open conversation
// on the right, the composer at the bottom. Everything shown here comes from
// the store; this file only turns it into DOM nodes and sends what the user
// does back.
//
// Nothing that came over the wire is ever put into innerHTML. Names, status
// texts and message bodies go through html`...` (which escapes) or through
// textContent; a body becomes a picture or a link only by the rules in
// chat/links.ts, which build DOM nodes and set validated URLs as properties.

const TITLE            = 'XMPP WebApp';
const TYPING_PAUSE_MS  = 4000;
const TOAST_MS         = 8000;

/** How close to the top a scroll comes before the next page of the past is asked for. */
const HISTORY_MARGIN   = 200;

export const chatPage: Page = {

    title: 'Chats',

    render({ root, params }) {

        store.start();

        render(root, html`
            <div id="chat" class="chat">

                <aside class="sidebar">

                    <header class="me">
                        <div class="me-info">
                            <div id="me-jid" class="me-jid" title="The XMPP account of this web app"></div>
                            <div id="me-state" class="me-state"></div>
                        </div>
                        <a href="/account" class="btn small" title="XMPP account settings">
                            <i class="fa-solid fa-gear"></i>
                        </a>
                        <button type="button" id="sign-out" class="btn small" title="Sign out of the web page">
                            <i class="fa-solid fa-right-from-bracket"></i>
                        </button>
                    </header>

                    <form id="new-chat" class="new-chat" autocomplete="off">
                        <input name="jid" placeholder="Chat with user@example.org" aria-label="Start a chat with this JID" />
                        <button type="submit" class="btn small" title="Start a chat"><i class="fa-solid fa-plus"></i></button>
                    </form>

                    <ul id="chat-list" class="chat-list"></ul>

                </aside>

                <main class="conversation">

                    <header id="peer" class="peer"></header>

                    <div id="banner" class="banner"></div>

                    <div id="messages" class="messages" aria-live="polite"></div>

                    <form id="composer" class="composer">
                        <textarea name="body" rows="1" placeholder="Write a message …" aria-label="Message"></textarea>
                        <button type="submit" class="btn primary" title="Send (Enter)"><i class="fa-solid fa-paper-plane"></i></button>
                    </form>

                </main>

                <div id="toasts" class="toasts" aria-live="polite"></div>

            </div>
        `);

        const view = new ChatView(must<HTMLElement>(root, '#chat'));

        view.select(params.jid ?? null, false);

        return () => view.destroy();

    }

};


class ChatView {

    private readonly meJid:      HTMLElement;
    private readonly meState:    HTMLElement;
    private readonly list:       HTMLElement;
    private readonly peer:       HTMLElement;
    private readonly banner:     HTMLElement;
    private readonly messages:   HTMLElement;
    private readonly composer:   HTMLFormElement;
    private readonly textarea:   HTMLTextAreaElement;
    private readonly sendButton: HTMLButtonElement;
    private readonly toasts:     HTMLElement;

    private current:     string | null = null;
    private elements     = new Map<string, HTMLElement>();
    private lastRendered: Message | null = null;
    private atBottom     = true;
    private composing    = false;
    private typingTimer:  number | null = null;
    private loadingOlder = false;
    private history:      HTMLElement | null = null;
    private readonly drafts = new Map<string, string>();

    private readonly unsubscribe: () => void;
    private readonly onVisibility = () => this.onVisible();
    private readonly onPopState   = () => this.select(jidFromURL(), false);

    constructor(private readonly element: HTMLElement) {

        this.meJid       = must(element, '#me-jid');
        this.meState     = must(element, '#me-state');
        this.list        = must(element, '#chat-list');
        this.peer        = must(element, '#peer');
        this.banner      = must(element, '#banner');
        this.messages    = must(element, '#messages');
        this.composer    = must<HTMLFormElement>(element, '#composer');
        this.textarea    = must<HTMLTextAreaElement>(this.composer, 'textarea');
        this.sendButton  = must<HTMLButtonElement>(this.composer, 'button[type="submit"]');
        this.toasts      = must(element, '#toasts');

        this.unsubscribe = store.onChange(event => this.onStoreEvent(event));

        must<HTMLButtonElement>(element, '#sign-out').addEventListener('click', () => {
            void auth.signOut();
        });

        must<HTMLFormElement>(element, '#new-chat').addEventListener('submit', event => {
            event.preventDefault();
            void this.startChat(event.currentTarget as HTMLFormElement);
        });

        // Chat entries are links (deep links work, middle-click works), but a
        // plain click only swaps the conversation instead of re-rendering the
        // page: preventDefault() here keeps the router out of it.
        this.list.addEventListener('click', event => {

            if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)
                return;

            const anchor = (event.target as Element | null)?.closest<HTMLAnchorElement>('a.chat-item');

            if (!anchor?.dataset.jid)
                return;

            event.preventDefault();
            this.select(anchor.dataset.jid, true);

        });

        this.composer.addEventListener('submit', event => {
            event.preventDefault();
            void this.send();
        });

        this.textarea.addEventListener('keydown', event => {
            if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
                event.preventDefault();
                void this.send();
            }
        });

        this.textarea.addEventListener('input', () => {
            this.autosize();
            this.typing();
        });

        this.messages.addEventListener('scroll', () => {

            this.atBottom = this.messages.scrollHeight - this.messages.scrollTop - this.messages.clientHeight < 48;

            // Scrolling up to the top is the ordinary way of asking for the
            // past; the button at the top is for a conversation too short to
            // scroll at all.
            if (this.messages.scrollTop < HISTORY_MARGIN)
                void this.loadOlder();

        });

        document.addEventListener('visibilitychange', this.onVisibility);
        window.addEventListener('popstate', this.onPopState);

        this.renderMe();
        this.renderConnection();
        this.renderList();

    }

    destroy(): void {

        this.unsubscribe();
        document.removeEventListener('visibilitychange', this.onVisibility);
        window.removeEventListener('popstate', this.onPopState);

        if (this.typingTimer !== null)
            window.clearTimeout(this.typingTimer);

        if (this.current !== null)
            this.drafts.set(this.current, this.textarea.value);

    }


    // Selecting a conversation

    select(jid: string | null, push: boolean): void {

        if (jid === this.current && this.elements.size > 0)
            return;

        if (this.current !== null) {
            this.drafts.set(this.current, this.textarea.value);
            this.stopTyping();
        }

        this.current       = jid;
        this.loadingOlder  = false;

        const path = jid === null ? '/' : `/chats/${encodeURIComponent(jid)}`;

        if (push && location.pathname !== path)
            history.pushState(null, '', path);

        this.element.classList.toggle('open', jid !== null);
        this.textarea.value = jid !== null ? (this.drafts.get(jid) ?? '') : '';
        this.autosize();

        this.renderPeer();
        this.renderList();
        this.renderTitle();

        if (jid === null) {
            this.renderEmpty('Pick a chat on the left, or start one with a JID.');
            return;
        }

        if (store.isLoaded(jid)) {
            this.renderMessages();
        }
        else {
            this.renderEmpty('Loading …');
            store.loadMessages(jid).catch((error: unknown) => {
                if (this.current === jid)
                    this.renderEmpty(`Could not load this chat: ${errorMessage(error)}`);
            });
        }

        if (document.visibilityState === 'visible')
            this.textarea.focus();

    }

    private async startChat(form: HTMLFormElement): Promise<void> {

        const input = must<HTMLInputElement>(form, 'input[name="jid"]');
        const jid   = input.value.trim();

        if (jid.length === 0)
            return;

        if (!/^[^@/\s]+@[^@/\s]+$/.test(jid)) {
            this.toast('error', 'A JID looks like user@example.org.');
            return;
        }

        try
        {
            const chat = await store.open(jid);
            input.value = '';
            this.select(chat.jid, true);
        }
        catch (error)
        {
            this.toast('error', errorMessage(error));
        }

    }


    // Store events

    private onStoreEvent(event: StoreEvent): void {

        switch (event.type) {

            case 'chats':
                this.renderList();
                this.renderPeer();
                this.renderTitle();
                break;

            case 'messages':
                if (event.jid === this.current)
                    this.renderMessages();
                break;

            case 'message':
                if (event.jid === this.current) {
                    this.upsertMessage(event.message);
                    if (event.message.direction === 'in' && document.visibilityState === 'visible')
                        store.markRead(event.jid);
                }
                break;

            case 'connection':
                this.renderMe();
                this.renderConnection();
                break;

            case 'stream':
                this.renderMe();
                break;

            case 'notice':
                this.toast(event.level, event.text);
                break;

        }

    }

    private onVisible(): void {
        if (document.visibilityState === 'visible' && this.current !== null)
            store.markRead(this.current);
    }


    // Rendering: the account and the connection

    private renderMe(): void {

        const connection = store.connection;

        this.meJid.textContent = connection?.jid ?? auth.user?.user.id ?? '';

        const state = connection?.state ?? 'connecting';
        const label = !store.streamConnected ? 'page offline, reconnecting …'
                    : state === 'connected'  ? 'connected'
                    : state === 'connecting' ? 'connecting …'
                    : state === 'reconnecting' ? 'connection lost, reconnecting …'
                    :                          'disconnected';

        render(this.meState, html`<span class="dot ${state === 'connected' && store.streamConnected ? 'available' : 'offline'}"></span> ${label}`);

    }

    private renderConnection(): void {

        const connection = store.connection;

        if (connection === null || connection.state === 'connected') {
            this.banner.replaceChildren();
            this.banner.className = 'banner';
            return;
        }

        const failed = connection.state === 'disconnected';

        render(this.banner, html`
            <i class="fa-solid ${failed ? 'fa-triangle-exclamation' : 'fa-rotate'}"></i>
            <span class="banner-text">
                ${connection.state === 'connecting'   ? html`Connecting to the XMPP server as <strong>${connection.jid}</strong> …`
                : connection.state === 'reconnecting' ? html`Connection lost, reconnecting …`
                : connection.error                    ? html`Not connected: ${connection.error}`
                :                                       html`Not connected to the XMPP server.`}
            </span>
            ${failed ? html`<button type="button" id="reconnect" class="btn small">Reconnect</button>` : ''}
        `);

        this.banner.className = failed ? 'banner error' : 'banner';

        this.banner.querySelector<HTMLButtonElement>('#reconnect')?.addEventListener('click', event => {
            (event.currentTarget as HTMLButtonElement).disabled = true;
            store.reconnect().catch((error: unknown) => this.toast('error', errorMessage(error)));
        });

    }

    private renderTitle(): void {

        const unread  = store.unread;
        const chat    = this.current !== null ? store.chats.get(this.current) : undefined;
        const name    = chat?.displayName ?? (this.current ?? '');

        document.title = (unread > 0 ? `(${unread}) ` : '') + (name ? `${name} · ${TITLE}` : TITLE);

    }


    // Rendering: the list

    private renderList(): void {

        const chats = store.sortedChats();

        if (chats.length === 0) {
            render(this.list, html`<li class="chat-empty muted">No chats yet. Contacts appear here once the account is connected.</li>`);
            return;
        }

        render(this.list, html`${chats.map(chat => html`
            <li>
                <a class="chat-item ${chat.jid === this.current ? 'active' : ''}"
                   href="/chats/${encodeURIComponent(chat.jid)}"
                   data-jid="${chat.jid}"
                   title="${chat.jid}${chat.status ? ' – ' + chat.status : ''}">
                    <span class="dot ${chat.presence}" title="${presenceLabel(chat.presence)}"></span>
                    <span class="chat-name">${chat.displayName}</span>
                    <span class="chat-time">${formatListTime(chat.lastActivity)}</span>
                    <span class="chat-preview ${chat.chatState === 'composing' ? 'typing' : ''}">
                        ${chat.chatState === 'composing' ? 'typing …'
                        : chat.pendingRequest              ? 'wants to add you as a contact'
                        :                                    preview(chat.lastMessage)}
                    </span>
                    ${chat.unread > 0 ? html`<span class="chat-unread">${chat.unread}</span>` : ''}
                </a>
            </li>
        `)}`);

    }


    // Rendering: the open conversation

    private renderPeer(): void {

        if (this.current === null) {
            render(this.peer, html`<div class="peer-info"><div class="peer-name muted">No chat selected</div></div>`);
            return;
        }

        const jid   = this.current;
        const chat  = store.chats.get(jid);

        const meta  = chat === undefined            ? jid
                    : chat.chatState === 'composing' ? `${jid} · typing …`
                    : chat.chatState === 'paused'    ? `${jid} · stopped typing`
                    : chat.status                    ? `${jid} · ${presenceLabel(chat.presence)} – ${chat.status}`
                    :                                  `${jid} · ${presenceLabel(chat.presence)}`;

        render(this.peer, html`
            <button type="button" class="btn small back" title="Back to the list"><i class="fa-solid fa-arrow-left"></i></button>
            <span class="dot ${chat?.presence ?? 'offline'}"></span>
            <div class="peer-info">
                <div class="peer-name">${chat?.displayName ?? jid}</div>
                <div class="peer-meta">${meta}</div>
            </div>
            <div class="peer-actions">
                ${chat?.pendingRequest ? html`
                    <span class="muted small">Contact request</span>
                    <button type="button" class="btn small primary" data-action="accept">Accept</button>
                    <button type="button" class="btn small" data-action="deny">Deny</button>
                ` : chat !== undefined && !chat.inRoster ? html`
                    <button type="button" class="btn small" data-action="add" title="Ask to see each other's presence"><i class="fa-solid fa-user-plus"></i> Add contact</button>
                ` : chat !== undefined ? html`
                    <span class="muted small" title="Subscription: ${chat.subscription}">contact</span>
                    <button type="button" class="btn small danger" data-action="remove" title="Remove from the contacts"><i class="fa-solid fa-user-minus"></i></button>
                ` : ''}
            </div>
        `);

        this.peer.querySelector<HTMLButtonElement>('.back')?.addEventListener('click', () => this.select(null, true));

        for (const button of this.peer.querySelectorAll<HTMLButtonElement>('button[data-action]'))
            button.addEventListener('click', () => void this.contact(jid, button.dataset.action as ContactAction));

    }

    private async contact(jid: string, action: ContactAction): Promise<void> {

        if (action === 'remove' && !window.confirm(`Remove ${jid} from the contacts?`))
            return;

        try
        {
            await store.contact(jid, action);
        }
        catch (error)
        {
            this.toast('error', errorMessage(error));
        }

    }

    private renderEmpty(text: string): void {
        this.elements.clear();
        this.lastRendered  = null;
        this.history       = null;
        render(this.messages, html`<p class="empty">${text}</p>`);
    }

    /**
     * Draws the whole conversation again.
     *
     * @param keepPosition after older messages were put in front: the view
     * moves down by exactly what they added, so that the line somebody was
     * reading stays under their eyes instead of jumping to the bottom.
     */
    private renderMessages(keepPosition = false): void {

        const jid   = this.current;
        const list  = jid !== null ? store.messages.get(jid) : undefined;

        const previousHeight  = this.messages.scrollHeight;
        const previousTop     = this.messages.scrollTop;

        this.elements.clear();
        this.lastRendered  = null;
        this.history       = null;
        this.messages.replaceChildren();

        if (jid === null || list === undefined)
            return;

        if (list.length === 0) {
            render(this.messages, html`<p class="empty">Nothing has been said here yet.</p>`);
            return;
        }

        if (store.hasOlder(jid))
            this.messages.appendChild(this.historyHeader());

        for (const message of list)
            this.append(message);

        if (keepPosition)
            this.messages.scrollTop = previousTop + (this.messages.scrollHeight - previousHeight);

        else {
            this.atBottom = true;
            this.scrollToBottom();
        }

    }

    /** The line above the oldest message: what is still in the archive, a click away. */
    private historyHeader(): HTMLElement {

        const element      = document.createElement('div');
        element.className  = 'history';

        if (this.loadingOlder)
            element.textContent = 'Loading older messages …';

        else {

            const button        = document.createElement('button');
            button.type         = 'button';
            button.className    = 'btn small';
            button.textContent  = 'Load older messages';

            button.addEventListener('click', () => void this.loadOlder());

            element.appendChild(button);

        }

        this.history = element;

        return element;

    }

    /** The next page of the past, from the archive on the server. */
    private async loadOlder(): Promise<void> {

        const jid = this.current;

        if (jid === null || this.loadingOlder || !store.hasOlder(jid))
            return;

        this.loadingOlder = true;

        // In place rather than through a re-render: the header keeps its
        // height, so the conversation does not move under a reader who is
        // looking at the top of it.
        if (this.history !== null)
            this.history.replaceChildren(document.createTextNode('Loading older messages …'));

        try
        {

            const added = await store.loadOlder(jid);

            this.loadingOlder = false;

            if (this.current === jid)
                this.renderMessages(added > 0);

        }
        catch (error)
        {

            this.loadingOlder = false;

            this.toast('error', `Could not load older messages: ${errorMessage(error)}`);

            // Nothing was added, so this only puts the header back where it
            // was - with the button on it, to try again.
            if (this.current === jid)
                this.renderMessages(true);

        }

    }

    private upsertMessage(message: Message): void {

        const existing = this.elements.get(message.id);

        if (existing !== undefined) {
            const replacement = this.messageElement(message);
            existing.replaceWith(replacement);
            this.elements.set(message.id, replacement);
            return;
        }

        // Older than the last line on screen (a message handed in late, or a
        // resumed stream): the cheap way is to draw the whole chat again.
        if (this.lastRendered !== null && Date.parse(message.timestamp) < Date.parse(this.lastRendered.timestamp)) {
            this.renderMessages();
            return;
        }

        if (this.messages.querySelector('.empty'))
            this.messages.replaceChildren();

        this.append(message);
        this.scrollIfAtBottom();

    }

    private append(message: Message): void {

        if (this.lastRendered === null || !sameDay(new Date(this.lastRendered.timestamp), new Date(message.timestamp))) {
            const day = document.createElement('div');
            day.className    = 'day';
            day.textContent  = formatDay(message.timestamp);
            this.messages.appendChild(day);
        }

        const element = this.messageElement(message);

        this.messages.appendChild(element);
        this.elements.set(message.id, element);
        this.lastRendered = message;

    }

    private messageElement(message: Message): HTMLElement {

        const element = document.createElement('article');
        element.className   = `message ${message.direction}`;
        element.dataset.id  = message.id;

        // The copy in the archive when there is one: it is still there when the
        // upload has expired, it works for an aesgcm:// URL the browser cannot
        // open at all, and fetching it tells no outside host who is reading.
        const body = renderBody(
                         message.body,
                         message.media === null
                             ? null
                             : {
                                   url:          api.mediaURL(message.chat, message.media.name),
                                   contentType:  message.media.contentType,
                                   name:         message.media.name,
                                   source:       message.media.source
                               }
                     );

        for (const img of body.querySelectorAll('img'))
            img.addEventListener('load', () => this.scrollIfAtBottom(), { once: true });

        for (const player of body.querySelectorAll('video, audio'))
            player.addEventListener('loadedmetadata', () => this.scrollIfAtBottom(), { once: true });

        element.appendChild(body);

        // The one thing blind trust leaves to notice, and it is not a detail
        // for the grey line below: the same device wrote before, with another
        // key. Not a reinstall - that comes back under a new device number and
        // reads as new - but the same device with a different identity.
        //
        // No message carries this yet: the library drops such a message instead
        // of decrypting it, so nothing arrives to be marked. It is rendered
        // because an archive from a later version can hold it, and because this
        // is where it will land when the library learns to say so.
        if (message.identity === 'changed') {
            const warning = document.createElement('div');
            warning.className    = 'key-changed';
            warning.textContent  = 'This device\u2019s key is not the one it used before.';
            warning.title        = 'Either they reinstalled their client, or somebody else is writing as them. ' +
                                   'There is no way to tell from here - ask them through another channel.';
            element.appendChild(warning);
        }

        const meta = document.createElement('div');
        meta.className = 'meta';

        const parts: string[] = [];

        if (message.delayed)
            parts.push('delivered late');

        if (message.carbon)
            parts.push(message.direction === 'out' ? 'from another device' : 'seen on another device');

        if (message.corrected)
            parts.push('edited');

        // Per message, because that is where it is true. This app reads
        // encrypted and sends in the clear, so a lock on the conversation would
        // be wrong for half of the lines in it.
        if (message.encrypted)
            parts.push('\u{1F512} encrypted');

        if (message.media !== null)
            parts.push(formatBytes(message.media.size));

        parts.push(formatTime(message.timestamp));

        if (message.direction === 'out')
            parts.push(message.displayed ? '✓✓ read' : message.delivered ? '✓✓' : '✓');

        meta.textContent = parts.join(' · ');
        meta.title       = new Date(message.timestamp).toLocaleString() + (message.direction === 'in' ? `\nfrom ${message.from}` : '');

        element.appendChild(meta);

        return element;

    }

    private scrollToBottom(): void {
        this.messages.scrollTop = this.messages.scrollHeight;
    }

    private scrollIfAtBottom(): void {
        if (this.atBottom)
            this.scrollToBottom();
    }


    // Sending

    private async send(): Promise<void> {

        const jid   = this.current;
        const body  = this.textarea.value.trimEnd();

        if (jid === null || body.trim().length === 0)
            return;

        this.sendButton.disabled = true;

        try
        {
            this.stopTyping();
            await store.send(jid, body);
            this.textarea.value = '';
            this.drafts.delete(jid);
            this.autosize();
            this.atBottom = true;
            this.scrollToBottom();
        }
        catch (error)
        {
            this.toast('error', errorMessage(error));
        }
        finally
        {
            this.sendButton.disabled = false;
            this.textarea.focus();
        }

    }

    private autosize(): void {
        this.textarea.style.height = 'auto';
        this.textarea.style.height = `${Math.min(this.textarea.scrollHeight, 160)}px`;
    }

    /** XEP-0085: "composing" once when the typing starts, "paused" after a while without a keystroke. */
    private typing(): void {

        const jid = this.current;

        if (jid === null)
            return;

        if (this.textarea.value.length === 0) {
            this.stopTyping();
            return;
        }

        if (!this.composing) {
            this.composing = true;
            store.sendState(jid, 'composing');
        }

        if (this.typingTimer !== null)
            window.clearTimeout(this.typingTimer);

        this.typingTimer = window.setTimeout(() => {
            this.typingTimer = null;
            if (this.composing) {
                this.composing = false;
                store.sendState(jid, 'paused');
            }
        }, TYPING_PAUSE_MS);

    }

    private stopTyping(): void {

        if (this.typingTimer !== null) {
            window.clearTimeout(this.typingTimer);
            this.typingTimer = null;
        }

        this.composing = false;

    }


    // Notices

    private toast(level: 'info' | 'warning' | 'error', text: string): void {

        const toast = document.createElement('div');
        toast.className    = `toast ${level}`;
        toast.textContent  = text;

        toast.addEventListener('click', () => toast.remove());

        this.toasts.appendChild(toast);

        window.setTimeout(() => toast.remove(), TOAST_MS);

    }

}


function jidFromURL(): string | null {

    const match = /^\/chats\/([^/]+)$/.exec(location.pathname);

    if (match === null)
        return null;

    try {
        return decodeURIComponent(match[1]);
    }
    catch {
        return null;
    }

}
