import type { Occupant, Room } from '../api/client';

// The parts of the room view that are text and nothing else.
//
// **No runtime imports, on purpose.** That is what lets these run under node
// in tests/rooms.test.mjs without a browser and without a bundler - the same
// reason chat/links.ts has none. Everything that turns a value into *markup*
// is next door in parts.ts, where html escapes it.

/**
 * The hover text of an occupant.
 *
 * It says when the room will not name somebody rather than leaving the line
 * blank: that absence is exactly what decides whether the room can be
 * encrypted in, so it is worth being able to point at.
 */
export function occupantTitle(who: Occupant): string {

    return `${who.nick} — ${who.affiliation}, ${who.role}` +
           (who.jid !== null ? ` — ${who.jid}` : ' — this room does not say who that is');

}

/** What a room shows as its second line in the list. */
export function roomPreview(room: Room): string {

    return room.state === 'refused'  ? (room.refusal ?? 'refused')
         : room.state === 'joining'  ? 'entering …'
         : room.subject              ? room.subject
         : `${room.occupants.length} ${room.occupants.length === 1 ? 'person' : 'people'}`;

}
