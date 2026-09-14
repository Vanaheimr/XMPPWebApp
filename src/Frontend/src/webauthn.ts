// The translation between what WebAuthn speaks and what JSON can carry.
//
// A ceremony deals in ArrayBuffers; the API deals in JSON. Everything that is
// bytes therefore travels as base64url, and this is the only place that knows
// it. Kept apart from passkeys.ts on purpose: nothing here touches the network,
// the DOM or the browser's credential store, so it can be tested by itself -
// which matters more than it looks. A padding mistake in a base64url decoder
// does not announce itself. It produces a credential that is quietly the wrong
// bytes, and the ceremony then fails with a message about signatures.

/** Bytes as the API carries them: base64url, unpadded. */
export function toBase64Url(bytes: ArrayBuffer): string {

    const binary = String.fromCharCode(...new Uint8Array(bytes));

    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

}

/** And back. Accepts padded and unpadded input, and the '+/' alphabet too. */
export function fromBase64Url(text: string): ArrayBuffer {

    const padded  = text.replace(/-/g, '+').replace(/_/g, '/');
    const binary  = atob(padded + '='.repeat((4 - padded.length % 4) % 4));
    const bytes   = new Uint8Array(binary.length);

    for (let i = 0; i < binary.length; i++)
        bytes[i] = binary.charCodeAt(i);

    return bytes.buffer;

}

/**
 * The options as the server sent them, with every byte string turned back into
 * the ArrayBuffer navigator.credentials expects.
 *
 * The four places that carry bytes are the challenge, the user id, and the ids
 * inside allowCredentials and excludeCredentials. Everything else is left
 * exactly as it arrived - this does not validate the options, it only undoes
 * the encoding JSON forced on them.
 */
export function withBuffers(publicKey: Record<string, unknown>): PublicKeyCredentialCreationOptions & PublicKeyCredentialRequestOptions {

    const options = { ...publicKey } as Record<string, unknown>;

    if (typeof options.challenge === 'string')
        options.challenge = fromBase64Url(options.challenge);

    const user = options.user as { id?: unknown } | undefined;
    if (user && typeof user.id === 'string')
        options.user = { ...user, id: fromBase64Url(user.id) };

    for (const list of ['allowCredentials', 'excludeCredentials'])
    {
        const credentials = options[list] as { id?: unknown }[] | undefined;

        if (Array.isArray(credentials))
            options[list] = credentials.map(credential =>
                                typeof credential.id === 'string'
                                    ? { ...credential, id: fromBase64Url(credential.id) }
                                    : credential);
    }

    // Through unknown, because the shape came off the wire and TypeScript is
    // right that it cannot know it is either of these until the browser says so.
    return options as unknown as PublicKeyCredentialCreationOptions & PublicKeyCredentialRequestOptions;

}
