/**
 * One thing at a time: a job that is asked for while the same job is still
 * running is not started a second time.
 *
 * What it is for is the send. A message is sent by a request that can take
 * seconds - the far end's device list is asked for, a server is slow - and for
 * exactly those seconds nothing on the page has changed yet: the text is still
 * in the box, and Enter is still Enter. The send button was disabled for the
 * length of it, and the keyboard was not, so pressing Enter again while it
 * hung sent the same line again - once per press, all of them delivered.
 *
 * Refusing is right rather than queueing: the second press does not mean
 * "and once more", it means "did that work?", and the answer to that is the
 * first one arriving.
 */
export class InFlight {

    private running = false;

    /** Whether a job is under way right now. */
    get busy(): boolean {
        return this.running;
    }

    /**
     * Runs the job, unless one is already running - then nothing happens and
     * the answer is false.
     *
     * A job that throws is over like one that returns, and what it threw is
     * passed on: the caller reports it the way it always did.
     */
    async run(job: () => Promise<void>): Promise<boolean> {

        if (this.running)
            return false;

        this.running = true;

        try {
            await job();
        }
        finally {
            this.running = false;
        }

        return true;

    }

}
