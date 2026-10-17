import { type Room, type RoomMessage } from '../api/client';
import { lineActions, quotation, showLine } from '../chat/lines';
import { renderBody } from '../chat/links';
import { store, type StoreEvent } from '../chat/store';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { lock, occupantRow, roomIcon } from '../rooms/parts';
import { roomPreview } from '../rooms/text';
import { errorMessage, excerpt, formatDay, formatListTime, formatTime, sameDay } from '../ui';

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

                    <div id="composer-context" class="composer-context" hidden></div>

                    <form id="composer" class="composer">
                        <textarea name="body" rows="1" placeholder="Say something …" aria-label="Message"></textarea>
                        <button type="button" id="unsay" class="btn small danger" hidden
                                title="XEP-0424: take this line back. A request and not a deletion - the other side decides what to do with it, and the archive keeps its own copy.">take back</button>
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
    private readonly context:    HTMLElement;
    private readonly composer:   HTMLFormElement;
    private readonly textarea:   HTMLTextAreaElement;
    private readonly unsay:      HTMLButtonElement;
    private readonly toasts:     HTMLElement;

    private readonly unsubscribe: () => void;

    private current:  string | null = null;
    private drawn:    string | null = null;

    /** XEP-0308/0424: the id of the line in the box, or null when it is new. */
    private editing:  string | null = null;

    /** What was in the box before an edit took it over, to be put back afterwards. */
    private draftBeforeEdit = '';

    /** XEP-0461: the line the box answers, or null. */
    private replyingTo:  RoomMessage | null = null;

    /** The line a tap picked, whose buttons show where nothing hovers. */
    private picked:   string | null = null;

    private sending   = false;

    constructor(private readonly root: HTMLElement) {

        this.state      = must<HTMLElement>(root, '#me-state');
        this.list       = must<HTMLElement>(root, '#room-list');
        this.head       = must<HTMLElement>(root, '#head');
        this.banner     = must<HTMLElement>(root, '#banner');
        this.messages   = must<HTMLElement>(root, '#messages');
        this.occupants  = must<HTMLElement>(root, '#occupants');
        this.context    = must<HTMLElement>(root, '#composer-context');
        this.composer   = must<HTMLFormElement>(root, '#composer');
        this.textarea   = must<HTMLTextAreaElement>(this.composer, 'textarea');
        this.unsay      = must<HTMLButtonElement>(this.composer, '#unsay');
        this.toasts     = must<HTMLElement>(root, '#toasts');

        this.unsubscribe = store.onChange(event => this.onStore(event));

        must<HTMLFormElement>(root, '#join').addEventListener('submit', event => {
            event.preventDefault();
            void this.join(new FormData(event.target as HTMLFormElement).get('jid') as string);
        });

        this.unsay.addEventListener('click', () => { void this.takeBack(); });

        this.composer.addEventListener('submit', event => {
            event.preventDefault();
            void this.send();
        });

        this.textarea.addEventListener('keydown', event => {

            if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                void this.send();
                return;
            }

            // XEP-0308: up-arrow in an empty box takes back the last line said
            // here, to be said properly. Sending it then corrects rather than
            // repeats — which is the whole point, because the wrong word is
            // standing in front of everybody until it is replaced. The pencil
            // on that line does the same.
            if (event.key === 'ArrowUp' && this.textarea.value.length === 0) {

                const mine = this.editableLine();

                if (mine !== null) {
                    event.preventDefault();
                    this.startEditing(mine);
                }

                return;

            }

            // And out again, with the box as it was.
            if (event.key === 'Escape') {
                if (this.editing !== null)
                    this.stopEditing();
                else if (this.replyingTo !== null)
                    this.stopReplying();
            }

        });

        // One listener for every line, because the lines are drawn again with
        // every one that arrives: the buttons on a line, the quotation in it,
        // and a tap that shows the buttons on a screen nothing hovers over.
        this.messages.addEventListener('click', event => this.onMessageClick(event));

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

        // Both belong to the room they were started in: an edit sent in the
        // next one would replace a line there, and an answer would point at a
        // line that is not in it.
        if (jid !== this.current) {

            if (this.editing !== null)
                this.stopEditing();

            if (this.replyingTo !== null)
                this.stopReplying();

            this.picked = null;

        }

        this.current = jid;
        this.drawn   = null;

        // On a phone the list and the room take turns, and this is what says
        // whose turn it is - without it a room chosen there was never shown.
        this.root.classList.toggle('open', jid !== null);

        document.title = TITLE;

        this.renderList();
        this.renderHead();
        this.renderOccupants();

        this.updateComposer();

        if (jid === null) {
            render(this.messages, html`<p class="empty">Pick a room, or enter one above.</p>`);
            return;
        }

        if (store.isRoomLoaded(jid))
            this.renderMessages();
        else {
            render(this.messages, html`<p class="empty">Loading …</p>`);
            void store.loadRoomMessages(jid)
                      .then(()      => this.updateComposer())
                      .catch((error: unknown) => {
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
                this.updateComposer();
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

    /**
     * There is somewhere to send to, or there is not.
     *
     * <b>Not a detail.</b> A box that takes a sentence and drops it is worse
     * than no box: the two cases it has to cover are a room nobody picked and a
     * room this app is not in - the second reachable by a bookmark to a room
     * that was left, which is exactly how it was found.
     */
    private updateComposer(): void {

        const room = this.current !== null ? store.rooms.get(this.current) : undefined;

        this.composer.hidden = room === undefined || room.state !== 'joined';

        // Nothing to answer into and nothing to correct with, so the strip
        // that says what sending would do goes with the box.
        if (this.composer.hidden) {

            if (this.editing !== null)
                this.stopEditing();

            if (this.replyingTo !== null)
                this.stopReplying();

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

        const editable = this.editableLine()?.id ?? null;

        const parts = messages.map(message => {

            const day = previous === null ||
                        !sameDay(new Date(previous.timestamp), new Date(message.timestamp))
                            ? html`<div class="day"><span>${formatDay(message.timestamp)}</span></div>`
                            : html``;

            previous = message;

            return html`
                ${day}
                <div class="message ${message.mine ? 'out' : 'in'} ${message.delayed ? 'delayed' : ''} ${message.private ? 'private' : ''} ${message.id === editable ? 'editable' : ''} ${message.id === this.picked ? 'active' : ''}" data-id="${message.id}">
                    <div class="meta">
                        <span class="nick">${message.nick}</span>
                        <span class="time">${formatTime(message.timestamp)}</span>
                        ${message.encrypted ? html`<i class="fa-solid fa-lock" title="This line travelled encrypted"></i>` : ''}
                        ${message.private
                              ? html`<span class="only-you" title="Said to you alone (XEP-0045, 7.5). The box below answers the room, not the person.">only you</span>`
                              : ''}
                        ${message.corrected
                              ? html`<span class="edited" title="XEP-0308: this line was corrected after it was said">edited</span>`
                              : ''}
                        ${message.retracted
                              ? html`<span class="taken-back" title="XEP-0424: the sender took this back. The archive may still hold it.">taken back</span>`
                              : ''}
                    </div>
                    ${message.quote !== null && message.quote.length > 0 && !message.retracted
                          ? html`<blockquote class="quote ${message.repliesTo !== null ? 'linked' : ''}"
                                             title="${message.repliesTo !== null ? 'Show the message this answers' : ''}">${quotation(message.quote)}</blockquote>`
                          : ''}
                    <div class="body-slot" data-body="${message.id}"></div>
                </div>
            `;

        });

        render(this.messages, html`${parts}`);

        // The body goes in as nodes rather than as markup: a link becomes a
        // link only by the rules in chat/links.ts, which validate the URL. The
        // buttons go in the same way, last, so that where they become a row of
        // the line - on a phone - that row is at its foot.
        for (const message of messages) {

            const slot = this.messages.querySelector<HTMLElement>(`[data-body="${cssEscape(message.id)}"]`);

            slot?.replaceChildren(renderBody(message.body));

            const reply = answerable(message);
            const edit  = message.mine && !message.private && !message.retracted;

            if (reply || edit)
                slot?.parentElement?.appendChild(lineActions({ reply, edit }));

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

            await store.openRoomUp(jid);

            // What the answer says about the room is a moment old and may be
            // wrong: the service announces the change in a message of its own
            // (status 172), which arrives after the configuration was
            // acknowledged. The banner is fed from that and is the truth; this
            // only says what was asked for.
            //
            // And the second sentence is the one people trip over: a service
            // need not announce the occupants again, and Prosody does not, so
            // whoever is already standing here stays nameless until they come
            // back.
            this.toast('info',
                       'The room now shows real addresses. Anybody already here may have to ' +
                       'leave and come back before they can be encrypted to.');

        }
        catch (error) {
            this.toast('error', errorMessage(error));
        }

    }

    /**
     * XEP-0308: the line an edit would replace, or null - the rule the server
     * applies: the last line said from here that still stands, and not a
     * private one. A line that went encrypted is corrected encrypted; the
     * server refuses only when the room can no longer carry that, and says
     * why.
     */
    private editableLine(): RoomMessage | null {

        if (this.current === null)
            return null;

        const lines = store.roomMessages.get(this.current) ?? [];

        for (let i = lines.length - 1; i >= 0; i--) {

            const line = lines[i];

            if (!line.mine || line.retracted || line.private)
                continue;

            return line;

        }

        return null;

    }

    private lineById(id: string): RoomMessage | null {

        if (this.current === null)
            return null;

        return (store.roomMessages.get(this.current) ?? []).find(line => line.id === id) ?? null;

    }

    /** XEP-0308: puts a line into the box, to be said properly. */
    private startEditing(line: RoomMessage): void {

        if (this.replyingTo !== null)
            this.stopReplying();

        if (this.editing === null)
            this.draftBeforeEdit = this.textarea.value;

        this.editing         = line.id;
        this.textarea.value  = line.body;
        this.composer.classList.add('editing');
        this.unsay.hidden    = false;

        this.renderContext();
        this.textarea.focus();

    }

    /** Ends an edit, and gives the box back what it held before. */
    private stopEditing(): void {
        this.editing          = null;
        this.textarea.value   = this.draftBeforeEdit;
        this.draftBeforeEdit  = '';
        this.composer.classList.remove('editing');
        this.unsay.hidden     = true;
        this.renderContext();
    }

    /**
     * XEP-0461: the next line said here answers this one. What is in the box
     * stays - whoever starts typing and then decides it is an answer should
     * not have to type it again.
     */
    private startReplying(line: RoomMessage): void {

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
     * or replace one. Without it the box looks the same either way, and Enter
     * does something different from what it seems to - in front of everybody.
     */
    private renderContext(): void {

        const editing  = this.editing !== null;
        const line     = editing ? this.lineById(this.editing!) : this.replyingTo;

        this.composer.classList.toggle('replying', !editing && line !== null);

        if (line === null) {
            this.context.hidden = true;
            this.context.replaceChildren();
            return;
        }

        render(this.context, html`
            <i class="fa-solid ${editing ? 'fa-pen' : 'fa-reply'}" aria-hidden="true"></i>
            <div class="context-text">
                <div class="context-label">${editing ? 'Editing your message' : `Replying to ${line.mine ? 'yourself' : line.nick}`}</div>
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
        const element  = target.closest<HTMLElement>('.message');
        const line     = element?.dataset.id !== undefined ? this.lineById(element.dataset.id) : null;

        if (element === null || line === null)
            return;

        switch (target.closest<HTMLButtonElement>('.actions button')?.dataset.action) {

            case 'reply':
                this.unpick();
                this.startReplying(line);
                return;

            case 'edit':
                this.unpick();
                this.startEditing(line);
                return;

        }

        if (line.repliesTo !== null && target.closest('.quote') !== null) {
            this.showAnswered(line.repliesTo);
            return;
        }

        // Links, players and a selection in progress keep their click.
        if (target.closest('a, button, audio, video') !== null || window.getSelection()?.isCollapsed === false)
            return;

        // A tap picks the line, so its buttons show where nothing hovers; a
        // second tap, or a tap on another line, puts them away again. Kept by
        // id, because the next line to arrive draws the room again.
        const picked = this.picked === line.id;

        this.unpick();

        if (!picked) {
            this.picked = line.id;
            element.classList.add('active');
        }

    }

    private unpick(): void {

        this.picked = null;

        for (const active of this.messages.querySelectorAll('.message.active'))
            active.classList.remove('active');

    }

    /**
     * XEP-0461: the line an answer is about, brought into view. Found by the
     * name the room gave it, which is what an answer in a room points at.
     */
    private showAnswered(stanzaId: string): void {

        const line     = (store.roomMessages.get(this.current ?? '') ?? []).find(candidate => candidate.stanzaId === stanzaId);
        const element  = line !== undefined
                             ? this.messages.querySelector<HTMLElement>(`.message[data-id="${cssEscape(line.id)}"]`)
                             : null;

        if (element === null) {
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
            await store.retractInRoom(jid, id);
        }
        catch (error) {
            this.toast('error', errorMessage(error));
        }

    }

    private async send(): Promise<void> {

        const jid       = this.current;
        const body      = this.textarea.value.trim();
        const editing   = this.editing !== null;
        const answers   = this.replyingTo;

        if (jid === null || body.length === 0 || this.sending)
            return;

        this.sending = true;

        try {

            if (editing) {

                await store.correctInRoom(jid, body);

                // What was in the box before the edit comes back.
                this.stopEditing();

            }
            else {

                await store.sendToRoom(jid, body, answers?.id);

                if (this.replyingTo === answers)
                    this.stopReplying();

                this.textarea.value = '';

            }

        }
        catch (error) {
            // The sentence stays in the box, and so does what it was to be -
            // an answer, or a correction. A refusal here usually means somebody
            // walked in whose address has not arrived, and the answer is to try
            // again in a moment - not to lose the sentence, nor to send it the
            // second time as something else.
            this.toast('error', errorMessage(error));
        }
        finally {
            this.sending = false;
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
 * XEP-0461: whether a line can be answered here.
 *
 * Not one taken back - there is nothing left to answer. Not a private word -
 * the answer goes to the whole room and carries what it answers along. And not
 * a line the room gave no name: the answer has to point at that name, and a
 * line of ours only learns it from its reflection a moment after it was said.
 */
function answerable(line: RoomMessage): boolean {
    return !line.retracted && !line.private && line.stanzaId !== null;
}

/**
 * CSS.escape is not in every browser this has to run in, and the only places
 * an id reaches a selector here are the body slot and the line an answer is
 * about.
 */
function cssEscape(value: string): string {
    return value.replace(/["\\]/gu, '\\$&');
}
