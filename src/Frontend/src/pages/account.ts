import { api, saslMechanisms, type AccountResponse, type AccountUpdate, type Confirmation, type Me, type Omemo, type Passkey, type SaslMechanism } from '../api/client';
import { confirm as confirmWithPasskey, passkeysPossible, register as registerPasskey } from '../passkeys';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { errorMessage, field } from '../ui';

// The settings page: what a fresh start opens on, and what the gear in the
// chat comes back to. Two things are configured here, and they are kept apart
// because they are different in kind - the XMPP account is an account
// elsewhere, whose password this program must know in the clear to answer
// SCRAM; the web login is this program's own door, and only ever needs to
// recognise a password, so it is stored hashed.

export const accountPage: Page = {

    title: 'Settings',

    async render({ root, navigate }) {

        render(root, html`
            <section class="account">
                <h1><i class="fa-solid fa-gear"></i> Settings</h1>
                <p id="intro" class="muted">Loading …</p>
                <div id="xmpp-area"></div>
                <div id="avatar-area"></div>
                <div id="me-area"></div>
            </section>
        `);

        const intro      = must<HTMLElement>(root, '#intro');
        const xmppArea   = must<HTMLElement>(root, '#xmpp-area');
        const avatarArea = must<HTMLElement>(root, '#avatar-area');
        const loginArea  = must<HTMLElement>(root, '#me-area');

        let current: AccountResponse;
        let me: Me;

        try
        {
            [current, me] = await Promise.all([api.account.get(), api.auth.me()]);
        }
        catch (error)
        {
            render(xmppArea, html`<div class="error-box">Could not load the settings: ${errorMessage(error)}</div>`);
            intro.textContent = '';
            return;
        }

        renderXMPP(xmppArea, intro, current, navigate, () => {
            // Called after the account was forgotten: draw the page again from
            // scratch, so the form empties and the card loses its danger zone.
            void accountPage.render({ root, params: {}, url: new URL(location.href), navigate });
        });

        // Only with an account: a picture is published to a server, so there is
        // nothing to offer before there is one.
        if (current.configured && current.account !== null)
            renderAvatar(avatarArea, current.account.avatar);

        renderMe(loginArea, me);

        // After the login card, and only when this browser could use one. What
        // the server thinks is found out by asking: the routes are not there at
        // all when it has no origin to bind a passkey to.
        if (passkeysPossible())
            void renderPasskeys(loginArea);

    }

};


/**
 * What the two account routes ask for beside the session. A typed password when
 * one was typed; otherwise a passkey, which is a fingerprint rather than the
 * same password into the same page for the second time.
 */
async function confirmation(form: HTMLFormElement): Promise<Confirmation> {

    const typed = field(form, 'confirm', false);

    if (typed.length > 0)
        return { password: typed };

    if (passkeysPossible())
        return await confirmWithPasskey();

    // Neither: the server answers 403 and says what it wants, which is a better
    // sentence than one invented here.
    return { password: '' };

}


// ---------------------------------------------------------------------------
// Passkeys

async function renderPasskeys(after: HTMLElement): Promise<void> {

    const area = document.createElement('div');
    after.append(area);

    let passkeys: Passkey[];

    try
    {
        passkeys = (await api.passkeys.list()).passkeys;
    }
    catch (problem)
    {
        // 404: this server has no origin it can bind a passkey to, so there is
        // nothing to offer and nothing to apologise for either.
        area.remove();
        return;
    }

    const draw = () => {

        render(area, html`
            <div class="card">

                <h2>Passkeys</h2>

                <p class="muted small">
                    A second way in, beside the password - your fingerprint, your
                    face, or a security key. Nothing about the password changes:
                    a passkey is an addition, because an authenticator that goes
                    missing must not take the account with it.
                </p>

                ${passkeys.length === 0
                      ? html`<p class="muted small">None yet.</p>`
                      : html`<ul class="passkeys">${passkeys.map(passkey => html`
                            <li>
                                <span class="name">${passkey.name}</span>
                                <span class="muted small">
                                    added ${new Date(passkey.createdAt).toLocaleDateString()}${
                                        passkey.lastUsedAt
                                            ? `, last used ${new Date(passkey.lastUsedAt).toLocaleDateString()}`
                                            : ', never used'}
                                </span>
                                <button type="button" class="btn small danger" data-remove="${passkey.id}">Remove</button>
                            </li>
                        `)}</ul>`}

                <form id="passkey-form" class="form-stack" autocomplete="off">
                    <label>Name for the new passkey
                        <input name="name" required maxlength="60" placeholder="e.g. this laptop" />
                    </label>
                    <div class="form-actions">
                        <button type="submit" class="btn primary">
                            <i class="fa-solid fa-key"></i> Add a passkey
                        </button>
                        <span id="passkey-error" class="form-error" role="alert"></span>
                        <span id="passkey-ok" class="form-notice" role="status"></span>
                    </div>
                </form>

            </div>
        `);

        const form    = must<HTMLFormElement>(area, '#passkey-form');
        const error   = must<HTMLElement>(area, '#passkey-error');
        const ok      = must<HTMLElement>(area, '#passkey-ok');
        const button  = must<HTMLButtonElement>(form, 'button[type="submit"]');

        const reload = async () => { passkeys = (await api.passkeys.list()).passkeys; draw(); };

        form.addEventListener('submit', event => {

            event.preventDefault();
            error.textContent  = '';
            ok.textContent     = '';
            button.disabled    = true;

            void (async () => {
                try
                {
                    await registerPasskey(field(form, 'name'));
                    await reload();
                }
                catch (problem)
                {
                    // Changing one's mind at the system prompt is not an error.
                    error.textContent = problem instanceof DOMException && problem.name === 'NotAllowedError'
                                            ? ''
                                            : errorMessage(problem);
                    button.disabled = false;
                }
            })();

        });

        for (const remove of area.querySelectorAll<HTMLButtonElement>('button[data-remove]'))
            remove.addEventListener('click', () => {

                error.textContent = '';
                remove.disabled   = true;

                void (async () => {
                    try
                    {
                        await api.passkeys.remove(remove.dataset.remove ?? '');
                        await reload();
                    }
                    catch (problem)
                    {
                        error.textContent = errorMessage(problem);
                        remove.disabled   = false;
                    }
                })();

            });

    };

    draw();

}


// ---------------------------------------------------------------------------
// The XMPP account

function renderXMPP(area:      HTMLElement,
                    intro:     HTMLElement,
                    current:   AccountResponse,
                    navigate:  (path: string, replace?: boolean) => void,
                    forgotten: () => void): void {

    const account       = current.account;
    const configured    = current.configured;
    const fromArguments = current.source === 'arguments';

    intro.textContent = configured
        ? 'Change any setting and save to reconnect.'
        : 'No XMPP account is set up yet. Enter the account this web app should sign in to.';

    render(area, html`
        <div class="card">

            <h2>XMPP account</h2>

            ${fromArguments ? html`
                <div class="notice">
                    This account comes from the command line for this run. Saving here writes it to the
                    account file, and from the next start on the file is what counts.
                </div>` : ''}

            <form id="account-form" class="form-stack" autocomplete="off">

                <label>JID
                    <input name="jid" required placeholder="user@example.org"
                           value="${account?.jid ?? ''}" autocomplete="username" autofocus />
                </label>

                <label>Password
                    <input name="password" type="password" autocomplete="current-password"
                           placeholder="${account?.passwordSet ? 'Leave blank to keep the stored password' : 'Required'}" />
                    <span class="hint">Kept in the clear on the server: SCRAM needs it to compute the proof.
                        Blank keeps the stored one, except when the JID or the endpoint changes - a password
                        nobody was shown must not be sendable to a new address without being typed.</span>
                </label>

                <label>WebSocket endpoint <span class="muted">(optional)</span>
                    <input name="websocket" placeholder="wss://xmpp.example.org:5281/xmpp-websocket"
                           value="${account?.websocket ?? ''}" />
                    <span class="hint">Leave blank to ask the host-meta of the domain (XEP-0156).</span>
                </label>

                <label>Weakest SASL mechanism still accepted
                    <select name="minimumSasl">
                        ${saslMechanisms.map(mechanism => html`
                            <option value="${mechanism}" ${((account?.minimumSasl ?? 'SCRAM-SHA-256') === mechanism) ? 'selected' : ''}>${mechanism}</option>
                        `)}
                    </select>
                </label>

                <label class="checkbox">
                    <input name="allowInsecure" type="checkbox" ${account?.allowInsecure ? 'checked' : ''} />
                    Allow a plain <code>ws://</code> endpoint
                    <span class="hint">Only for a server on the same machine. Over <code>ws://</code> the login travels readable.</span>
                </label>

                <label class="checkbox">
                    <input name="trustAnnouncement" type="checkbox" ${account?.trustAnnouncement ? 'checked' : ''} />
                    Trust a changed SASL announcement (XEP-0474)
                    <span class="hint">Carry on when the server signs a different mechanism list than the one that arrived. Only if you know why.</span>
                </label>

                <div class="form-actions">
                    <button type="submit" class="btn primary">Save &amp; connect</button>
                    ${configured ? html`<a href="/" class="btn">Back to the chat</a>` : ''}
                    <span id="form-error" class="form-error" role="alert"></span>
                </div>

                <label>Your password here
                    <input name="confirm" type="password" autocomplete="current-password" />
                    <span class="hint">
                        The password of this page, not of the XMPP account. Asked before the
                        account is changed or forgotten: a browser left open must not be able
                        to point this somewhere else. Leave it blank to be asked for a passkey
                        instead, if you have one.
                    </span>
                </label>

            </form>

            <div id="connection" class="account-connection"></div>

            ${configured ? html`
                <div class="danger-zone">
                    <button type="button" id="forget" class="btn danger">Forget this account</button>
                    <span class="hint">Disconnects and deletes the account file. The next start asks again.</span>
                </div>` : ''}

        </div>
    `);

    const form    = must<HTMLFormElement>(area, '#account-form');
    const error   = must<HTMLElement>(area, '#form-error');
    const button  = must<HTMLButtonElement>(form, 'button[type="submit"]');

    renderConnection(must<HTMLElement>(area, '#connection'), current);

    form.addEventListener('submit', event => {

        event.preventDefault();
        error.textContent = '';
        button.disabled   = true;

        const update: AccountUpdate = {
            jid:                field(form, 'jid'),
            websocket:          field(form, 'websocket') || null,
            minimumSasl:        (field(form, 'minimumSasl') || 'SCRAM-SHA-256') as SaslMechanism,
            allowInsecure:      checked(form, 'allowInsecure'),
            trustAnnouncement:  checked(form, 'trustAnnouncement')
        };

        // An empty password keeps the stored one; the server understands that.
        const password = field(form, 'password', false);
        if (password.length > 0)
            update.password = password;

        void (async () => {
            try
            {
                const saved = await api.account.save(update, await confirmation(form));

                // Configured now: the chat is where the live connection banner
                // is, so that is where saving lands.
                if (saved.configured)
                    navigate('/', true);
                else
                    renderConnection(must<HTMLElement>(area, '#connection'), saved);
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
                button.disabled   = false;
            }
        })();

    });

    area.querySelector<HTMLButtonElement>('#forget')?.addEventListener('click', () => {

        if (!window.confirm('Forget this account? This disconnects and deletes the account file on the server.'))
            return;

        void (async () => {
            try
            {
                await api.account.forget(await confirmation(form));
                forgotten();
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
            }
        })();

    });

}


// ---------------------------------------------------------------------------
// XEP-0084: the picture this account publishes

/**
 * The own avatar: show it, replace it, take it down.
 *
 * A card of its own and not a field in the account form, and the reason is what
 * the two do. Saving the account points this program at a server and reconnects
 * it, which is why it asks for a password first; publishing a picture tells
 * everybody subscribed what one looks like and can be undone by clicking once
 * more. Putting them in one form would mean either asking for a password to
 * change a picture, or not asking for one to change a server.
 */
function renderAvatar(area: HTMLElement, current: string | null): void {

    let shown = current;

    const draw = (): void => {

        render(area, html`
            <div class="card">

                <h2>Your picture</h2>

                <p class="muted small">
                    Published over PEP (XEP-0084), which means everybody who has you in their
                    contacts and is subscribed to your presence is told about it. It is not a
                    secret and it is not encrypted: an avatar is the one thing in a chat account
                    meant to be seen by people you have not written to yet.
                </p>

                <div class="avatar-row">

                    ${shown !== null
                        ? html`<img class="avatar large" src="${api.avatarURL(shown)}" alt="Your picture" decoding="async">`
                        : html`<span class="avatar large initials" aria-hidden="true"><i class="fa-solid fa-user"></i></span>`}

                    <div class="avatar-actions">
                        <input type="file" id="avatar-file" accept="image/png,image/jpeg,image/gif,image/webp" hidden />
                        <button type="button" id="avatar-pick" class="btn">
                            <i class="fa-solid fa-image"></i> ${shown !== null ? 'Replace' : 'Choose a picture'}
                        </button>
                        ${shown !== null
                            ? html`<button type="button" id="avatar-remove" class="btn danger">Take it down</button>`
                            : ''}
                        <span class="hint">
                            PNG, JPEG, GIF or WebP, up to 256 KiB. It travels inside a stanza, so
                            small is the point - a picture the size of a photograph is a picture
                            every one of your contacts downloads.
                        </span>
                        <span id="avatar-error" class="form-error" role="alert"></span>
                    </div>

                </div>

            </div>
        `);

        const file   = must<HTMLInputElement>(area, '#avatar-file');
        const error  = must<HTMLElement>(area, '#avatar-error');

        must<HTMLButtonElement>(area, '#avatar-pick').addEventListener('click', () => file.click());

        file.addEventListener('change', () => {

            const picked = file.files?.[0];

            // The input is cleared before anything else, so picking the same
            // file again after a failure still fires a change event.
            file.value = '';

            if (picked === undefined)
                return;

            error.textContent = 'Publishing …';

            void (async () => {
                try
                {
                    shown = (await api.account.setAvatar(picked)).avatar;
                    draw();
                }
                catch (problem)
                {
                    error.textContent = errorMessage(problem);
                }
            })();

        });

        area.querySelector<HTMLButtonElement>('#avatar-remove')?.addEventListener('click', () => {

            if (!window.confirm('Take your picture down? Everybody subscribed is told.'))
                return;

            error.textContent = 'Taking it down …';

            void (async () => {
                try
                {
                    await api.account.removeAvatar();
                    shown = null;
                    draw();
                }
                catch (problem)
                {
                    error.textContent = errorMessage(problem);
                }
            })();

        });

    };

    draw();

}


// ---------------------------------------------------------------------------
// The account of this page - not the XMPP one, which is further down

function renderMe(area: HTMLElement, me: Me): void {

    render(area, html`
        <div class="card">

            <h2>Your account</h2>

            <p class="muted small">
                Who may open this page. The password is kept as a hash in the account
                database, so a lost file does not hand anybody the password. The
                username itself does not change - it is what the account is called.
            </p>

            <form id="me-form" class="form-stack" autocomplete="off">

                <label>Username
                    <input name="username" value="${me.user.id}" autocomplete="username" disabled />
                </label>

                <label>Current password
                    <input name="currentPassword" type="password" required autocomplete="current-password" />
                    <span class="hint">Asked for every change: an open browser must not be able to lock you out.</span>
                </label>

                <label>New password
                    <input name="newPassword" type="password" required autocomplete="new-password" />
                    <span class="hint">At least 8 characters. Changing it ends every other session.</span>
                </label>

                <div class="form-actions">
                    <button type="submit" class="btn primary">Change the password</button>
                    <span id="me-error" class="form-error" role="alert"></span>
                    <span id="me-ok" class="form-notice" role="status"></span>
                </div>

            </form>

        </div>
    `);

    const form    = must<HTMLFormElement>(area, '#me-form');
    const error   = must<HTMLElement>(area, '#me-error');
    const ok      = must<HTMLElement>(area, '#me-ok');
    const button  = must<HTMLButtonElement>(form, 'button[type="submit"]');

    form.addEventListener('submit', event => {

        event.preventDefault();
        error.textContent  = '';
        ok.textContent     = '';
        button.disabled    = true;

        void (async () => {
            try
            {
                await api.auth.changePassword({
                          currentPassword:  field(form, 'currentPassword', false),
                          newPassword:      field(form, 'newPassword',     false)
                      });

                form.reset();

                ok.textContent = 'Changed. Every other session has ended.';
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
            }
            finally
            {
                button.disabled = false;
            }
        })();

    });

}


// ---------------------------------------------------------------------------

function renderConnection(target: HTMLElement, response: AccountResponse): void {

    const connection = response.connection;

    if (!response.configured) {
        target.replaceChildren();
        return;
    }

    const dot = connection.state === 'connected' ? 'available' : 'offline';

    render(target, html`
        <div class="facts">
            <div><span class="dot ${dot}"></span> ${connectionLabel(response)}</div>
            ${connection.error ? html`<div class="conn-error">${connection.error}</div>` : ''}
            ${omemoFacts(connection.omemo)}
        </div>
    `);

}

/**
 * XEP-0384, said in the two sentences it takes to be true.
 *
 * Receiving and sending are named separately because they differ, and the
 * fingerprint is here because it is the only thing anybody can do anything
 * with: read it out to the person at the other end, and from then on a key
 * that changes is a key that changed.
 */
function omemoFacts(omemo: Omemo | undefined): HTMLFragment {

    if (omemo === undefined || !omemo.configured)
        return html`<div class="conn-omemo">Encryption off: messages sent to this device encrypted cannot be read.</div>`;

    if (!omemo.receiving)
        return html`<div class="conn-omemo">Encryption not ready yet \u2013 this device has not been announced to the server.</div>`;

    return html`
        <div class="conn-omemo">
            \u{1F512} Encrypted whenever the far end can read it, in the clear when it cannot\u200a\u2014\u200aand
            the lock beside a line says which of the two that line was. The padlock in a conversation turns it off for that contact.
        </div>
        <div class="conn-fingerprint" title="Read this out to the person at the other end. Once they have it, a key that changes is a key that changed.">
            This device: <code>${fingerprintGroups(omemo.fingerprint)}</code>
        </div>
    `;

}

/**
 * A fingerprint in groups of eight, because sixty-four hex characters in one
 * run is not something a human being compares - and comparing it is the only
 * thing it is for.
 */
function fingerprintGroups(Fingerprint: string | null): string {

    if (Fingerprint === null)
        return '\u2014';

    return (Fingerprint.match(/.{1,8}/g) ?? [Fingerprint]).join(' ');

}

function connectionLabel(response: AccountResponse): string {

    switch (response.connection.state) {
        case 'connected':     return `Connected as ${response.connection.fullJid ?? response.connection.jid}`;
        case 'connecting':    return 'Connecting …';
        case 'reconnecting':  return 'Connection lost, reconnecting …';
        case 'unconfigured':  return 'No account configured';
        default:              return 'Not connected';
    }

}

function checked(form: HTMLFormElement, name: string): boolean {
    return form.querySelector<HTMLInputElement>(`input[name="${name}"]`)?.checked ?? false;
}
