import { api } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { passkeysPossible, signIn as signInWithPasskey } from '../passkeys';
import { errorMessage, field, safeNext } from '../ui';

export const loginPage: Page = {

    title: 'Sign in',

    render({ root, url, navigate }) {

        const next = safeNext(url.searchParams.get('next')) ?? '/';

        if (auth.user) {
            navigate(next, true);
            return;
        }

        render(root, html`
            <section class="login">

                <h1><i class="fa-solid fa-comments"></i> XMPP WebApp</h1>
                <p class="muted">Sign in to see the chats of this account.</p>

                <form id="login-form" class="form-stack">
                    <label>Username
                        <input name="username" required autocomplete="username" autofocus />
                    </label>
                    <label>Password
                        <input name="password" type="password" required autocomplete="current-password" />
                    </label>
                    <div class="form-actions">
                        <button type="submit" class="btn primary">Sign in</button>
                        <span id="form-error" class="form-error" role="alert"></span>
                    </div>
                </form>

                <div id="passkey-area" hidden>
                    <p class="muted small">or</p>
                    <button id="passkey-button" type="button" class="btn">
                        <i class="fa-solid fa-key"></i> Sign in with a passkey
                    </button>
                </div>

            </section>
        `);

        const form    = must<HTMLFormElement>(root, '#login-form');
        const error   = must<HTMLElement>(root, '#form-error');
        const button  = must<HTMLButtonElement>(form, 'button[type="submit"]');

        // Only offered where it can work: a browser that has the API, on an
        // origin it considers trustworthy. The server has its own answer - it
        // registers no passkey routes when it cannot name an origin - and the
        // first click finds that out, which is why the button says what went
        // wrong rather than disappearing again.
        if (passkeysPossible()) {

            const area          = must<HTMLElement>(root, '#passkey-area');
            const passkeyButton = must<HTMLButtonElement>(root, '#passkey-button');

            area.hidden = false;

            passkeyButton.addEventListener('click', () => {

                error.textContent        = '';
                passkeyButton.disabled   = true;

                void (async () => {
                    try
                    {
                        // Without a username: an authenticator that holds a
                        // discoverable credential for this site offers it by
                        // itself, which is the whole point of the thing.
                        auth.set(await signInWithPasskey());
                        navigate(next, true);
                    }
                    catch (problem)
                    {
                        // A person who changed their mind at the system prompt
                        // has not made a mistake, so they are not told off.
                        error.textContent = problem instanceof DOMException && problem.name === 'NotAllowedError'
                                                ? ''
                                                : errorMessage(problem);
                        passkeyButton.disabled = false;
                    }
                })();

            });

        }

        form.addEventListener('submit', event => {

            event.preventDefault();
            error.textContent = '';
            button.disabled   = true;

            void (async () => {
                try
                {
                    auth.set(await api.auth.login(field(form, 'username'), field(form, 'password', false)));
                    navigate(next, true);
                }
                catch (problem)
                {
                    error.textContent = errorMessage(problem);
                    button.disabled   = false;
                }
            })();

        });

    }

};
