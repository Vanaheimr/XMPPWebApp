import { api, type Chat, type ChatState, type Connection, type ContactAction, type Message,
         type Room, type RoomMessage } from '../api/client';

// The browser's copy of the conversations, fed by two things: snapshots from
// the JSON API and the Server-Sent Events stream. Both carry sequence numbers
// from the same counter on the server. A snapshot says "everything up to N is
// in here", an event says "this is change N" - so an event is applied only
// when it is newer than the snapshot it would otherwise overwrite, and a
// replayed stream (Hermod repeats its cached events to every new client) does
// no harm.
//
// One counter per scope, not one overall: the list snapshot vouches for every
// chat summary, a chat's own snapshot vouches for its messages and its summary.

export type StoreEvent =
    | { type: 'chats' }
    | { type: 'messages'; jid: string }
    | { type: 'message';  jid: string; message: Message }
    | { type: 'rooms' }
    | { type: 'roomMessages'; jid: string }
    | { type: 'roomMessage';  jid: string; message: RoomMessage }
    | { type: 'connection' }
    | { type: 'notice';   level: 'info' | 'warning' | 'error'; text: string }
    | { type: 'stream' };

type Listener = (event: StoreEvent) => void;

interface ChatData        { seq: number; chat: Chat }
interface MessageData     { seq: number; message: Message }
interface RoomData        { seq: number; room: Room }
interface RoomMessageData { seq: number; message: RoomMessage }
interface ConnectionData  { seq: number; previous: string; connection: Connection }
interface NoticeData      { seq: number; level: string; text: string; timestamp: string }

type Recent =
    | { kind: 'chat';    seq: number; jid: string; chat: Chat }
    | { kind: 'message'; seq: number; jid: string; message: Message };

const RECENT_LIMIT = 500;


export class ChatStore {

    readonly chats     = new Map<string, Chat>();
    readonly messages  = new Map<string, Message[]>();

    /**
     * XEP-0045, and kept apart from the conversations above on purpose — the
     * same split the server makes, and for the same reason: a room looks like a
     * conversation and none of the rules underneath are the same.
     *
     * What is *not* split is the event stream. One connection carries both, and
     * has to: a second EventSource would be a second session's worth of work on
     * the server for the same account, and the room page and the chat page are
     * never open at once anyway.
     */
    readonly rooms         = new Map<string, Room>();
    readonly roomMessages  = new Map<string, RoomMessage[]>();

    /** Per chat: whether the archive holds anything older than what is loaded. */
    private readonly more = new Map<string, boolean>();

    connection:       Connection | null = null;
    streamConnected   = false;

    private listSeq   = 0;
    private readonly chatSeq    = new Map<string, number>();
    private          recent:    Recent[] = [];
    private          source:    EventSource | null = null;
    private readonly listeners  = new Set<Listener>();


    onChange(listener: Listener): () => void {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    /** The chats for the list: the most recently active first. */
    sortedChats(): Chat[] {

        return Array.from(this.chats.values()).sort((a, b) => {

            const timeA = a.lastActivity ? Date.parse(a.lastActivity) : 0;
            const timeB = b.lastActivity ? Date.parse(b.lastActivity) : 0;

            return timeB - timeA || a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' });

        });

    }

    get unread(): number {
        let n = 0;
        for (const chat of this.chats.values())
            n += chat.unread;
        return n;
    }


    /** Open the event stream; every 'open' - the first and every reconnect - reloads the state. */
    start(): void {

        if (this.source !== null)
            return;

        const source = new EventSource(api.eventsURL);
        this.source  = source;

        source.addEventListener('open', () => {
            this.streamConnected = true;
            this.emit({ type: 'stream' });
            void this.refresh();
        });

        source.addEventListener('error', () => {
            if (this.streamConnected) {

                this.streamConnected = false;
                this.emit({ type: 'stream' });

                // Ask once whether we are still signed in. A stream that drops
                // is usually the network, and EventSource reconnects on its own
                // - but it is also what a revoked session looks like from here,
                // and that one never reconnects: the server closes the stream
                // and answers the retry with 401, which EventSource reports as
                // one more 'error' and nothing else. Without this the page sits
                // showing "disconnected" until somebody clicks something.
                //
                // Only on the way from connected to disconnected, so a browser
                // that is offline does not ask again for every failed retry.
                // The answer is not read: a 401 goes to the handler that sends
                // the page back to the sign-in, and anything else means the
                // session is fine and the stream really was the network.
                void api.auth.me().catch(() => { /* handled there or not ours */ });

            }
        });

        source.addEventListener('chat',        event => this.onChat       (parse<ChatData>        (event)));
        source.addEventListener('message',     event => this.onMessage    (parse<MessageData>     (event)));
        source.addEventListener('room',        event => this.onRoom       (parse<RoomData>        (event)));
        source.addEventListener('roomMessage', event => this.onRoomMessage(parse<RoomMessageData> (event)));
        source.addEventListener('connection',  event => this.onConnection (parse<ConnectionData>  (event)));
        source.addEventListener('notice',      event => this.onNotice     (parse<NoticeData>      (event)));

    }

    /** Close the stream and forget everything, e.g. at sign-out. */
    stop(): void {

        this.source?.close();
        this.source = null;

        this.streamConnected = false;
        this.connection      = null;
        this.listSeq         = 0;
        this.recent          = [];

        this.chats.clear();
        this.messages.clear();
        this.chatSeq.clear();
        this.more.clear();

        this.rooms.clear();
        this.roomMessages.clear();

    }


    // XEP-0045: the rooms. Everything below is the room half and touches
    // nothing above it.

    /** The rooms for the list: the most recently active first. */
    sortedRooms(): Room[] {

        return Array.from(this.rooms.values()).sort((a, b) =>
            Date.parse(b.lastActivity ?? '1970-01-01') - Date.parse(a.lastActivity ?? '1970-01-01') ||
            a.displayName.localeCompare(b.displayName));

    }

    async loadRooms(): Promise<void> {

        const list = await api.rooms.list();

        this.rooms.clear();

        for (const room of list.rooms)
            this.rooms.set(room.jid, room);

        this.emit({ type: 'rooms' });

    }

    async loadRoomMessages(jid: string): Promise<void> {

        const result = await api.rooms.messages(jid);

        this.roomMessages.set(jid, result.messages);
        this.rooms.set(jid, result.room);

        this.emit({ type: 'roomMessages', jid });
        this.emit({ type: 'rooms' });

    }

    /** Whether the lines of this room have been loaded. */
    isRoomLoaded(jid: string): boolean {
        return this.roomMessages.has(jid);
    }

    async joinRoom(jid: string, nick?: string): Promise<Room> {

        const result = await api.rooms.join(jid, nick);

        this.rooms.set(result.room.jid, result.room);
        this.emit({ type: 'rooms' });

        return result.room;

    }

    async leaveRoom(jid: string): Promise<void> {

        await api.rooms.leave(jid);

        this.rooms.delete(jid);
        this.roomMessages.delete(jid);

        this.emit({ type: 'rooms' });

    }

    /**
     * XEP-0308: replaces the last line this app said in the room.
     *
     * The line keeps its place and its time — a correction is not a new thing
     * said later, it is the same thing said properly, and moving it to the
     * bottom would put it after the answers to it.
     */
    async correctInRoom(jid: string, body: string): Promise<RoomMessage> {

        const corrected = await api.rooms.correct(jid, body);

        this.applyRoomMessage(jid, corrected);
        this.emit({ type: 'roomMessage', jid, message: corrected });

        return corrected;

    }

    /** XEP-0424: takes back a line this app said in a room. */
    async retractInRoom(jid: string, id: string): Promise<RoomMessage> {

        const taken = await api.rooms.retract(jid, id);

        this.applyRoomMessage(jid, taken);
        this.emit({ type: 'roomMessage', jid, message: taken });

        return taken;

    }

    async sendToRoom(jid: string, body: string): Promise<RoomMessage> {

        const result = await api.rooms.send(jid, body);

        // The room will hand it back as well; it is recognised by its id there
        // and on the server, so this only makes it appear at once.
        if (this.applyRoomMessage(jid, result.message))
            this.emit({ type: 'roomMessage', jid, message: result.message });

        return result.message;

    }

    async openRoomUp(jid: string): Promise<Room> {

        const result = await api.rooms.openUp(jid);

        this.rooms.set(result.room.jid, result.room);
        this.emit({ type: 'rooms' });

        return result.room;

    }

    async setRoomSubject(jid: string, subject: string): Promise<void> {
        await api.rooms.subject(jid, subject);
    }

    markRoomRead(jid: string): void {

        const room = this.rooms.get(jid);

        if (room === undefined || room.unread === 0)
            return;

        api.rooms.read(jid).catch((error: unknown) => console.debug('Could not mark the room as read:', error));

    }


    async refresh(): Promise<void> {

        try
        {

            await this.loadChats();
            await this.loadStatus();

            for (const jid of Array.from(this.messages.keys()))
                await this.loadMessages(jid);

        }
        catch (error)
        {
            this.emit({ type: 'notice', level: 'error', text: `Could not load the chats: ${message(error)}` });
        }

    }

    async loadStatus(): Promise<void> {
        const status     = await api.status();
        this.connection  = status.connection;
        this.emit({ type: 'connection' });
    }

    async loadChats(): Promise<void> {

        const list = await api.chats.list();

        this.chats.clear();

        for (const chat of list.chats)
            this.chats.set(chat.jid, chat);

        this.listSeq = list.seq;

        // What came in over the stream while the list was on its way.
        for (const entry of this.recent)
            if (entry.kind === 'chat' && entry.seq > list.seq && entry.seq > (this.chatSeq.get(entry.jid) ?? 0))
                this.chats.set(entry.jid, entry.chat);

        this.emit({ type: 'chats' });

    }

    async loadMessages(jid: string): Promise<void> {

        const previous = this.messages.get(jid) ?? [];
        const result   = await api.chats.messages(jid);

        // What a scroll into the past has already brought back is kept. The
        // snapshot only reaches as far as the server's own store does, so
        // refreshing it would otherwise undo every scroll - and a refresh
        // happens on every reconnect of the event stream.
        const oldest = result.messages.length > 0 ? Date.parse(result.messages[0].timestamp) : Infinity;
        const known  = new Set(result.messages.map(message => message.id));
        const older  = previous.filter(message => !known.has(message.id) && Date.parse(message.timestamp) < oldest);

        this.messages.set(jid, older.concat(result.messages));
        this.chats.set(jid, result.chat);
        this.chatSeq.set(jid, result.seq);

        // hasMore is an answer about the oldest message the server sent. Where
        // older ones are already on screen, it says nothing - and the answer
        // that was given about those still holds.
        if (older.length === 0)
            this.more.set(jid, result.hasMore);

        for (const entry of this.recent)
        {

            if (entry.jid !== jid || entry.seq <= result.seq)
                continue;

            if (entry.kind === 'message')
                upsert(this.messages.get(jid)!, entry.message);
            else
                this.chats.set(jid, entry.chat);

        }

        this.emit({ type: 'messages', jid });
        this.emit({ type: 'chats' });

    }

    /**
     * The next page of what was said before the oldest message on screen,
     * straight out of the archive on the server.
     *
     * It does not emit: whoever asked for it is looking at the top of the
     * conversation and has to put the scroll position back where it was, which
     * only works if the rendering happens under their control.
     */
    async loadOlder(jid: string): Promise<number> {

        const list = this.messages.get(jid);

        if (list === undefined || list.length === 0 || !this.hasOlder(jid))
            return 0;

        const result = await api.chats.older(jid, list[0].timestamp);
        const known  = new Set(list.map(message => message.id));
        const older  = result.messages.filter(message => !known.has(message.id));

        this.messages.set(jid, older.concat(list));

        // A page that added nothing ends the scrolling back, whatever the
        // server says about there being more. The cursor is the oldest message
        // on screen, so a page of messages already on screen would leave that
        // cursor where it is and be asked for again - and the page asks as soon
        // as the top is in view, which after this would be forever.
        this.more.set(jid, older.length > 0 && result.hasMore);

        return older.length;

    }

    /** Whether scrolling up in this chat leads anywhere. */
    hasOlder(jid: string): boolean {
        return this.more.get(jid) ?? false;
    }

    /** Whether the messages of this chat have been loaded. */
    isLoaded(jid: string): boolean {
        return this.messages.has(jid);
    }


    async open(jid: string): Promise<Chat> {

        const result = await api.chats.open(jid);

        if (!this.chats.has(result.chat.jid))
            this.chats.set(result.chat.jid, result.chat);

        this.emit({ type: 'chats' });

        return result.chat;

    }

    /**
     * XEP-0308: replaces the last line sent here.
     *
     * Refused by the server where the conversation is encrypted — a correction
     * can only go out in the clear, and it carries the text that was
     * encrypted. The refusal is shown rather than worked around.
     */
    async correct(jid: string, body: string): Promise<Message> {

        const corrected = await api.chats.correct(jid, body);

        this.applyMessage(jid, corrected);
        this.emit({ type: 'message', jid, message: corrected });

        return corrected;

    }

    /** XEP-0424: takes back a line this app sent. */
    async retract(jid: string, id: string): Promise<Message> {

        const taken = await api.chats.retract(jid, id);

        this.applyMessage(jid, taken);
        this.emit({ type: 'message', jid, message: taken });

        return taken;

    }

    async send(jid: string, body: string): Promise<Message> {

        const result = await api.chats.send(jid, body);

        // The stream may have delivered it already; either way it is in.
        if (this.applyMessage(jid, result.message))
            this.emit({ type: 'message', jid, message: result.message });

        return result.message;

    }

    /**
     * XEP-0363: sends a file and puts the line into the conversation.
     *
     * The same shape as `send`, because as far as everything downstream is
     * concerned it is the same thing: a message whose body is an address.
     */
    async sendFile(jid: string, file: File): Promise<Message> {

        const result = await api.chats.sendFile(jid, file);

        if (this.applyMessage(jid, result.message))
            this.emit({ type: 'message', jid, message: result.message });

        return result.message;

    }

    markRead(jid: string): void {

        const chat = this.chats.get(jid);

        if (chat === undefined || chat.unread === 0)
            return;

        api.chats.read(jid).catch((error: unknown) => console.debug('Could not mark the chat as read:', error));

    }

    sendState(jid: string, state: ChatState): void {
        api.chats.state(jid, state).catch((error: unknown) => console.debug('Could not send the chat state:', error));
    }

    async contact(jid: string, action: ContactAction): Promise<void> {
        await api.chats.contact(jid, action);
    }

    async reconnect(): Promise<void> {
        const result     = await api.reconnect();
        this.connection  = result.connection;
        this.emit({ type: 'connection' });
    }


    private onChat(event: ChatData): void {

        const jid = event.chat.jid;

        this.remember({ kind: 'chat', seq: event.seq, jid, chat: event.chat });

        if (event.seq <= Math.max(this.listSeq, this.chatSeq.get(jid) ?? 0))
            return;

        this.chats.set(jid, event.chat);
        this.emit({ type: 'chats' });

    }

    private onMessage(event: MessageData): void {

        const jid = event.message.chat;

        this.remember({ kind: 'message', seq: event.seq, jid, message: event.message });

        if (event.seq <= (this.chatSeq.get(jid) ?? 0))
            return;

        if (this.applyMessage(jid, event.message))
            this.emit({ type: 'message', jid, message: event.message });

    }

    private onRoom(event: RoomData): void {

        // 'left' is the one state that never stands in the list: it is the
        // event saying the row is gone.
        if (event.room.state === 'left') {
            this.rooms.delete(event.room.jid);
            this.roomMessages.delete(event.room.jid);
        }
        else
            this.rooms.set(event.room.jid, event.room);

        this.emit({ type: 'rooms' });

    }

    private onRoomMessage(event: RoomMessageData): void {

        const jid = event.message.room;

        if (this.applyRoomMessage(jid, event.message))
            this.emit({ type: 'roomMessage', jid, message: event.message });

    }

    /** Put a line into a loaded room; false when the room is not loaded. */
    private applyRoomMessage(jid: string, message: RoomMessage): boolean {

        const list = this.roomMessages.get(jid);

        if (list === undefined)
            return false;

        const index = list.findIndex(existing => existing.id === message.id);

        if (index >= 0) {
            list[index] = message;
            return true;
        }

        let position = list.length;

        while (position > 0 && Date.parse(list[position - 1].timestamp) > Date.parse(message.timestamp))
            position--;

        list.splice(position, 0, message);

        return true;

    }

    private onConnection(event: ConnectionData): void {
        this.connection = event.connection;
        this.emit({ type: 'connection' });
    }

    private onNotice(event: NoticeData): void {

        const level = event.level === 'error' || event.level === 'warning'
                          ? event.level
                          : 'info';

        this.emit({ type: 'notice', level, text: event.text });

    }

    /** Put a message into a loaded chat; false when the chat is not loaded. */
    private applyMessage(jid: string, message: Message): boolean {

        const list = this.messages.get(jid);

        if (list === undefined)
            return false;

        upsert(list, message);

        return true;

    }

    private remember(entry: Recent): void {

        this.recent.push(entry);

        if (this.recent.length > RECENT_LIMIT)
            this.recent.splice(0, this.recent.length - RECENT_LIMIT);

    }

    private emit(event: StoreEvent): void {

        for (const listener of this.listeners)
        {
            try
            {
                listener(event);
            }
            catch (error)
            {
                console.error('A chat listener failed:', error);
            }
        }

    }

}


/** Replace the message with the same id, or insert it where its time puts it. */
function upsert(list: Message[], message: Message): void {

    const index = list.findIndex(existing => existing.id === message.id);

    if (index >= 0) {
        list[index] = message;
        return;
    }

    let position = list.length;

    while (position > 0 && Date.parse(list[position - 1].timestamp) > Date.parse(message.timestamp))
        position--;

    list.splice(position, 0, message);

}

function parse<T>(event: Event): T {
    return JSON.parse((event as MessageEvent<string>).data) as T;
}

function message(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}


export const store = new ChatStore();
