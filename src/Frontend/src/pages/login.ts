import { api } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
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

            </section>
        `);

        const form    = must<HTMLFormElement>(root, '#login-form');
        const error   = must<HTMLElement>(root, '#form-error');
        const button  = must<HTMLButtonElement>(form, 'button[type="submit"]');

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
