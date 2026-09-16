// XEP-0045: the room view, checked without a browser.
//
//   npm test
//
// Two kinds of check, because the room view has two kinds of thing worth
// pinning and only one of them can be imported here.
//
// The text helpers in src/rooms/text.ts have no runtime imports - the same
// reason chat/links.ts has none - so node strips their types and runs them.
//
// The markup builders next door import html.ts and cannot be loaded this way.
// What matters about them is not what they draw but that **everything on this
// page is chosen by somebody else**: a nickname by whoever walked into the
// room, a subject by whoever set it, a refusal by the service. html`...`
// escapes every interpolated value, and the only ways past it are raw() and
// innerHTML. So those two are checked in the source, which needs no DOM and is
// exactly the invariant the file header claims.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { occupantTitle, roomPreview } from '../src/rooms/text.ts';


const aRoom = (over = {}) => ({
    jid:            'chat@conference.example.org',
    displayName:    'chat',
    nick:           'me',
    state:          'joined',
    subject:        null,
    subjectBy:      null,
    nonAnonymous:   false,
    owner:          false,
    cannotEncrypt:  'this room is semi-anonymous',
    occupants:      [],
    unread:         0,
    lastMessage:    null,
    lastActivity:   null,
    refusal:        null,
    ...over
});

const anOccupant = (over = {}) => ({
    nick:         'alice',
    affiliation:  'none',
    role:         'participant',
    jid:          null,
    self:         false,
    ...over
});


describe('what an occupant row says about somebody', () => {

    it('says when the room will not name them', () => {

        // The absence is worth showing rather than leaving blank: it is exactly
        // what decides whether the room can be encrypted in.
        assert.match(occupantTitle(anOccupant()), /does not say who that is/u);

        assert.match(occupantTitle(anOccupant({ jid: 'alice@example.org' })),
                     /alice@example\.org/u);

    });

    it('names the affiliation and the role, which are two different things', () => {

        // An affiliation outlives the visit and a role lasts for it, and a
        // moderator of an ordinary room has neither of the other's powers.
        const title = occupantTitle(anOccupant({ affiliation: 'owner', role: 'moderator' }));

        assert.match(title, /owner/u);
        assert.match(title, /moderator/u);

    });

});


describe('what a room shows as its second line', () => {

    it('prefers the refusal, then the state, then the subject', () => {

        // A refused room stays in the list carrying its reason - a row that
        // simply never appears tells nobody why.
        assert.equal(roomPreview(aRoom({ state: 'refused', refusal: 'conflict' })), 'conflict');
        assert.equal(roomPreview(aRoom({ state: 'refused', refusal: null })),       'refused');
        assert.equal(roomPreview(aRoom({ state: 'joining' })),                      'entering …');
        assert.equal(roomPreview(aRoom({ subject: 'Deployment on Friday' })),       'Deployment on Friday');

    });

    it('falls back to the head count, and counts in words', () => {

        assert.equal(roomPreview(aRoom({ occupants: [anOccupant()] })), '1 person');
        assert.equal(roomPreview(aRoom({ occupants: [anOccupant(), anOccupant()] })), '2 people');
        assert.equal(roomPreview(aRoom()), '0 people');

    });

    it('returns text and never markup', () => {

        // Whatever comes back is interpolated into html`...`, which escapes it.
        // A helper that started returning markup would be trusted instead.
        const hostile = roomPreview(aRoom({ state: 'refused', refusal: '<img src=x onerror=alert(1)>' }));

        assert.equal(hostile, '<img src=x onerror=alert(1)>',
                     'The preview built markup of its own, which would then not be escaped.');

    });

});


describe('nothing on this page trusts what came over the wire', () => {

    // A nickname is chosen by whoever walked in, so the one rule that has to
    // hold everywhere in this view is that no string from the server becomes
    // markup. html`...` escapes; raw() and innerHTML are the two ways past it.
    const files = ['src/rooms/parts.ts', 'src/rooms/text.ts', 'src/pages/rooms.ts'];

    for (const file of files) {

        it(`${file} reaches for neither raw() nor innerHTML`, () => {

            // Comments out first, or the sentence explaining the rule trips the
            // check on the rule. Crude - a string containing "/*" would confuse
            // it - and enough for what this is: these three files are read by
            // whoever changes them.
            const source = readFileSync(new URL(`../${file}`, import.meta.url), 'utf8')
                               .replace(/\/\*[\s\S]*?\*\//gu, '')
                               .replace(/\/\/.*$/gmu, '');

            assert.ok(!/\braw\s*\(/u.test(source),
                      `${file} uses raw(), which hands a string to the browser as markup.`);

            assert.ok(!/\binnerHTML\b/u.test(source),
                      `${file} touches innerHTML. Only html.ts may, and only through render().`);

        });

    }

});
