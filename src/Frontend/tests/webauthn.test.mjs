// The translation between WebAuthn's ArrayBuffers and the JSON the API carries.
//
// Node strips the types of the imported .ts file, so this runs the real module.
// It needs no DOM and no network - which is why the encoding lives in a file of
// its own - but it does need atob and btoa, and Node has had both for a while.
//
// Why this is worth testing at all: a base64url decoder that gets the padding
// wrong does not throw. It hands back the wrong bytes, the ceremony then fails
// on the signature, and the message points at the wrong half of the system.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { fromBase64Url, toBase64Url, withBuffers } from '../src/webauthn.ts';

const bytes = array => new Uint8Array(array).buffer;

describe('base64url', () => {

    it('round-trips every byte value', () => {

        const all = new Uint8Array(256);
        for (let i = 0; i < 256; i++)
            all[i] = i;

        assert.deepEqual(new Uint8Array(fromBase64Url(toBase64Url(all.buffer))), all);

    });

    it('round-trips every length, which is where padding goes wrong', () => {

        // 0, 1, 2 and 3 bytes are the four padding cases, and everything longer
        // is one of them again with a whole group in front.
        for (let length = 0; length <= 16; length++)
        {
            const value = new Uint8Array(length).map((_, i) => (i * 37 + 11) & 0xff);
            assert.deepEqual(new Uint8Array(fromBase64Url(toBase64Url(value.buffer))), value,
                             `length ${length}`);
        }

    });

    it('writes the url alphabet and no padding', () => {

        // 0xfb 0xff produces '+/' in standard base64 and needs padding.
        const encoded = toBase64Url(bytes([0xfb, 0xff, 0xfe]));

        assert.ok(!encoded.includes('+'), 'no plus');
        assert.ok(!encoded.includes('/'), 'no slash');
        assert.ok(!encoded.includes('='), 'no padding');
        assert.deepEqual(new Uint8Array(fromBase64Url(encoded)), new Uint8Array([0xfb, 0xff, 0xfe]));

    });

    it('reads padded input and the other alphabet too', () => {

        // What a server that did not read the spec as closely might send.
        assert.deepEqual(new Uint8Array(fromBase64Url('-_8=')),  new Uint8Array([0xfb, 0xff]));
        assert.deepEqual(new Uint8Array(fromBase64Url('+/8=')),  new Uint8Array([0xfb, 0xff]));
        assert.deepEqual(new Uint8Array(fromBase64Url('+/8')),   new Uint8Array([0xfb, 0xff]));

    });

    it('is empty for empty', () => {
        assert.equal(toBase64Url(bytes([])), '');
        assert.equal(fromBase64Url('').byteLength, 0);
    });

});

describe('withBuffers', () => {

    const options = () => ({
        challenge:  toBase64Url(bytes([1, 2, 3])),
        rp:         { id: 'localhost', name: 'XMPPWebApp' },
        user:       { id: toBase64Url(bytes([4, 5])), name: 'admin', displayName: 'admin' },
        timeout:    60000,
        allowCredentials:    [ { type: 'public-key', id: toBase64Url(bytes([6])) } ],
        excludeCredentials:  [ { type: 'public-key', id: toBase64Url(bytes([7, 8])) } ]
    });

    it('turns the four byte fields into buffers', () => {

        const ready = withBuffers(options());

        assert.deepEqual(new Uint8Array(ready.challenge),                new Uint8Array([1, 2, 3]));
        assert.deepEqual(new Uint8Array(ready.user.id),                  new Uint8Array([4, 5]));
        assert.deepEqual(new Uint8Array(ready.allowCredentials[0].id),   new Uint8Array([6]));
        assert.deepEqual(new Uint8Array(ready.excludeCredentials[0].id), new Uint8Array([7, 8]));

    });

    it('leaves everything else exactly as it arrived', () => {

        const ready = withBuffers(options());

        assert.equal(ready.timeout,                       60000);
        assert.deepEqual(ready.rp,                        { id: 'localhost', name: 'XMPPWebApp' });
        assert.equal(ready.user.name,                     'admin');
        assert.equal(ready.allowCredentials[0].type,      'public-key');

    });

    it('does not write into what it was given', () => {

        // The options object comes from the parsed response, and a caller that
        // wanted to retry a ceremony with it would otherwise find the strings
        // already turned into buffers.
        const original = options();

        withBuffers(original);

        assert.equal(typeof original.challenge,             'string');
        assert.equal(typeof original.user.id,               'string');
        assert.equal(typeof original.allowCredentials[0].id, 'string');

    });

    it('copes with the fields that may be missing', () => {

        // A sign-in without a login has no allowCredentials and no user.
        const ready = withBuffers({ challenge: toBase64Url(bytes([9])), timeout: 60000 });

        assert.deepEqual(new Uint8Array(ready.challenge), new Uint8Array([9]));
        assert.equal(ready.user,             undefined);
        assert.equal(ready.allowCredentials, undefined);

    });

});
