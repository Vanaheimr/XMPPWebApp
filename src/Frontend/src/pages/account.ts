import { api, saslMechanisms, type AccountResponse, type AccountUpdate, type SaslMechanism, type WebLogin, type WebLoginUpdate } from '../api/client';
import { html, must, render } from '../html';
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
                <div id="weblogin-area"></div>
            </section>
        `);

        const intro     = must<HTMLElement>(root, '#intro');
        const xmppArea  = must<HTMLElement>(root, '#xmpp-area');
        const loginArea = must<HTMLElement>(root, '#weblogin-area');

        let current: AccountResponse;
        let webLogin: WebLogin;

        try
        {
            [current, webLogin] = await Promise.all([api.account.get(), api.webLogin.get()]);
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

        renderWebLogin(loginArea, webLogin);

    }

};


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
                    <span class="hint">Kept in the clear on the server: SCRAM needs it to compute the proof.</span>
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
                const saved = await api.account.save(update);

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
                await api.account.forget();
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
// The web login

function renderWebLogin(area: HTMLElement, login: WebLogin): void {

    render(area, html`
        <div class="card">

            <h2>Web login</h2>

            <p class="muted small">
                Who may open this page. Kept as a hash in <code>${login.file}</code>,
                so a lost file does not hand anybody the password.
            </p>

            <form id="weblogin-form" class="form-stack" autocomplete="off">

                <label>Username
                    <input name="username" required value="${login.username}" autocomplete="username" />
                </label>

                <label>Current password
                    <input name="currentPassword" type="password" required autocomplete="current-password" />
                    <span class="hint">Asked for every change: an open browser must not be able to lock you out.</span>
                </label>

                <label>New password <span class="muted">(optional)</span>
                    <input name="newPassword" type="password" autocomplete="new-password"
                           placeholder="Leave blank to change only the username" />
                    <span class="hint">At least 8 characters. Changing it ends every other session.</span>
                </label>

                <div class="form-actions">
                    <button type="submit" class="btn primary">Change the web login</button>
                    <span id="weblogin-error" class="form-error" role="alert"></span>
                    <span id="weblogin-ok" class="form-notice" role="status"></span>
                </div>

            </form>

        </div>
    `);

    const form    = must<HTMLFormElement>(area, '#weblogin-form');
    const error   = must<HTMLElement>(area, '#weblogin-error');
    const ok      = must<HTMLElement>(area, '#weblogin-ok');
    const button  = must<HTMLButtonElement>(form, 'button[type="submit"]');

    form.addEventListener('submit', event => {

        event.preventDefault();
        error.textContent  = '';
        ok.textContent     = '';
        button.disabled    = true;

        const update: WebLoginUpdate = {
            currentPassword:  field(form, 'currentPassword', false),
            username:         field(form, 'username')
        };

        const newPassword = field(form, 'newPassword', false);
        if (newPassword.length > 0)
            update.newPassword = newPassword;

        void (async () => {
            try
            {
                const saved = await api.webLogin.save(update);

                form.reset();
                must<HTMLInputElement>(form, 'input[name="username"]').value = saved.username;

                ok.textContent = saved.sessionsEnded
                                     ? `Saved. ${saved.sessionsEnded} other session(s) ended.`
                                     : 'Saved.';
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
        </div>
    `);

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
