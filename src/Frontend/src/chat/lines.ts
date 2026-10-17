// What a conversation and a room have in common about one line: the buttons
// that act on it, and how the text it answers is shown above it. One copy, so
// that the two views cannot come to disagree about either.


/**
 * What can be done to a line: answer it (XEP-0461), and - on the one line an
 * edit would replace - say it again properly (XEP-0308).
 *
 * The pencil is put on every line that could ever be the one, and shown on
 * one of them only, by the `editable` class the view moves along as lines
 * arrive; deciding it here would decide it once, for a line that is not the
 * last for long.
 */
export function lineActions(options: { reply: boolean; edit: boolean }): HTMLElement {

    const actions = document.createElement('div');
    actions.className = 'actions';

    if (options.reply)
        actions.appendChild(actionButton('reply', 'fa-reply', 'Reply'));

    if (options.edit)
        actions.appendChild(actionButton('edit', 'fa-pen', 'Edit (↑ in an empty box does the same)'));

    return actions;

}

function actionButton(action: string, icon: string, title: string): HTMLButtonElement {

    const button = document.createElement('button');
    button.type            = 'button';
    button.className       = action;
    button.dataset.action  = action;
    button.title           = title;
    button.setAttribute('aria-label', title);

    const glyph = document.createElement('i');
    glyph.className = `fa-solid ${icon}`;
    glyph.setAttribute('aria-hidden', 'true');

    button.appendChild(glyph);

    return button;

}

/**
 * XEP-0461: the quoted lines an answer came with, as they are shown above it.
 *
 * The "> " is how the quotation travels for clients without the extension. A
 * blockquote says the same thing, so saying it twice is just noise.
 */
export function quotation(quote: string): string {

    return quote.replace(/\s+$/, '')
                .split('\n')
                .map(line => line.replace(/^>\s?/, ''))
                .join('\n');

}

/**
 * XEP-0461: brings the line an answer is about into view, and marks it for a
 * moment so the eye finds it among its neighbours.
 */
export function showLine(element: HTMLElement): void {

    element.scrollIntoView({ block: 'center', behavior: 'smooth' });

    // Taken off and put back, so that a second click marks it again.
    element.classList.remove('flash');
    void element.offsetWidth;
    element.classList.add('flash');

}
