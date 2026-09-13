// What a message body may become on screen besides text.
//
// Everything here builds DOM nodes and sets their properties; nothing is ever
// assembled as an HTML string. A body is text until a rule below says
// otherwise, and the two rules are deliberately narrow:
//
//   - A body that is nothing but one https:// URL whose path ends in an image
//     extension is shown as the picture it points to. That is how a client
//     hands over an upload (XEP-0363): the body repeats the URL. A link inside
//     a sentence is a different act - somebody is pointing at something - and
//     stays a link. Only https, because the page may itself be served over TLS
//     and a browser would refuse the picture anyway, and because the
//     Content-Security-Policy of the page only opens img-src for https.
//
//   - http:// and https:// URLs inside the text become links, opened in a new
//     tab without a referrer and without a window handle back to this page.
//
// A javascript: or data: URL cannot pass either rule: both demand a scheme of
// http or https from the URL parser, not from a pattern.
//
// A third case comes from the server rather than from the body: when the web
// app has fetched the shared file into its archive, the message carries it, and
// then the copy is shown instead of the original. That is the better picture in
// every way - it is still there when the upload has expired, it works for an
// aesgcm:// URL no browser can open, and asking for it tells the host the file
// came from nothing about who is reading it.

const IMAGE_EXTENSIONS  = ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.avif', '.bmp'];

/** Deliberately not a general URL pattern: what is not http or https is text. */
const URL_PATTERN       = /\bhttps?:\/\/[^\s<>"]+/gi;


/** The picture a body consists of, or null when it is not one. */
export function imageURL(body: string): URL | null {

    const text = body.trim();

    if (text.length === 0 || /\s/.test(text))
        return null;

    let url: URL;

    try {
        url = new URL(text);
    }
    catch {
        return null;
    }

    if (url.protocol !== 'https:' || url.username !== '' || url.password !== '')
        return null;

    const path = url.pathname.toLowerCase();

    return IMAGE_EXTENSIONS.some(extension => path.endsWith(extension))
               ? url
               : null;

}


/** The text with its http(s) links as anchors, as DOM nodes. */
export function linkify(text: string): DocumentFragment {

    const fragment = document.createDocumentFragment();
    let   last     = 0;

    for (const match of text.matchAll(URL_PATTERN))
    {

        const start    = match.index;
        const trimmed  = trimTrailingPunctuation(match[0]);
        const url      = parseHTTP(trimmed);

        if (url === null || trimmed.length === 0)
            continue;

        if (start > last)
            fragment.appendChild(document.createTextNode(text.slice(last, start)));

        fragment.appendChild(anchor(url, trimmed));

        last = start + trimmed.length;

    }

    if (last < text.length)
        fragment.appendChild(document.createTextNode(text.slice(last)));

    return fragment;

}


/**
 * A file of a conversation as the page needs it: where it is served from here,
 * what it is, and where it came from. Built by whoever knows the API; this
 * module only decides what to make of it.
 */
export interface EmbeddedMedia {
    /** the address it is served from by this web app */
    url:          string;
    contentType:  string;
    name:         string;
    /** the address it was shared under, or null */
    source:       string | null;
}


/** Whether a body is one URL and nothing else - of any scheme, aesgcm included. */
export function isLoneURL(body: string): boolean {

    const text = body.trim();

    if (text.length === 0 || /\s/.test(text))
        return false;

    try {
        new URL(text);
        return true;
    }
    catch {
        return false;
    }

}


/** A body as an element: the file it handed over, the picture it is, or its text with links. */
export function renderBody(body: string, media?: EmbeddedMedia | null): HTMLElement {

    const container      = document.createElement('div');
    container.className  = 'body';

    if (media !== undefined && media !== null)
    {

        // The URL that only repeats what is being shown is not shown as well;
        // a sentence around it is.
        if (!isLoneURL(body) && body.trim().length > 0)
        {
            const text = document.createElement('div');
            text.className = 'body-text';
            text.appendChild(linkify(body));
            container.appendChild(text);
        }

        container.appendChild(mediaElement(media, container));

        return container;

    }

    const image = imageURL(body);

    if (image !== null)
    {

        container.classList.add('image');

        // The link holds the picture and nothing else; the URL only appears
        // as text when the picture cannot be shown.
        const link  = anchor(image, '');
        const img   = document.createElement('img');

        img.src             = image.href;
        img.alt             = image.href;
        img.loading         = 'lazy';
        img.decoding        = 'async';
        img.referrerPolicy  = 'no-referrer';

        // Not a picture after all (gone, refused, not an image): the link
        // stays, the frame around a broken image goes.
        img.addEventListener('error', () => {
            container.classList.remove('image');
            link.replaceChildren(document.createTextNode(image.href));
        }, { once: true });

        link.appendChild(img);
        container.appendChild(link);

        return container;

    }

    container.appendChild(linkify(body));

    return container;

}


/**
 * The stored file as an element: a picture, a player, or a link to download it.
 *
 * Everything here points at this web app's own address for the file, never at
 * the one it came from - so no host outside learns that somebody is reading
 * this conversation. The URL is not parsed or validated here because it was not
 * built from the message: it is this page's own API path with the file name
 * percent-encoded into it.
 */
function mediaElement(media: EmbeddedMedia, container: HTMLElement): HTMLElement {

    const type = media.contentType.toLowerCase();

    if (type.startsWith('image/'))
    {

        container.classList.add('image');

        const link = document.createElement('a');
        link.href            = media.url;
        link.target          = '_blank';
        link.rel             = 'noopener noreferrer';
        link.referrerPolicy  = 'no-referrer';

        const img = document.createElement('img');
        img.src        = media.url;
        img.alt        = media.name;
        img.loading    = 'lazy';
        img.decoding   = 'async';

        // A file that is in the archive but no longer readable: the frame goes,
        // the name stays, so that it is clear what is missing.
        img.addEventListener('error', () => {
            container.classList.remove('image');
            link.replaceChildren(document.createTextNode(media.name));
        }, { once: true });

        link.appendChild(img);

        return link;

    }

    if (type.startsWith('video/') || type.startsWith('audio/'))
    {

        container.classList.add(type.startsWith('video/') ? 'video' : 'audio');

        const player = document.createElement(type.startsWith('video/') ? 'video' : 'audio');
        player.src       = media.url;
        player.controls  = true;
        player.preload   = 'metadata';

        return player;

    }

    container.classList.add('file');

    const link = document.createElement('a');
    link.href         = media.url;
    link.download     = media.name;
    link.textContent  = media.name;
    link.rel          = 'noopener noreferrer';

    return link;

}


function anchor(url: URL, text: string): HTMLAnchorElement {

    const a = document.createElement('a');

    a.href            = url.href;
    a.textContent     = text;
    a.target          = '_blank';
    a.rel             = 'noopener noreferrer nofollow';
    a.referrerPolicy  = 'no-referrer';

    return a;

}


function parseHTTP(text: string): URL | null {

    try
    {

        const url = new URL(text);

        // "https://github.com@evil.example/" reads like one site and goes to
        // another: a URL with credentials in it is not made clickable.
        return (url.protocol === 'http:' || url.protocol === 'https:') && url.username === '' && url.password === ''
                   ? url
                   : null;

    }
    catch
    {
        return null;
    }

}


/**
 * Drops the punctuation a sentence leaves clinging to its last URL: a full
 * stop after an address ends the sentence, not the address. A closing bracket
 * only counts as punctuation when no opening one precedes it in the URL, so
 * that "https://example.org/a_(b).jpg" stays whole.
 */
export function trimTrailingPunctuation(text: string): string {

    let end = text.length;

    while (end > 0)
    {

        const last = text[end - 1];

        if (last === '.' || last === ',' || last === ';' || last === ':' || last === '!' || last === '?' || last === '"' || last === "'")
            end--;

        else if (last === ')' && count(text.slice(0, end - 1), '(') < count(text.slice(0, end), ')'))
            end--;

        else
            break;

    }

    return text.slice(0, end);

}

function count(text: string, character: string): number {
    let n = 0;
    for (const c of text)
        if (c === character)
            n++;
    return n;
}
