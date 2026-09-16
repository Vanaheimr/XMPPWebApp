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
    /**
     * XEP-0084: the id of the picture this account last published, or null.
     *
     * Not a setting and not in the account file: it is what the server holds,
     * and pointing this app at a different server would make it somebody
     * else's question.
     */
    avatar:             string | null;
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

/** One registered passkey, as the account routes list it. */
export interface Passkey {
    id:          string;
    name:        string;
    createdAt:   string;
    lastUsedAt:  string | null;
}

/** The options of one ceremony, with every byte string as base64url. */
export interface PasskeyCeremony {
    ceremonyId:  string;
    publicKey:   Record<string, unknown>;
}

/**
 * Proof that whoever is asking is still at the keyboard, for the two account
 * routes that cannot be undone by closing the tab. Either the password of this
 * page's account - not the XMPP one - or a passkey assertion.
 */
export type Confirmation = { password: string }
                         | { ceremonyId: string; credential: unknown };

/** What the settings page sends to change the password of the account. */
export interface PasswordUpdate {
    /** required: a session alone must not be able to take the account over */
    currentPassword:  string;
    newPassword:      string;
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

/**
 * How the sending device's OMEMO identity key stood when a message arrived.
 *
 * Only two of the three can ride on a message. 'new' is the ordinary case and
 * not a warning: blind trust means the first message from a device is read
 * without anybody having compared a fingerprint. 'changed' is the one that
 * matters - the same device, a different key - and it never appears here,
 * because such a message is refused and there is no line to mark. It arrives as
 * a notice instead, with both fingerprints in it.
 */
export type Identity = 'new' | 'known' | 'changed';

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
    /** XEP-0461: the id of the message this one answers */
    repliesTo:  string | null;
    /**
     * The quoted lines an answer came with, taken out of the body.
     *
     * An answer carries the text it answers a second time, as "> " lines, for
     * clients that cannot follow the reference. This one can, so the duplicate
     * is kept here rather than in the body - but kept, because it is sometimes
     * the only copy of what is being answered.
     */
    quote:      string | null;
    corrected:  boolean;
    delivered:  boolean;
    displayed:  boolean;
    /** XEP-0384: whether this line travelled encrypted */
    encrypted:  boolean;
    /** how the sending device's key stood; null for a line that came in the clear */
    identity:   Identity | null;
    /** the file this message handed over, once it has been fetched */
    media:      Media | null;
}

/**
 * XEP-0384: what this side can do with encryption.
 *
 * Reading and writing stay separate although they are the same answer today -
 * they are different capabilities, and one of them could stop working on its
 * own. Neither says whether a *particular* message will be encrypted: that
 * depends on the devices at the far end and on the switch for that
 * conversation, and every line says for itself.
 */
export interface Omemo {
    /** whether OMEMO is configured at all (--no-omemo turns it off) */
    configured:   boolean;
    /** whether this device is announced and can read encrypted messages */
    receiving:    boolean;
    /** whether this app encrypts what it sends - false, and it says so */
    sending:      boolean;
    deviceId:     number | null;
    /** this device's own fingerprint, for somebody to compare */
    fingerprint:  string | null;
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
    /**
     * XEP-0384: 'auto' encrypts whenever the far end can read it, 'off' is
     * somebody having said not to for this conversation.
     *
     * There is no 'on': this app encrypts when it can and writes in the clear
     * when it cannot, and every line says which of the two it was.
     */
    encryption:      'auto' | 'off';
    /**
     * XEP-0084: the id of this contact's picture, or null when they have none
     * this app kept.
     *
     * The id is the SHA-1 of the bytes, which is why it is the id and not an
     * address: it changes exactly when the face does, so `avatarURL` builds an
     * address that can be cached forever and is never stale.
     */
    avatar:          string | null;
}

/** The XMPP connection of the web app. */
export interface Connection {
    state:             ConnectionState;
    jid:               string;
    fullJid:           string | null;
    websocket:         string | null;
    carbons:           boolean;
    streamManagement:  boolean;
    /** absent while no account is configured */
    omemo?:            Omemo;
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
/** The signed-in account, as HTTPExtAPI answers it. */
export interface Me {
    user:     { id: string; name?: string; passkeys?: number };
    session:  { createdAt: string; expiresAt: string } | null;
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


async function request<T>(method: string, path: string, body?: unknown, base: string = config.apiBase): Promise<T> {

    const headers: Record<string, string> = { 'Accept': 'application/json' };

    if (body !== undefined)
        headers['Content-Type'] = 'application/json';

    // Same origin, so the session cookie travels with every request.
    const response = await fetch(base + path, {
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


/**
 * Posts raw bytes and reads the JSON answer.
 *
 * Apart from `request` because the two differ in exactly the thing `request`
 * takes for granted: there is no JSON to stringify, and the Content-Type is the
 * file's rather than application/json. Folding that into `request` would mean a
 * branch in the one function every other call goes through.
 */
async function sendBytes<T>(path: string, file: File, method: string = 'POST'): Promise<T> {

    const response = await fetch(config.apiBase + path, {
                               method,
                               // Whatever the browser made of the file, or
                               // nothing: the server does not believe it either
                               // way - it works the type out from the name, and
                               // an upload service is told octet-stream when the
                               // file is encrypted.
                               headers:      { 'Accept': 'application/json',
                                               'Content-Type': file.type.length > 0 ? file.type : 'application/octet-stream' },
                               credentials:  'same-origin',
                               body:         file
                           });

    if (response.status === 401)
        unauthorizedHandler?.();

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

export const api = {

    /** The Server-Sent Events stream; the browser sends the session cookie along. */
    eventsURL:  `${config.apiBase}/events`,

    auth: {
        me:              ()                                  => request<Me>  ('GET',  '/auth/me',       undefined,                     config.authBase),
        // "login" and not "username": HTTPExtAPI takes the e-mail address just
        // as well, so the field is named after what it is for.
        login:           (login: string, password: string)   => request<Me>  ('POST', '/auth/login',    { login, password },           config.authBase),
        logout:          ()                                  => request<void>('POST', '/auth/logout',   undefined,                     config.authBase),
        rename:          (displayName: string)               => request<Me>  ('PUT',  '/auth/me',       { displayName },               config.authBase),
        changePassword:  (update: PasswordUpdate)            => request<void>('POST', '/auth/password', update,                        config.authBase)
    },

    /**
     * The passkey ceremonies. These routes exist only when the server could
     * name an origin a browser will accept, so a 404 here is an answer and not
     * a fault - see passkeys.ts.
     */
    passkeys: {
        list:             ()                       => request<{ passkeys: Passkey[] }>('GET',    '/auth/passkeys',                 undefined, config.authBase),
        registerOptions:  ()                       => request<PasskeyCeremony>        ('POST',   '/auth/passkeys/register/options', {},       config.authBase),
        register:         (body: unknown)          => request<{ passkey: Passkey }>   ('POST',   '/auth/passkeys/register',         body,     config.authBase),
        loginOptions:     (login?: string)         => request<PasskeyCeremony>        ('POST',   '/auth/passkeys/login/options',    login !== undefined ? { login } : {}, config.authBase),
        login:            (body: unknown)          => request<Me>                     ('POST',   '/auth/passkeys/login',            body,     config.authBase),
        rename:           (id: string, name: string) => request<{ passkey: Passkey }> ('PUT',    `/auth/passkeys/${encodeURIComponent(id)}`, { name }, config.authBase),
        remove:           (id: string)             => request<void>                   ('DELETE', `/auth/passkeys/${encodeURIComponent(id)}`, undefined, config.authBase)
    },

    status:     ()  => request<Status>('GET',  '/status'),
    reconnect:  ()  => request<{ connection: Connection }>('POST', '/connection/reconnect'),

    account: {
        get:     ()                       => request<AccountResponse>('GET',    '/account'),
        save:    (account: AccountUpdate, confirm: Confirmation)  => request<AccountResponse>('PUT',    '/account', { ...account, confirm }),
        forget:  (confirm: Confirmation)                          => request<AccountResponse>('DELETE', '/account', { confirm }),

        /**
         * XEP-0084: publishes a picture, or takes the published one down.
         *
         * No confirmation, unlike saving the account or forgetting it: those
         * two point this program at a server or hand it a password, and this
         * one changes a picture that can be changed back. What it does do is
         * tell everybody subscribed, which is the whole point of it.
         */
        setAvatar:     (file: File) => sendBytes<{ avatar: string; type: string; bytes: number }>('/account/avatar', file, 'PUT'),
        removeAvatar:  ()           => request<{ avatar: null }>('DELETE', '/account/avatar')
    },

    /**
     * XEP-0084: where a face is served from.
     *
     * By the id, which is the SHA-1 of the bytes - so this address means one
     * particular picture for ever and the browser is told to keep it for ever.
     * Same origin, so the session cookie travels with it and no other host is
     * ever asked who is looking at whom.
     */
    avatarURL:  (id: string) => `${config.apiBase}/avatars/${encodeURIComponent(id)}`,



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

        /**
         * Sends a file (XEP-0363, and XEP-0454 when the conversation is
         * encrypted - the server decides that, not this).
         *
         * The bytes go as the body and the name as a query parameter, rather
         * than as a multipart form: there is one file and one field, and
         * multipart would be a parser for a shape nothing here needs.
         */
        sendFile:  (chat: string, file: File)                => sendBytes<{ seq: number; message: Message }>(`/chats/${jid(chat)}/files?name=${encodeURIComponent(file.name)}`, file),
        read:      (chat: string)                            => request<void>                ('POST', `/chats/${jid(chat)}/read`),
        state:     (chat: string, state: ChatState)          => request<void>                ('POST', `/chats/${jid(chat)}/state`, { state }),
        contact:   (chat: string, action: ContactAction)     => request<void>                ('POST', `/chats/${jid(chat)}/contact`, { action }),

        /** XEP-0384: turn encryption off for this conversation, or back on. */
        encryption: (chat: string, enabled: boolean)         => request<{ seq: number; encryption: 'auto' | 'off' }>('POST', `/chats/${jid(chat)}/encryption`, { enabled })
    }

};
