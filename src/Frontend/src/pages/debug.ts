import { api, type Stanza } from '../api/client';
import { store, type StoreEvent } from '../chat/store';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { errorMessage } from '../ui';

// The XML debug view: what actually went over the wire, which is the only
// thing that answers "why did that fail". The console has had it all along
// behind --verbose; this is the same trace, where somebody running the web app
// can read it.
//
// Off until switched on. Not out of thrift - keeping five hundred stanzas
// costs nothing - but because a trace is every word of every conversation, the
// whole roster and who is online, and that belongs on a page only while
// somebody is looking for something. The server empties the SASL exchange out
// before it keeps anything, so no credential reaches this page even when the
// connection negotiated PLAIN.

/** The filter box matches on this, so the needle is lower-cased once. */
function matches(stanza: Stanza, needle: string): boolean {
    return needle === '' || stanza.xml.toLowerCase().includes(needle);
}

/**
 * A stanza's element name, for the label on the left.
 *
 * Read off the text rather than parsed: this runs for every line on every
 * render, the frames are well-formed XML from a library that just serialised
 * them, and what it is used for is a four-letter label. A frame that defeats
 * it says "xml" and loses nothing.
 */
function nameOf(xml: string): string {
    return /^\s*<([a-zA-Z0-9_:-]+)/.exec(xml)?.[1]?.split(':').pop() ?? 'xml';
}

function time(timestamp: string): string {
    const at = new Date(timestamp);
    return at.toLocaleTimeString() + '.' + String(at.getMilliseconds()).padStart(3, '0');
}

export const debugPage: Page = {

    title: 'XML debug',

    async render({ root }) {

        render(root, html`
            <section class="debug">

                <header class="debug-head">
                    <h1><i class="fa-solid fa-code"></i> XML debug</h1>
                    <div class="debug-actions">
                        <input id="filter" type="search" placeholder="Filter, e.g. iq or omemo" aria-label="Filter the stanzas" />
                        <button type="button" class="btn small" id="clear" title="Throw away what is shown and keep recording">Clear</button>
                        <button type="button" class="btn small primary" id="toggle">…</button>
                    </div>
                </header>

                <p id="intro" class="muted small"></p>

                <div id="stanzas" class="stanzas" aria-live="off"></div>

            </section>
        `);

        const intro     = must<HTMLElement>(root, '#intro');
        const list      = must<HTMLElement>(root, '#stanzas');
        const toggle    = must<HTMLButtonElement>(root, '#toggle');
        const clear     = must<HTMLButtonElement>(root, '#clear');
        const filter    = must<HTMLInputElement>(root, '#filter');

        let recording  = false;
        let capacity   = 0;

        function draw(): void {

            const needle  = filter.value.trim().toLowerCase();
            const shown   = store.stanzas.filter(stanza => matches(stanza, needle));

            toggle.textContent  = recording ? 'Stop recording' : 'Start recording';
            toggle.className    = recording ? 'btn small danger' : 'btn small primary';
            clear.disabled      = !recording;

            intro.textContent = recording
                ? `Recording. The last ${capacity} stanzas are kept; the SASL exchange is emptied out before anything is stored. `
                  + `${shown.length} of ${store.stanzas.length} shown.`
                : 'Not recording. Switching it on shows every stanza this connection sends and receives — '
                  + 'every message body and the whole roster among them. Switching it off throws away what was kept.';

            if (shown.length === 0) {
                render(list, html`<p class="muted small">${recording ? 'Nothing yet.' : ''}</p>`);
                return;
            }

            // At the bottom before the redraw, stay at the bottom after it -
            // which is what somebody watching a live trace wants, and the
            // opposite of what somebody scrolled up reading one wants.
            const atBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 40;

            render(list, html`${shown.map(stanza => html`
                <div class="stanza ${stanza.direction}">
                    <div class="stanza-meta">
                        <span class="arrow">${stanza.direction === 'out' ? '▲' : '▼'}</span>
                        <span class="name">${nameOf(stanza.xml)}</span>
                        <span class="at">${time(stanza.timestamp)}</span>
                    </div>
                    <pre>${stanza.xml}</pre>
                </div>
            `)}`);

            if (atBottom)
                list.scrollTop = list.scrollHeight;

        }

        function apply(trace: { enabled: boolean; capacity: number; stanzas: Stanza[] }): void {
            recording      = trace.enabled;
            capacity       = trace.capacity;
            store.stanzas  = trace.stanzas;
            draw();
        }

        try {
            apply(await api.debug.stanzas());
        }
        catch (error) {
            intro.textContent = errorMessage(error);
        }

        toggle.addEventListener('click', () => void (async () => {
            try   { apply(await api.debug.record(!recording)); }
            catch (error) { intro.textContent = errorMessage(error); }
        })());

        clear.addEventListener('click', () => void (async () => {
            try   { apply(await api.debug.clear()); }
            catch (error) { intro.textContent = errorMessage(error); }
        })());

        filter.addEventListener('input', draw);

        return store.onChange((event: StoreEvent) => { if (event.type === 'stanza') draw(); });

    }

};
