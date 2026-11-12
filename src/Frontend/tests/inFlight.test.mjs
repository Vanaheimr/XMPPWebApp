// One send at a time - src/chat/inFlight.ts, checked without a browser.
//
//   npm test

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { InFlight } from '../src/chat/inFlight.ts';


/** A job that ends when the test says so. */
function held() {
    let release, fail;
    const done = new Promise((resolve, reject) => { release = resolve; fail = reject; });
    return { job: () => done, release, fail };
}

describe('InFlight', () => {

    it('does not start the same job twice while the first is still running', async () => {

        const flight = new InFlight();
        const first  = held();
        let   started = 0;

        const one = flight.run(() => { started++; return first.job(); });
        const two = flight.run(() => { started++; return Promise.resolve(); });
        const three = flight.run(() => { started++; return Promise.resolve(); });

        assert.equal(await two,   false, 'the second press was taken for a second message');
        assert.equal(await three, false);
        assert.equal(flight.busy, true);

        first.release();

        assert.equal(await one, true);
        assert.equal(started, 1, `one message typed, ${started} sent`);

    });

    it('is free again once the job is over', async () => {

        const flight = new InFlight();
        let   started = 0;

        await flight.run(async () => { started++; });
        await flight.run(async () => { started++; });

        assert.equal(started, 2, 'the next message after a finished one was swallowed');
        assert.equal(flight.busy, false);

    });

    it('is free again after a job that failed, and passes the failure on', async () => {

        const flight = new InFlight();

        await assert.rejects(flight.run(async () => { throw new Error('refused'); }), /refused/);

        assert.equal(flight.busy, false, 'one failed send, and nothing can be sent any more');
        assert.equal(await flight.run(async () => {}), true);

    });

});
