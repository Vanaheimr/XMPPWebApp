import { html, type HTMLFragment } from '../html';
import type { Occupant, Room } from '../api/client';
import { occupantTitle } from './text';

// The pieces of the room view that are only a value turned into markup.
//
// Apart from pages/rooms.ts because they can then be tested without a browser
// and without the store: html`...` builds a string, so what these produce can
// be read straight off. **And the thing worth reading off is the escaping** —
// a nickname is chosen by whoever walked into the room, and a room subject by
// whoever set it. Both end up in this markup.


/** The lock in the room header, or the button that makes one possible. */
export function lock(room: Room | undefined): HTMLFragment {

    if (room === undefined || room.state !== 'joined')
        return html``;

    if (room.cannotEncrypt === null)
        return html`
            <span class="btn small encryption auto" title="Everything said here is encrypted to the people in the room">
                <i class="fa-solid fa-lock"></i>
            </span>`;

    // Offered only to an owner, because only an owner can configure a room.
    // Shown greyed out to everybody else rather than hidden: the reason it is
    // not encrypted is in the banner either way, and a missing control is
    // harder to ask about than a disabled one.
    return html`
        <button type="button"
                class="btn small encryption off"
                data-encrypt="${room.jid}"
                ${room.owner ? '' : 'disabled'}
                title="${room.owner
                            ? 'Make this room show real addresses, which is what encryption in it needs. It changes the room for everybody.'
                            : 'Not encrypted here, and only an owner of the room can change that.'}">
            <i class="fa-solid fa-lock-open"></i>
        </button>`;

}

/** The mark in front of a room in the list: what state it is in, or its lock. */
export function roomIcon(room: Room): HTMLFragment {

    return room.state === 'refused'    ? html`<i class="fa-solid fa-ban"></i>`
         : room.state === 'joining'    ? html`<i class="fa-solid fa-hourglass-half"></i>`
         : room.cannotEncrypt === null ? html`<i class="fa-solid fa-lock"></i>`
         :                               html`<i class="fa-solid fa-hashtag"></i>`;

}

/** What somebody is in the room, as one icon. */
export function roleMark(who: Occupant): HTMLFragment {

    return who.affiliation === 'owner' ? html`<i class="fa-solid fa-crown" title="Owner"></i>`
         : who.role === 'moderator'    ? html`<i class="fa-solid fa-shield" title="Moderator"></i>`
         : who.role === 'visitor'      ? html`<i class="fa-solid fa-ear-listen" title="May not speak here"></i>`
         :                               html`<i class="fa-solid fa-user"></i>`;

}

/** One row of the occupant list. */
export function occupantRow(who: Occupant): HTMLFragment {

    return html`
        <li class="occupant ${who.self ? 'self' : ''}" title="${occupantTitle(who)}">
            <span class="occupant-role ${who.role}">${roleMark(who)}</span>
            <span class="occupant-nick">${who.nick}</span>
            ${who.jid !== null
                ? html`<span class="occupant-jid">${who.jid}</span>`
                : html`<span class="occupant-jid muted" title="This room does not say who that is">—</span>`}
        </li>`;

}
