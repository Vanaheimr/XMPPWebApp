import { type Room, type RoomMessage } from '../api/client';
import { renderBody } from '../chat/links';
import { store, type StoreEvent } from '../chat/store';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { lock, occupantRow, roomIcon } from '../rooms/parts';
import { roomPreview } from '../rooms/text';
import { errorMessage, formatDay, formatListTime, formatTime, sameDay } from '../ui';

// XEP-0045: the room view.
//
// A page of its own and not a mode of the chat page, which is the same split
// the server makes and the library made in D116. A room looks like a
// conversation and none of the rules underneath are the same: who is there is a
// list rather than a state, nobody in it is a contact, receipts and markers
// mean nothing, and whether it can be encrypted is a property of the room
// rather than of the far end.
//
// So: a third column for the occupants, which a conversation has no use for,
// and a header that says what the room is rather than how somebody is.
//
// Nothing that came over the wire is ever put into innerHTML - the same rule as
// the chat page, and it matters more here, because a nickname is chosen by
// whoever walked in.

const TITLE       = 'Rooms · XMPP WebApp';
const TOAST_MS    = 8000;


export const roomsPage: Page = {

    title: 'Rooms',

    render({ root, params }) {

        store.start();

        render(root, html`
            <div id="rooms" class="chat rooms">

                <aside class="sidebar">

                    <header class="me">
                        <div class="me-info">
                            <div class="me-jid">Rooms</div>
                            <div id="me-state" class="me-state"></div>
                        </div>
                        <a href="/" class="btn small" title="Back to the conversations">
                            <i class="fa-solid fa-comments"></i>
                        </a>
                        <a href="/account" class="btn small" title="XMPP account settings">
                            <i class="fa-solid fa-gear"></i>
                        </a>
                    </header>

                    <form id="join" class="new-chat" autocomplete="off">
                        <input name="jid" placeholder="Join room@conference.example.org" aria-label="Join this room" />
                        <button type="submit" class="btn small" title="Enter the room"><i class="fa-solid fa-plus"></i></button>
                    </form>

                    <ul id="room-list" class="chat-list"></ul>

                </aside>

                <main class="conversation">

                    <header id="head" class="peer"></header>

                    <div id="banner" class="banner"></div>

                    <div id="messages" class="messages" aria-live="polite"></div>

                    <form id="composer" class="composer">
                        <textarea name="body" rows="1" placeholder="Say something …" aria-label="Message"></textarea>
                        <button type="submit" class="btn primary" title="Send (Enter)"><i class="fa-solid fa-paper-plane"></i></button>
                    </form>

                </main>

                <aside id="occupants" class="occupants"></aside>

                <div id="toasts" class="toasts" aria-live="polite"></div>

            </div>
        `);

        const view = new RoomView(must<HTMLElement>(root, '#rooms'));

        view.select(params.jid ?? null);

        return () => view.destroy();

    }

};


class RoomView {

    private readonly state:      HTMLElement;
    private readonly list:       HTMLElement;
    private readonly head:       HTMLElement;
    private readonly banner:     HTMLElement;
    private readonly messages:   HTMLElement;
    private readonly occupants:  HTMLElement;
    private readonly composer:   HTMLFormElement;
    private readonly textarea:   HTMLTextAreaElement;
    private readonly toasts:     HTMLElement;

    private readonly unsubscribe: () => void;

    private current:  string | null = null;
    private drawn:    string | null = null;

    constructor(private readonly root: HTMLElement) {

        this.state      = must<HTMLElement>(root, '#me-state');
        this.list       = must<HTMLElement>(root, '#room-list');
        this.head       = must<HTMLElement>(root, '#head');
        this.banner     = must<HTMLElement>(root, '#banner');
        this.messages   = must<HTMLElement>(root, '#messages');
        this.occupants  = must<HTMLElement>(root, '#occupants');
        this.composer   = must<HTMLFormElement>(root, '#composer');
        this.textarea   = must<HTMLTextAreaElement>(this.composer, 'textarea');
        this.toasts     = must<HTMLElement>(root, '#toasts');

        this.unsubscribe = store.onChange(event => this.onStore(event));

        must<HTMLFormElement>(root, '#join').addEventListener('submit', event => {
            event.preventDefault();
            void this.join(new FormData(event.target as HTMLFormElement).get('jid') as string);
        });

        this.composer.addEventListener('submit', event => {
            event.preventDefault();
            void this.send();
        });

        this.textarea.addEventListener('keydown', event => {
            if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                void this.send();
            }
        });

        this.list.addEventListener('click', event => {

            const anchor = (event.target as Element | null)?.closest<HTMLAnchorElement>('a.chat-item');

            if (anchor === null || anchor === undefined)
                return;

            event.preventDefault();
            history.pushState(null, '', anchor.getAttribute('href') ?? '/rooms');
            this.select(anchor.dataset.jid ?? null);

        });

        void store.loadRooms().catch((error: unknown) => this.toast('error', errorMessage(error)));

        this.renderConnection();

    }

    destroy(): void {
        this.unsubscribe();
    }


    select(jid: string | null): void {

        this.current = jid;
        this.drawn   = null;

        document.title = TITLE;

        this.renderList();
        this.renderHead();
        this.renderOccupants();

        if (jid === null) {
            render(this.messages, html`<p class="empty">Pick a room, or enter one above.</p>`);
            this.composer.hidden = true;
            return;
        }

        this.composer.hidden = false;

        if (store.isRoomLoaded(jid))
            this.renderMessages();
        else {
            render(this.messages, html`<p class="empty">Loading …</p>`);
            void store.loadRoomMessages(jid).catch((error: unknown) => {
                render(this.messages, html`<p class="empty">${errorMessage(error)}</p>`);
            });
        }

        store.markRoomRead(jid);

    }


    private onStore(event: StoreEvent): void {

        switch (event.type) {

            case 'rooms':
                this.renderList();
                this.renderHead();
                this.renderOccupants();
                break;

            case 'roomMessages':
                if (event.jid === this.current)
                    this.renderMessages();
                break;

            case 'roomMessage':
                if (event.jid === this.current) {
                    this.renderMessages();
                    store.markRoomRead(event.jid);
                }
                break;

            case 'connection':
                this.renderConnection();
                break;

            case 'notice':
                this.toast(event.level, event.text);
                break;

        }

    }

    private renderConnection(): void {
        this.state.textContent = store.connection?.state === 'connected'
                                     ? store.connection.jid
                                     : (store.connection?.state ?? 'connecting …');
    }


    // Rendering: the list of rooms

    private renderList(): void {

        const rooms = store.sortedRooms();

        if (rooms.length === 0) {
            render(this.list, html`<li class="chat-empty muted">No rooms. Enter one above — a room that does not exist is created by entering it.</li>`);
            return;
        }

        render(this.list, html`${rooms.map(room => html`
            <li>
                <a class="chat-item room-item ${room.jid === this.current ? 'active' : ''}"
                   href="/rooms/${encodeURIComponent(room.jid)}"
                   data-jid="${room.jid}"
                   title="${room.jid}">
                    <span class="room-mark ${room.state}">${roomIcon(room)}</span>
                    <span class="chat-name">${room.displayName}</span>
                    <span class="chat-time">${formatListTime(room.lastActivity)}</span>
                    <span class="chat-preview">${roomPreview(room)}</span>
                    ${room.unread > 0 ? html`<span class="chat-unread">${room.unread}</span>` : ''}
                </a>
            </li>
        `)}`);

    }


    // Rendering: the head of the open room

    private renderHead(): void {

        if (this.current === null) {
            render(this.head, html`<div class="peer-info"><div class="peer-name muted">No room selected</div></div>`);
            this.banner.replaceChildren();
            return;
        }

        const jid   = this.current;
        const room  = store.rooms.get(jid);

        render(this.head, html`
            <a href="/rooms" class="btn small back" title="Back to the list"><i class="fa-solid fa-arrow-left"></i></a>
            <div class="peer-info">
                <div class="peer-name">${room?.displayName ?? jid}</div>
                <div class="peer-meta">${jid}${room ? ` · as ${room.nick}` : ''}${room?.subject ? ` · ${room.subject}` : ''}</div>
            </div>
            <div class="peer-actions">
                ${lock(room)}
                ${room !== undefined && room.state === 'joined' ? html`
                    <button type="button" class="btn small danger" data-action="leave" title="Leave this room">
                        <i class="fa-solid fa-right-from-bracket"></i>
                    </button>` : ''}
            </div>
        `);

        this.head.querySelector<HTMLButtonElement>('button[data-encrypt]')?.
             addEventListener('click', () => void this.openUp(jid));

        this.head.querySelector<HTMLButtonElement>('button[data-action="leave"]')?.
             addEventListener('click', () => void this.leave(jid));

        this.renderBanner(room);

    }

    /**
     * The one thing about a room somebody has to be told rather than shown: a
     * semi-anonymous room cannot carry an encrypted line, and the lock alone
     * does not say why.
     */
    private renderBanner(room: Room | undefined): void {

        if (room === undefined || room.state !== 'joined' || room.cannotEncrypt === null) {
            this.banner.replaceChildren();
            return;
        }

        render(this.banner, html`
            <div class="notice small">
                <i class="fa-solid fa-lock-open"></i>
                Not encrypted here: ${room.cannotEncrypt}.
                ${room.owner && !room.nonAnonymous
                    ? html` You own this room and can change that.`
                    : !room.nonAnonymous
                        ? html` Only an owner of the room can change that.`
                        : ''}
            </div>
        `);

    }


    // Rendering: who is in the room

    private renderOccupants(): void {

        const room = this.current !== null ? store.rooms.get(this.current) : undefined;

        if (room === undefined || room.state !== 'joined') {
            this.occupants.replaceChildren();
            return;
        }

        render(this.occupants, html`
            <h2 class="occupants-head">${room.occupants.length} here</h2>
            <ul class="occupants-list">
                ${room.occupants.map(occupantRow)}
            </ul>
        `);

    }


    // Rendering: what was said

    private renderMessages(): void {

        const jid       = this.current;

        if (jid === null)
            return;

        const messages  = store.roomMessages.get(jid) ?? [];
        const atBottom  = this.messages.scrollHeight - this.messages.scrollTop - this.messages.clientHeight < 80;

        if (messages.length === 0) {
            render(this.messages, html`<p class="empty">Nothing said yet.</p>`);
            this.drawn = jid;
            return;
        }

        let previous: RoomMessage | null = null;

        const parts = messages.map(message => {

            const day = previous === null ||
                        !sameDay(new Date(previous.timestamp), new Date(message.timestamp))
                            ? html`<div class="day"><span>${formatDay(message.timestamp)}</span></div>`
                            : html``;

            previous = message;

            return html`
                ${day}
                <div class="message ${message.mine ? 'out' : 'in'} ${message.delayed ? 'delayed' : ''}" data-id="${message.id}">
                    <div class="meta">
                        <span class="nick">${message.nick}</span>
                        <span class="time">${formatTime(message.timestamp)}</span>
                        ${message.encrypted ? html`<i class="fa-solid fa-lock" title="This line travelled encrypted"></i>` : ''}
                    </div>
                    <div class="body-slot" data-body="${message.id}"></div>
                </div>
            `;

        });

        render(this.messages, html`${parts}`);

        // The body goes in as nodes rather than as markup: a link becomes a
        // link only by the rules in chat/links.ts, which validate the URL.
        for (const message of messages) {
            const slot = this.messages.querySelector<HTMLElement>(`[data-body="${cssEscape(message.id)}"]`);
            slot?.replaceChildren(renderBody(message.body));
        }

        if (atBottom || this.drawn !== jid)
            this.messages.scrollTop = this.messages.scrollHeight;

        this.drawn = jid;

    }


    // Doing things

    private async join(input: string): Promise<void> {

        const jid = input.trim();

        if (jid.length === 0)
            return;

        try {

            const room = await store.joinRoom(jid);

            must<HTMLFormElement>(this.root, '#join').reset();

            history.pushState(null, '', `/rooms/${encodeURIComponent(room.jid)}`);
            this.select(room.jid);

        }
        catch (error) {
            this.toast('error', errorMessage(error));
            // The row stays in the list carrying the refusal, so the list is
            // redrawn even though nothing was entered.
            this.renderList();
        }

    }

    private async leave(jid: string): Promise<void> {

        if (!window.confirm(`Leave ${jid}?\n\nWhat was said here is not kept by this app — the room's own history stays on the server.`))
            return;

        try {
            await store.leaveRoom(jid);
            history.pushState(null, '', '/rooms');
            this.select(null);
        }
        catch (error) {
            this.toast('error', errorMessage(error));
        }

    }

    /**
     * Makes the room one that can be written in encrypted.
     *
     * Asked for, and the question says what it costs: this changes the room for
     * everybody in it, and it is not undone by leaving.
     */
    private async openUp(jid: string): Promise<void> {

        const room = store.rooms.get(jid);

        if (room === undefined || room.cannotEncrypt === null)
            return;

        if (!room.nonAnonymous && !window.confirm(
                `Make ${jid} show everybody's real address?\n\n` +
                'That is what encryption in a room needs: one encrypts to the devices of an ' +
                'address, and a room hands out nicknames. From then on everybody in this room ' +
                'can see who everybody else really is.\n\n' +
                'It also only helps the people who come in afterwards — a service need not ' +
                'announce the people already here again.'))
        {
            return;
        }

        try {
            const changed = await store.openRoomUp(jid);
            this.toast('info', changed.cannotEncrypt === null
                                   ? 'Encrypted from here on.'
                                   : `Still not encrypted: ${changed.cannotEncrypt}`);
        }
        catch (error) {
            this.toast('error', errorMessage(error));
        }

    }

    private async send(): Promise<void> {

        const jid   = this.current;
        const body  = this.textarea.value.trim();

        if (jid === null || body.length === 0)
            return;

        this.textarea.value = '';

        try {
            await store.sendToRoom(jid, body);
        }
        catch (error) {
            // Put back what was typed. A refusal here usually means somebody
            // walked in whose address has not arrived, and the answer is to try
            // again in a moment - not to lose the sentence.
            this.textarea.value = body;
            this.toast('error', errorMessage(error));
        }

    }

    private toast(level: 'info' | 'warning' | 'error', text: string): void {

        const toast      = document.createElement('div');
        toast.className  = `toast ${level}`;
        toast.textContent = text;

        this.toasts.append(toast);

        window.setTimeout(() => toast.remove(), TOAST_MS);

    }

}


/**
 * CSS.escape is not in every browser this has to run in, and the only place an
 * id reaches a selector here is the body slot below.
 */
function cssEscape(value: string): string {
    return value.replace(/["\\]/gu, '\\$&');
}
