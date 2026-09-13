import type { Message, Presence } from './api/client';
import { imageURL } from './chat/links';

export function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

/** Only same-site paths may be used as a "next" target after signing in. */
export function safeNext(value: string | null): string | null {
    return value !== null && value.startsWith('/') && !value.startsWith('//')
               ? value
               : null;
}

/** Read a form field as a trimmed string. */
export function field(form: HTMLFormElement, name: string, trim = true): string {
    const value = String(new FormData(form).get(name) ?? '');
    return trim ? value.trim() : value;
}


// Times and dates

export function formatTime(iso: string): string {

    const date = new Date(iso);

    return Number.isNaN(date.getTime())
               ? iso
               : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

}

export function formatDay(iso: string): string {

    const date = new Date(iso);

    if (Number.isNaN(date.getTime()))
        return iso;

    const today      = new Date();
    const yesterday  = new Date();
    yesterday.setDate(today.getDate() - 1);

    if (sameDay(date, today))
        return 'Today';

    if (sameDay(date, yesterday))
        return 'Yesterday';

    return date.toLocaleDateString([], { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });

}

/** The time for the chat list: the time of day today, otherwise the date. */
export function formatListTime(iso: string | null): string {

    if (iso === null)
        return '';

    const date = new Date(iso);

    if (Number.isNaN(date.getTime()))
        return '';

    const today = new Date();

    if (sameDay(date, today))
        return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

    if (date.getFullYear() === today.getFullYear())
        return date.toLocaleDateString([], { month: 'short', day: 'numeric' });

    return date.toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' });

}

export function sameDay(a: Date, b: Date): boolean {
    return a.getFullYear() === b.getFullYear() &&
           a.getMonth()    === b.getMonth()    &&
           a.getDate()     === b.getDate();
}


// Presence and previews

export function presenceLabel(presence: Presence): string {

    switch (presence) {
        case 'available':  return 'online';
        case 'chat':       return 'free to chat';
        case 'away':       return 'away';
        case 'xa':         return 'extended away';
        case 'dnd':        return 'do not disturb';
        default:           return 'offline';
    }

}

/** A file size as somebody would say it. */
export function formatBytes(bytes: number): string {

    if (!Number.isFinite(bytes) || bytes < 0)
        return '';

    if (bytes < 1024)
        return `${bytes} B`;

    const units = ['kB', 'MB', 'GB'];
    let   value = bytes / 1024;
    let   unit  = 0;

    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }

    return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;

}


/** The first line of the last message, for the list on the left. */
export function preview(message: Message | null): string {

    if (message === null)
        return '';

    const text  = message.media !== null        ? mediaPreview(message.media.contentType)
                : imageURL(message.body) !== null ? '\u{1F4F7} Picture'
                :                                   message.body.split(/\r?\n/, 1)[0].trim();

    const line  = text.length > 120 ? text.slice(0, 120) + '…' : text;

    return message.direction === 'out'
               ? `You: ${line}`
               : line;

}

function mediaPreview(contentType: string): string {

    if (contentType.startsWith('image/'))  return '\u{1F4F7} Picture';
    if (contentType.startsWith('video/'))  return '\u{1F3AC} Video';
    if (contentType.startsWith('audio/'))  return '\u{1F3B5} Audio';

    return '\u{1F4CE} File';

}
