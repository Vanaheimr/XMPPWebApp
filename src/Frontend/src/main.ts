import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { api, ApiError } from './api/client';
import { auth } from './auth';
import { store } from './chat/store';
import { config } from './config';
import { startLiveReload } from './devReload';
import { html, must, render } from './html';
import { Router } from './router';

import { accountPage }  from './pages/account';
import { chatPage }     from './pages/chat';
import { loginPage }    from './pages/login';
import { notFoundPage } from './pages/notFound';


const root = document.getElementById('app');

if (root === null)
    throw new Error("The '#app' element is missing!");

render(root, html`
    <main id="page" class="page"></main>
    ${config.devMode ? html`<span class="dev-badge" title="Served from disk with live reload">dev</span>` : ''}
`);

const router = new Router({
    routes: [
        { path: '/',            page: chatPage,     guard: auth.requireSignIn },
        { path: '/chats/:jid',  page: chatPage,     guard: auth.requireSignIn },
        { path: '/account',     page: accountPage,  guard: auth.requireSignIn },
        { path: '/login',       page: loginPage }
    ],
    outlet:       must<HTMLElement>(root, '#page'),
    notFound:     notFoundPage,
    titleSuffix:  ' · XMPP WebApp'
});

// Signed out - by the button, or because the session expired and a request
// came back with 401: close the stream, forget the chats, show the sign-in.
auth.onChange(user => {

    if (user !== null)
        return;

    store.stop();

    if (location.pathname !== '/login')
        router.navigate(auth.requireSignIn(new URL(location.href)) ?? '/login', true);

});

// The account can go away at runtime (forgotten on the account page, or never
// set up): a chat with no account behind it belongs on the account page.
store.onChange(event => {

    if (event.type !== 'connection')
        return;

    if (store.connection?.state === 'unconfigured' &&
        location.pathname !== '/account' &&
        location.pathname !== '/login')
    {
        router.navigate('/account', true);
    }

});

// Find out who is signed in, and whether an account is configured, before the
// first page renders - so a fresh start opens on the account page and a
// configured one goes straight to the chat.
void (async () => {

    await auth.refresh();

    if (auth.user && location.pathname === '/')
    {
        try
        {
            const account = await api.account.get();

            if (!account.configured)
                history.replaceState(null, '', '/account');
        }
        catch (error)
        {
            // A 401 is handled by the auth flow; anything else, the chat page
            // and its banner can still explain.
            if (!(error instanceof ApiError && error.isUnauthorized))
                console.warn('Could not read the account:', error);
        }
    }

    router.start();

})();

// "dotnet run -- --dev": reload after "npm run watch" wrote a new bundle.
if (config.devMode)
    startLiveReload();
