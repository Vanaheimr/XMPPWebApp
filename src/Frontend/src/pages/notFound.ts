import { html, render } from '../html';
import type { Page } from '../router';

export const notFoundPage: Page = {

    title: 'Page not found',

    render({ root, url }) {

        render(root, html`
            <section class="login">
                <h1>Page not found</h1>
                <p class="muted">There is no page at <code>${url.pathname}</code>.</p>
                <p><a href="/">Back to the chats</a></p>
            </section>
        `);

    }

};
