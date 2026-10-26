import { api, type Chat, type ContactAction, type Message } from '../api/client';
import { auth } from '../auth';
import { lineActions, quotation, showLine } from '../chat/lines';
import { renderBody } from '../chat/links';
import { store, type StoreEvent } from '../chat/store';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { errorMessage, excerpt, formatBytes, formatDay, formatListTime, formatTime, presenceLabel, preview, sameDay } from '../ui';

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
                        <a href="/rooms" class="btn small" title="Rooms (XEP-0045)">
                            <i class="fa-solid fa-hashtag"></i>
                        </a>
                        <a href="/account" class="btn small" title="XMPP account settings">
                            <i class="fa-solid fa-gear"></i>
                        </a>
                        <a href="/debug" class="btn small" title="XML debug: what goes over the wire">
                            <i class="fa-solid fa-code"></i>
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

                    <div id="composer-context" class="composer-context" hidden></div>

                    <form id="composer" class="composer">
                        <input id="file" type="file" hidden>
                        <button type="button" id="attach" class="btn" title="Send a file"><i class="fa-solid fa-paperclip"></i></button>
                        <textarea name="body" rows="1" placeholder="Write a message …" aria-label="Message"></textarea>
                        <button type="button" id="unsay" class="btn small danger" hidden
                                title="XEP-0424: take this line back. A request and not a deletion - the other side decides what to do with it, and the archive keeps its own copy.">take back</button>
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
    private readonly context:    HTMLElement;
    private readonly composer:   HTMLFormElement;
    private readonly fileInput:  HTMLInputElement;
    private readonly attach:     HTMLButtonElement;
    private readonly textarea:   HTMLTextAreaElement;
    private readonly sendButton: HTMLButtonElement;
    private readonly unsay:      HTMLButtonElement;
    private readonly toasts:     HTMLElement;

    private current:     string | null = null;

    /** XEP-0308/0424: the id of the line in the box, or null when it is new. */
    private editing:     string | null = null;

    /** What was in the box before an edit took it over, to be put back afterwards. */
    private draftBeforeEdit = '';

    /** XEP-0461: the line the box answers, or null. */
    private replyingTo:  Message | null = null;
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
        this.context     = must(element, '#composer-context');
        this.composer    = must<HTMLFormElement>(element, '#composer');
        this.fileInput   = must<HTMLInputElement>(element, '#file');
        this.attach      = must<HTMLButtonElement>(element, '#attach');
        this.textarea    = must<HTMLTextAreaElement>(this.composer, 'textarea');
        this.sendButton  = must<HTMLButtonElement>(this.composer, 'button[type="submit"]');
        this.unsay       = must<HTMLButtonElement>(this.composer, '#unsay');

        this.unsay.addEventListener('click', () => { void this.takeBack(); });
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

        // The button and the input are two elements because a styled file
        // input is not a thing a browser offers: the input stays hidden and the
        // button opens it.
        this.attach.addEventListener('click', () => this.fileInput.click());

        this.fileInput.addEventListener('change', () => {

            const file = this.fileInput.files?.[0];

            // Cleared either way, and before anything else: without it the same
            // file cannot be sent twice, because 'change' does not fire when the
            // value has not changed.
            this.fileInput.value = '';

            if (file !== undefined)
                void this.sendFile(file);

        });

        this.composer.addEventListener('submit', event => {
            event.preventDefault();
            void this.send();
        });

        this.textarea.addEventListener('keydown', event => {

            if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
                event.preventDefault();
                void this.send();
                return;
            }

            // XEP-0308: up-arrow in an empty box takes back the last line sent
            // here, to be said properly. Sending it then replaces that line
            // instead of adding one. The pencil on that line does the same.
            if (event.key === 'ArrowUp' && this.textarea.value.length === 0) {

                const mine = this.editableLine();

                if (mine !== null) {
                    event.preventDefault();
                    this.startEditing(mine);
                }

                return;

            }

            if (event.key === 'Escape') {
                if (this.editing !== null)
                    this.stopEditing();
                else if (this.replyingTo !== null)
                    this.stopReplying();
            }

        });

        // One listener for every line, because the lines are drawn again and
        // again: the buttons on a line, the quotation in it, and a tap that
        // shows the buttons on a screen nothing hovers over.
        this.messages.addEventListener('click', event => this.onMessageClick(event));

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

        // Both belong to the conversation they were started in: an edit sent
        // in the next one would replace a line there, and an answer would
        // point at a line that is not in it.
        if (this.editing !== null)
            this.stopEditing();

        if (this.replyingTo !== null)
            this.stopReplying();

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
                    ${avatar(chat.displayName, chat.avatar, chat.jid)}
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
            ${avatar(chat?.displayName ?? jid, chat?.avatar ?? null, jid)}
            <span class="dot ${chat?.presence ?? 'offline'}"></span>
            <div class="peer-info">
                <div class="peer-name">${chat?.displayName ?? jid}</div>
                <div class="peer-meta">${meta}</div>
            </div>
            <div class="peer-actions">
                ${encryptionButton(chat)}
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

        this.peer.querySelector<HTMLButtonElement>('button[data-encryption]')?.
             addEventListener('click', () => void this.encryption(jid, chat?.encryption === 'off'));

        for (const button of this.peer.querySelectorAll<HTMLButtonElement>('button[data-action]'))
            button.addEventListener('click', () => void this.contact(jid, button.dataset.action as ContactAction));

    }

    /**
     * Turn encryption off for this conversation, or back on.
     *
     * Asked for before turning it off, because the consequence outlives the
     * click: it is remembered, and every message to this contact from now on
     * goes in the clear until somebody turns it back on.
     */
    private async encryption(jid: string, enable: boolean): Promise<void> {

        if (!enable && !window.confirm(
                `Write to ${jid} without encryption from now on?\n\n` +
                'Everything you send here will be readable by their server and yours. ' +
                'This is remembered until you turn it back on.'))
        {
            return;
        }

        try {
            await api.chats.encryption(jid, enable);
        }
        catch (error) {
            this.toast('error', errorMessage(error));
        }

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

        this.markEditable();

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
            this.markEditable();
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
        this.markEditable();
        this.scrollIfAtBottom();

    }

    /**
     * XEP-0308: puts the pencil on the one line an edit would replace, and
     * takes it off whichever line had it before - a new line of ours moves
     * it, a line taken back loses it.
     */
    private markEditable(): void {

        const line = this.editableLine();

        for (const marked of this.messages.querySelectorAll('.message.editable'))
            marked.classList.remove('editable');

        if (line !== null)
            this.elements.get(line.id)?.classList.add('editable');

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

        // XEP-0461: what is being answered, above the answer. The body no
        // longer holds it, so leaving this out would lose the context entirely -
        // which would be worse than the duplicate it replaces.
        if (message.quote !== null && message.quote.length > 0 && !message.retracted) {

            const quoted = document.createElement('blockquote');
            quoted.className   = 'quote';

            // A click leads to the line it answers, when that line is known.
            if (message.repliesTo !== null) {
                quoted.classList.add('linked');
                quoted.title = 'Show the message this answers';
            }

            quoted.textContent = quotation(message.quote);

            element.appendChild(quoted);

        }

        element.appendChild(body);

        const meta = document.createElement('div');
        meta.className = 'meta';

        const parts: string[] = [];

        if (message.delayed)
            parts.push('delivered late');

        if (message.carbon)
            parts.push(message.direction === 'out' ? 'from another device' : 'seen on another device');

        if (message.corrected)
            parts.push('edited');

        // XEP-0424: the words are gone and the place is not. Said rather than
        // left to an empty line, which would read as a message that failed.
        if (message.retracted)
            parts.push('taken back');

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

        // A line taken back has nothing left to answer or to say again. Last,
        // so that where the buttons become a row of the line - on a phone -
        // that row is at its foot.
        if (!message.retracted)
            element.appendChild(lineActions({ reply: true, edit: message.direction === 'out' }));

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

    /**
     * XEP-0363: sends a file.
     *
     * Whether it travels encrypted is the server's decision and follows the
     * conversation - see SendFileToAsync. Saying so here would be a second
     * place to keep the same rule, and the two would drift.
     */
    private async sendFile(file: File): Promise<void> {

        const jid = this.current;

        if (jid === null)
            return;

        this.attach.disabled = true;

        try
        {
            await store.sendFile(jid, file);
            this.atBottom = true;
            this.scrollToBottom();
        }
        catch (error)
        {
            this.toast('error', errorMessage(error));
        }
        finally
        {
            this.attach.disabled = false;
            this.textarea.focus();
        }

    }

    /**
     * XEP-0308: the line an edit would replace, or null.
     *
     * The rule the server applies - the last line sent from here that still
     * stands, so not a carbon of another device and not a line taken back -
     * and then null for a file, whose text is its address. A line that went
     * encrypted is corrected encrypted; the server refuses only when that is
     * no longer possible, and says why.
     */
    private editableLine(): Message | null {

        if (this.current === null)
            return null;

        const lines = store.messages.get(this.current) ?? [];

        for (let i = lines.length - 1; i >= 0; i--) {

            const line = lines[i];

            if (line.direction !== 'out' || line.carbon || line.retracted)
                continue;

            return line.media !== null ? null : line;

        }

        return null;

    }

    private messageById(id: string): Message | null {

        if (this.current === null)
            return null;

        return (store.messages.get(this.current) ?? []).find(message => message.id === id) ?? null;

    }

    /** XEP-0308: puts a line into the box, to be said properly. */
    private startEditing(line: Message): void {

        if (this.replyingTo !== null)
            this.stopReplying();

        if (this.editing === null)
            this.draftBeforeEdit = this.textarea.value;

        this.editing        = line.id;
        this.textarea.value = line.body;
        this.composer.classList.add('editing');
        this.unsay.hidden   = false;

        this.renderContext();
        this.autosize();
        this.textarea.focus();

    }

    /** Ends an edit, and gives the box back what it held before. */
    private stopEditing(): void {
        this.editing         = null;
        this.textarea.value  = this.draftBeforeEdit;
        this.draftBeforeEdit = '';
        this.composer.classList.remove('editing');
        this.unsay.hidden    = true;
        this.renderContext();
        this.autosize();
    }

    /**
     * XEP-0461: the next line sent answers this one.
     *
     * What is in the box stays: whoever starts typing and then decides it is
     * an answer to something should not have to type it again.
     */
    private startReplying(line: Message): void {

        if (this.editing !== null)
            this.stopEditing();

        this.replyingTo = line;

        this.renderContext();
        this.textarea.focus();

    }

    private stopReplying(): void {
        this.replyingTo = null;
        this.renderContext();
    }

    /**
     * The strip above the box that says what sending will do: answer a line,
     * or replace one. Without it the box looks the same either way, and
     * Enter does something different from what it seems to.
     */
    private renderContext(): void {

        const editing  = this.editing !== null;
        const line     = editing ? this.messageById(this.editing!) : this.replyingTo;

        this.composer.classList.toggle('replying', !editing && line !== null);

        if (line === null) {
            this.context.hidden = true;
            this.context.replaceChildren();
            return;
        }

        const chat  = this.current !== null ? store.chats.get(this.current) : undefined;
        const who   = line.direction === 'out' ? 'yourself' : (chat?.displayName ?? line.chat);

        render(this.context, html`
            <i class="fa-solid ${editing ? 'fa-pen' : 'fa-reply'}" aria-hidden="true"></i>
            <div class="context-text">
                <div class="context-label">${editing ? 'Editing your message' : `Replying to ${who}`}</div>
                <div class="context-line">${excerpt(line)}</div>
            </div>
            <button type="button" class="btn small" title="Cancel (Esc)" aria-label="Cancel"><i class="fa-solid fa-xmark"></i></button>
        `);

        this.context.hidden = false;

        must<HTMLButtonElement>(this.context, 'button').addEventListener('click', () => {
            if (editing)
                this.stopEditing();
            else
                this.stopReplying();
            this.textarea.focus();
        });

    }

    private onMessageClick(event: MouseEvent): void {

        if (!(event.target instanceof Element))
            return;

        const target   = event.target;
        const element  = target.closest<HTMLElement>('article.message');
        const message  = element?.dataset.id !== undefined ? this.messageById(element.dataset.id) : null;

        if (element === null || message === null)
            return;

        switch (target.closest<HTMLButtonElement>('.actions button')?.dataset.action) {

            case 'reply':
                element.classList.remove('active');
                this.startReplying(message);
                return;

            case 'edit':
                element.classList.remove('active');
                this.startEditing(message);
                return;

        }

        if (message.repliesTo !== null && target.closest('.quote') !== null) {
            this.showAnswered(message.repliesTo);
            return;
        }

        // Links, players and a selection in progress keep their click.
        if (target.closest('a, button, audio, video') !== null || window.getSelection()?.isCollapsed === false)
            return;

        // A tap picks the line, so its buttons show where nothing hovers; a
        // second tap, or a tap on another line, puts them away again.
        const picked = element.classList.contains('active');

        for (const active of this.messages.querySelectorAll('.message.active'))
            active.classList.remove('active');

        if (!picked)
            element.classList.add('active');

    }

    /** XEP-0461: the line an answer is about, brought into view. */
    private showAnswered(id: string): void {

        const element = this.elements.get(id);

        if (element === undefined) {
            this.toast('info', 'The message this answers is further back than what is loaded here.');
            return;
        }

        showLine(element);

    }

    /**
     * XEP-0424: takes the line being edited back instead of saying it again.
     *
     * A request and not a deletion, which is why it is a button and not a
     * keystroke: what the far side does with it is theirs to decide, the
     * archive keeps its own copy, and whoever was reading has already read.
     */
    private async takeBack(): Promise<void> {

        const jid  = this.current;
        const id   = this.editing;

        if (jid === null || id === null)
            return;

        this.stopEditing();

        try {
            await store.retract(jid, id);
        }
        catch (error) {
            this.toast('error', errorMessage(error));
        }

    }

    private async send(): Promise<void> {

        const jid      = this.current;
        const body     = this.textarea.value.trimEnd();
        const editing  = this.editing !== null;
        const answers  = this.replyingTo;

        if (jid === null || body.trim().length === 0)
            return;

        this.sendButton.disabled = true;

        try
        {

            this.stopTyping();

            if (editing) {

                await store.correct(jid, body);

                // The line stays where it was, so the view does too - and
                // what was in the box before the edit comes back.
                this.stopEditing();

            }
            else {

                await store.send(jid, body, answers?.id);

                if (this.replyingTo === answers)
                    this.stopReplying();

                this.textarea.value = '';
                this.drafts.delete(jid);
                this.autosize();
                this.atBottom = true;
                this.scrollToBottom();

            }

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


/**
 * XEP-0084: somebody's face, or the initials that stand in for one.
 *
 * The initials are not a placeholder for a picture that is coming: most people
 * in most rosters have never published one, and a row of identical grey
 * silhouettes is a list nobody can scan. Two letters of the name at least
 * differ from each other.
 *
 * `loading="lazy"` because a long roster is a long list of requests otherwise,
 * and `decoding="async"` so a picture that turns out to be malformed cannot
 * hold up the row it is in. Neither the name nor the id reaches innerHTML
 * unescaped - html`` escapes both, and the id is forty hex digits or the route
 * refuses it.
 */
function avatar(name: string, id: string | null, jid: string): HTMLFragment {

    if (id !== null)
        return html`<img class="avatar" src="${api.avatarURL(id)}" alt="" loading="lazy" decoding="async" title="${jid}">`;

    return html`<span class="avatar initials" aria-hidden="true">${initials(name)}</span>`;

}

/** One or two letters to stand in for a face. */
function initials(name: string): string {

    // The JID of somebody without a name starts with the local part, which is
    // what anybody would read them by, so taking the front of the string works
    // for both. Split on the separators a name uses and a JID does not.
    const parts = name.split(/[\s._-]+/u).filter(part => part.length > 0);

    if (parts.length === 0)
        return '?';

    const first = Array.from(parts[0])[0] ?? '';
    const last  = parts.length > 1 ? Array.from(parts[parts.length - 1])[0] ?? '' : '';

    return (first + last).toUpperCase();

}


/**
 * The lock in the conversation header.
 *
 * Shown only when this app does OMEMO at all - without it the button would
 * offer to turn off something that is not on. It says what the conversation
 * does rather than what one message did: the lock on a message line is the
 * record of that line, this is the setting.
 */
function encryptionButton(chat: Chat | undefined): HTMLFragment {

    if (chat === undefined || store.connection?.omemo?.configured !== true)
        return html``;

    const off = chat.encryption === 'off';

    return html`
        <button type="button"
                class="btn small encryption ${off ? 'off' : 'auto'}"
                data-encryption="${chat.encryption}"
                title="${off
                            ? 'Encryption is off for this conversation - everything sent here goes in the clear. Click to turn it back on.'
                            : 'Encrypted whenever they can read it; a lock on a line means that line went encrypted. Click to turn encryption off for this conversation.'}">
            <i class="fa-solid ${off ? 'fa-lock-open' : 'fa-lock'}"></i>
        </button>
    `;

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
