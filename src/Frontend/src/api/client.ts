import { config } from '../config';

export type Presence         = 'offline' | 'available' | 'away' | 'chat' | 'dnd' | 'xa';
export type Subscription     = 'none' | 'to' | 'from' | 'both' | 'remove';
export type ChatState        = 'active' | 'composing' | 'paused' | 'inactive' | 'gone';
export type ConnectionState  = 'unconfigured' | 'disconnected' | 'connecting' | 'connected' | 'reconnecting';
export type ContactAction    = 'add' | 'accept' | 'deny' | 'remove';
export type SaslMechanism    = 'SCRAM-SHA-256' | 'SCRAM-SHA-1' | 'PLAIN';
export type AccountSource     = 'none' | 'file' | 'arguments';

/** The SASL mechanisms the account page offers, strongest first. */
export const saslMechanisms: SaslMechanism[] = ['SCRAM-SHA-256', 'SCRAM-SHA-1', 'PLAIN'];

/** The XMPP account settings, as the account page reads them - never the password. */
export interface AccountInfo {
    jid:                string;
    websocket:          string | null;
    minimumSasl:        SaslMechanism;
    allowInsecure:      boolean;
    trustAnnouncement:  boolean;
    /** whether a password is stored; the page never receives the password itself */
    passwordSet:        boolean;
}

/** What the account page sends: the same fields, and a password only when changing it. */
export interface AccountUpdate {
    jid:                string;
    password?:          string;
    websocket:          string | null;
    minimumSasl:        SaslMechanism;
    allowInsecure:      boolean;
    trustAnnouncement:  boolean;
}

/** The answer of every account route. */
export interface AccountResponse {
    configured:   boolean;
    source:       AccountSource;
    account:      AccountInfo | null;
    connection:   Connection;
}

/** The login of the web page itself - the username, never the password. */
export interface WebLogin {
    username:        string;
    /** where it is kept on the server */
    file:            string;
    /** how many other sessions a change ended */
    sessionsEnded?:  number;
}

/** What the settings page sends to change the web login. */
export interface WebLoginUpdate {
    /** required: a session alone must not be able to take the page over */
    currentPassword:  string;
    username:         string;
    /** blank keeps the password in force and changes only the username */
    newPassword?:     string;
}

/**
 * A file that was shared in a conversation and has been fetched into the
 * archive beside it. The URL is not part of this - `mediaURL` builds it from
 * the conversation and the name, so that what lies on disk carries no address
 * of this web app.
 */
export interface Media {
    name:         string;
    contentType:  string;
    size:         number;
    /** where it came from, for the link to the original */
    source:       string | null;
}

/** One line of a conversation. The body is plain text: the page escapes it. */
export interface Message {
    id:         string;
    chat:       string;
    direction:  'in' | 'out';
    from:       string;
    body:       string;
    timestamp:  string;
    delayed:    boolean;
    carbon:     boolean;
    corrects:   string | null;
    corrected:  boolean;
    delivered:  boolean;
    displayed:  boolean;
    /** the file this message handed over, once it has been fetched */
    media:      Media | null;
}

/** A conversation as the list shows it. */
export interface Chat {
    jid:             string;
    name:            string | null;
    displayName:     string;
    inRoster:        boolean;
    subscription:    Subscription;
    pendingRequest:  boolean;
    presence:        Presence;
    status:          string | null;
    chatState:       ChatState | null;
    unread:          number;
    lastMessage:     Message | null;
    lastActivity:    string | null;
}

/** The XMPP connection of the web app. */
export interface Connection {
    state:             ConnectionState;
    jid:               string;
    fullJid:           string | null;
    websocket:         string | null;
    carbons:           boolean;
    streamManagement:  boolean;
    connectedAt:       string | null;
    error:             string | null;
}

export interface Status {
    service:     string;
    version:     string;
    hermod:      string | null;
    ratatoskr:   string | null;
    timestamp:   string;
    uptime:      string;
    connection:  Connection;
    contacts:    number;
    chats:       number;
    unread:      number;
    sessions:    number;
    seq:         number;
}

/** Who is signed in to the web page. */
export interface Me {
    username:  string;
    session:   { createdAt: string; expiresAt: string };
}

export interface ChatList {
    seq:    number;
    chats:  Chat[];
}

export interface ChatMessages {
    seq:       number;
    chat:      Chat;
    messages:  Message[];
    /** whether the archive holds anything from before the oldest message here */
    hasMore:   boolean;
}

/** What a scroll into the past brings back: older messages, straight from the archive. */
export interface OlderMessages {
    seq:       number;
    before:    string;
    messages:  Message[];
    hasMore:   boolean;
}


export class ApiError extends Error {

    constructor(public readonly status:  number,
                message:                 string,
                public readonly body?:   unknown) {
        super(message);
        this.name = 'ApiError';
    }

    get isUnauthorized(): boolean {
        return this.status === 401;
    }

}


let unauthorizedHandler: (() => void) | null = null;

/** Called whenever the API answers 401, i.e. the session is gone. */
export function onUnauthorized(handler: () => void): void {
    unauthorizedHandler = handler;
}


async function request<T>(method: string, path: string, body?: unknown): Promise<T> {

    const headers: Record<string, string> = { 'Accept': 'application/json' };

    if (body !== undefined)
        headers['Content-Type'] = 'application/json';

    // Same origin, so the session cookie travels with every request.
    const response = await fetch(config.apiBase + path, {
                               method,
                               headers,
                               credentials: 'same-origin',
                               body: body !== undefined ? JSON.stringify(body) : undefined
                           });

    if (response.status === 401)
        unauthorizedHandler?.();

    if (response.status === 204) {
        // Nothing to read, but reading it lets the browser finish the request
        // cleanly instead of aborting an unconsumed body.
        await response.arrayBuffer();
        return undefined as T;
    }

    const text = await response.text();
    let json: unknown = null;

    try {
        json = text.length > 0 ? JSON.parse(text) : null;
    }
    catch {
        if (response.ok)
            throw new ApiError(response.status, `Invalid JSON in the response of ${method} ${path}`, text);
    }

    if (!response.ok) {

        const message = typeof json === 'object' && json !== null && 'error' in json && typeof json.error === 'string'
                            ? json.error
                            : `${response.status} ${response.statusText}`;

        throw new ApiError(response.status, message, json);

    }

    return json as T;

}


const jid = (value: string) => encodeURIComponent(value);

export const api = {

    /** The Server-Sent Events stream; the browser sends the session cookie along. */
    eventsURL:  `${config.apiBase}/events`,

    auth: {
        me:      ()                                     => request<Me>  ('GET',  '/auth/me'),
        login:   (username: string, password: string)   => request<Me>  ('POST', '/auth/login', { username, password }),
        logout:  ()                                     => request<void>('POST', '/auth/logout')
    },

    status:     ()  => request<Status>('GET',  '/status'),
    reconnect:  ()  => request<{ connection: Connection }>('POST', '/connection/reconnect'),

    account: {
        get:     ()                       => request<AccountResponse>('GET',    '/account'),
        save:    (account: AccountUpdate)  => request<AccountResponse>('PUT',    '/account', account),
        forget:  ()                       => request<AccountResponse>('DELETE', '/account')
    },

    webLogin: {
        get:   ()                        => request<WebLogin>('GET', '/weblogin'),
        save:  (login: WebLoginUpdate)   => request<WebLogin>('PUT', '/weblogin', login)
    },

    /**
     * Where a file of a conversation is served from. Same origin, so the
     * session cookie travels with the picture - and the browser never asks the
     * host the file came from, which would tell it who is reading what.
     */
    mediaURL:  (chat: string, name: string) => `${config.apiBase}/chats/${jid(chat)}/media/${encodeURIComponent(name)}`,

    chats: {
        list:      ()                                        => request<ChatList>            ('GET',  '/chats'),
        open:      (chat: string)                            => request<{ seq: number; chat: Chat }>('POST', '/chats', { jid: chat }),
        messages:  (chat: string)                            => request<ChatMessages>        ('GET',  `/chats/${jid(chat)}/messages`),
        older:     (chat: string, before: string, limit?: number) => request<OlderMessages>(
                       'GET',
                       `/chats/${jid(chat)}/messages?before=${encodeURIComponent(before)}` +
                       (limit !== undefined ? `&limit=${limit}` : '')
                   ),
        send:      (chat: string, body: string)              => request<{ seq: number; message: Message }>('POST', `/chats/${jid(chat)}/messages`, { body }),
        read:      (chat: string)                            => request<void>                ('POST', `/chats/${jid(chat)}/read`),
        state:     (chat: string, state: ChatState)          => request<void>                ('POST', `/chats/${jid(chat)}/state`, { state }),
        contact:   (chat: string, action: ContactAction)     => request<void>                ('POST', `/chats/${jid(chat)}/contact`, { action })
    }

};
