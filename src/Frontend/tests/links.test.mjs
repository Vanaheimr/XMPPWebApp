// What a message body may become on screen - the two rules in src/chat/links.ts,
// checked without a browser. Node strips the types of the imported .ts file
// itself (Node 22.18+); the DOM the functions build is a small stand-in
// defined below, enough to see which nodes come out and with which attributes.
//
//   npm test

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { imageURL, isLoneURL, linkify, renderBody, trimTrailingPunctuation } from '../src/chat/links.ts';


// A stand-in for the few DOM calls links.ts makes: elements with properties,
// text nodes, fragments. No innerHTML on purpose - if links.ts ever reached
// for it, these tests would fail to build the node, which is the point.
class FakeNode {
    constructor(type, text = '') {
        this.nodeType   = type;
        this.textContent = text;
        this.children   = [];
        this.className  = '';
        this.listeners  = {};
        this.classList  = {
            add:    name => { this.className = (this.className + ' ' + name).trim(); },
            remove: name => { this.className = this.className.split(' ').filter(c => c !== name).join(' '); },
            contains: name => this.className.split(' ').includes(name)
        };
    }
    appendChild(node)          { this.children.push(node); return node; }
    replaceChildren(...nodes)  { this.children = nodes; }
    addEventListener(name, fn) { (this.listeners[name] ??= []).push(fn); }
    querySelectorAll()         { return []; }
    get text() {
        return this.nodeType === 3 || this.children.length === 0
                   ? this.textContent
                   : this.children.map(child => child.text).join('');
    }
}

globalThis.document = {
    createElement:        tag  => Object.assign(new FakeNode(1), { tagName: tag.toUpperCase() }),
    createTextNode:       text => new FakeNode(3, text),
    createDocumentFragment: () => new FakeNode(11)
};


describe('imageURL: a body that is nothing but one https picture URL', () => {

    it('is the picture', () => {
        const url = imageURL('https://upload.example.org/abc/photo.jpg');
        assert.equal(url?.href, 'https://upload.example.org/abc/photo.jpg');
    });

    it('may be surrounded by whitespace and use any case in the extension', () => {
        assert.equal(imageURL('  https://x.example/a.PNG\n')?.href, 'https://x.example/a.PNG');
        assert.equal(imageURL('https://x.example/a.webp?size=large')?.href, 'https://x.example/a.webp?size=large');
    });

    it('is not a picture when it is a sentence, two URLs, or a link without an image extension', () => {
        assert.equal(imageURL('look at https://x.example/a.jpg'), null);
        assert.equal(imageURL('https://x.example/a.jpg https://x.example/b.jpg'), null);
        assert.equal(imageURL('https://x.example/page.html'), null);
        assert.equal(imageURL('https://x.example/a.jpg.html'), null);
    });

    it('is only ever https - not http, not data, not javascript, not aesgcm', () => {
        assert.equal(imageURL('http://x.example/a.jpg'), null);
        assert.equal(imageURL('data:image/png;base64,iVBORw0KGgo=.png'), null);
        assert.equal(imageURL('javascript:alert(1)//.png'), null);
        assert.equal(imageURL('aesgcm://x.example/a.jpg#' + 'a'.repeat(88)), null);
    });

    it('refuses credentials inside the URL', () => {
        assert.equal(imageURL('https://user:pw@x.example/a.jpg'), null);
    });

});


describe('trimTrailingPunctuation', () => {

    it('drops the full stop a sentence leaves behind', () => {
        assert.equal(trimTrailingPunctuation('https://a.org/b.jpg.'), 'https://a.org/b.jpg');
        assert.equal(trimTrailingPunctuation('https://a.org/b?'), 'https://a.org/b');
    });

    it('keeps a bracket that was opened inside the URL, drops one that was not', () => {
        assert.equal(trimTrailingPunctuation('https://a.org/b_(c).jpg'), 'https://a.org/b_(c).jpg');
        assert.equal(trimTrailingPunctuation('https://a.org/b.jpg)'), 'https://a.org/b.jpg');
    });

});


describe('linkify', () => {

    it('turns http and https URLs into anchors and leaves the rest as text', () => {

        const fragment  = linkify('see https://a.org/x. and http://b.org/y, ok?');
        const anchors   = fragment.children.filter(node => node.tagName === 'A');

        assert.equal(fragment.text, 'see https://a.org/x. and http://b.org/y, ok?');
        assert.deepEqual(anchors.map(a => a.href), ['https://a.org/x', 'http://b.org/y']);
        assert.deepEqual(anchors.map(a => a.textContent), ['https://a.org/x', 'http://b.org/y']);

        for (const a of anchors) {
            assert.equal(a.target, '_blank');
            assert.equal(a.rel, 'noopener noreferrer nofollow');
        }

    });

    it('leaves a URL with credentials in it as text', () => {

        const fragment = linkify('see https://github.com@evil.example/login and https://a.org/');
        const anchors  = fragment.children.filter(node => node.tagName === 'A');

        assert.deepEqual(anchors.map(a => a.href), ['https://a.org/']);
        assert.equal(fragment.text, 'see https://github.com@evil.example/login and https://a.org/');

    });

    it('never makes a link out of javascript:, data: or markup', () => {

        const fragment = linkify('javascript:alert(1) data:text/html,hi <a href="https://x.example">x</a> <script>alert(2)</script>');
        const anchors  = fragment.children.filter(node => node.tagName === 'A');

        // The one https URL inside the pasted markup becomes a link - to the
        // URL, as text, without the markup around it doing anything.
        assert.deepEqual(anchors.map(a => a.href), ['https://x.example/']);
        assert.equal(fragment.text, 'javascript:alert(1) data:text/html,hi <a href="https://x.example">x</a> <script>alert(2)</script>');

    });

});


describe('renderBody', () => {

    it('shows a picture for a body that is one image URL, wrapped in a link to it', () => {

        const body  = renderBody('https://x.example/a.jpg');
        const link  = body.children[0];
        const img   = link.children[0];

        assert.equal(body.className, 'body image');
        assert.equal(link.tagName, 'A');
        assert.equal(link.href, 'https://x.example/a.jpg');
        assert.equal(img.tagName, 'IMG');
        assert.equal(img.src, 'https://x.example/a.jpg');
        assert.equal(img.referrerPolicy, 'no-referrer');
        assert.equal(img.loading, 'lazy');
        assert.ok(img.listeners.error?.length === 1, 'a broken picture falls back to the link');

    });

    it('falls back to the link when the picture does not load', () => {

        const body  = renderBody('https://x.example/gone.png');
        const link  = body.children[0];

        link.children[0].listeners.error[0]();

        assert.equal(body.className, 'body');
        assert.equal(link.text, 'https://x.example/gone.png');

    });

    it('renders everything else as text with links', () => {

        const body = renderBody('<img src=x onerror=alert(1)> and https://x.example/');

        assert.equal(body.className, 'body');
        assert.equal(body.text, '<img src=x onerror=alert(1)> and https://x.example/');
        assert.equal(body.children.filter(node => node.tagName === 'IMG').length, 0);
        assert.equal(body.children[0].children.filter(node => node.tagName === 'A').length, 1);

    });

});


describe('renderBody with a file the archive fetched', () => {

    const MEDIA_URL = '/api/v1/chats/alice%40example.org/media/20260913T071848Z_photo.jpg';

    const media = contentType => ({
        url:          MEDIA_URL,
        contentType,
        name:         '20260913T071848Z_photo.jpg',
        source:       'https://upload.example.org/x/photo.jpg'
    });

    it('shows the copy this web app holds, never the address it came from', () => {

        const body  = renderBody('https://upload.example.org/x/photo.jpg', media('image/jpeg'));
        const link  = body.children[0];
        const img   = link.children[0];

        assert.equal(body.className, 'body image');
        assert.equal(link.tagName, 'A');
        assert.equal(link.href, MEDIA_URL);
        assert.equal(img.tagName, 'IMG');
        assert.equal(img.src, MEDIA_URL);

        // The URL the file was shared under does not appear anywhere on the
        // page: asking for it would tell that host who is reading.
        assert.ok(!link.href.includes('upload.example.org'));
        assert.ok(!body.text.includes('upload.example.org'));

    });

    it('works for an aesgcm URL, which no browser can open', () => {

        const body = renderBody('aesgcm://upload.example.org/x/photo.jpg#0123456789abcdef', media('image/jpeg'));

        assert.equal(body.className, 'body image');
        assert.equal(body.children[0].children[0].src, MEDIA_URL);
        assert.ok(!body.text.includes('0123456789abcdef'), 'the key in the fragment is not put on the page');

    });

    it('keeps a sentence written beside the file, and drops a body that only repeats the URL', () => {

        const withText = renderBody('Schau mal: https://upload.example.org/x/photo.jpg', media('image/jpeg'));
        const without  = renderBody('  https://upload.example.org/x/photo.jpg\n', media('image/jpeg'));

        assert.ok(withText.text.includes('Schau mal:'));
        assert.equal(withText.children.length, 2, 'the text and the picture');

        assert.equal(without.children.length, 1, 'nothing but the picture');

    });

    it('plays a video or a sound instead of showing it', () => {

        const video = renderBody('https://upload.example.org/x/clip.mp4', media('video/mp4'));
        const audio = renderBody('https://upload.example.org/x/note.opus', media('audio/opus'));

        assert.equal(video.className, 'body video');
        assert.equal(video.children[0].tagName, 'VIDEO');
        assert.equal(video.children[0].src, MEDIA_URL);
        assert.equal(video.children[0].controls, true);

        assert.equal(audio.className, 'body audio');
        assert.equal(audio.children[0].tagName, 'AUDIO');

    });

    it('offers anything else as a download rather than opening it', () => {

        const body = renderBody('https://upload.example.org/x/thing', media('application/octet-stream'));
        const link = body.children[0];

        assert.equal(body.className, 'body file');
        assert.equal(link.tagName, 'A');
        assert.equal(link.download, '20260913T071848Z_photo.jpg');
        assert.equal(link.textContent, '20260913T071848Z_photo.jpg');

    });

    it('is the ordinary rendering again when there is no file', () => {

        assert.equal(renderBody('https://x.example/a.jpg', null).className, 'body image');
        assert.equal(renderBody('just text', null).className, 'body');

    });

});


describe('isLoneURL', () => {

    it('is true for one URL of any scheme and nothing else', () => {
        assert.equal(isLoneURL('https://x.example/a.jpg'), true);
        assert.equal(isLoneURL('  aesgcm://x.example/a.jpg#ab \n'), true);
    });

    it('is false for a sentence, for two URLs and for what is not a URL at all', () => {
        assert.equal(isLoneURL('look at https://x.example/a.jpg'), false);
        assert.equal(isLoneURL('https://a.example/ https://b.example/'), false);
        assert.equal(isLoneURL(''), false);
        assert.equal(isLoneURL('hello'), false);
    });

});
