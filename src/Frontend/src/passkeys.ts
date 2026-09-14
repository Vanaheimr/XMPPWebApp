// Passkeys (WebAuthn), the browser half.
//
// The server speaks JSON and the browser speaks ArrayBuffer, so everything that
// is bytes travels as base64url and is translated here and nowhere else. The
// ceremonies themselves belong to Hermod's HTTPExtAPI: this asks it for the
// options, hands them to the authenticator, and sends back what came out.
//
// Nothing here decides whether a passkey may be offered. That is the server's
// answer - it registers the routes only when it can name an origin a browser
// will accept - plus the browser's own two conditions below.

import { api, type Confirmation, type Me } from './api/client';

// ---------------------------------------------------------------------------
// base64url

function toBase64Url(bytes: ArrayBuffer): string {

    const binary = String.fromCharCode(...new Uint8Array(bytes));

    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

}

function fromBase64Url(text: string): ArrayBuffer {

    const padded  = text.replace(/-/g, '+').replace(/_/g, '/');
    const binary  = atob(padded + '='.repeat((4 - padded.length % 4) % 4));
    const bytes   = new Uint8Array(binary.length);

    for (let i = 0; i < binary.length; i++)
        bytes[i] = binary.charCodeAt(i);

    return bytes.buffer;

}

// ---------------------------------------------------------------------------
// Whether to offer one at all

/**
 * Whether this browser can do passkeys here at all.
 *
 * isSecureContext is the one that catches the interesting case: an http page on
 * anything but localhost has no navigator.credentials, so the button would be a
 * button that cannot work. The server has its own opinion about the origin and
 * answers 404 for the routes when it has none; this only keeps the page from
 * offering something the browser would refuse before the request.
 */
export function passkeysPossible(): boolean {
    return window.isSecureContext === true &&
           typeof window.PublicKeyCredential === 'function';
}

// ---------------------------------------------------------------------------
// The two ceremonies

/** What the server sends: the options, with every byte string as base64url. */
interface Ceremony {
    ceremonyId:  string;
    publicKey:   Record<string, unknown>;
}

function withBuffers(publicKey: Record<string, unknown>): PublicKeyCredentialCreationOptions & PublicKeyCredentialRequestOptions {

    const options = { ...publicKey } as Record<string, unknown>;

    if (typeof options.challenge === 'string')
        options.challenge = fromBase64Url(options.challenge);

    const user = options.user as { id?: unknown } | undefined;
    if (user && typeof user.id === 'string')
        user.id = fromBase64Url(user.id);

    for (const list of ['allowCredentials', 'excludeCredentials'])
    {
        const credentials = options[list] as { id?: unknown }[] | undefined;
        if (Array.isArray(credentials))
            for (const credential of credentials)
                if (typeof credential.id === 'string')
                    credential.id = fromBase64Url(credential.id);
    }

    // Through unknown, because the shape came off the wire and TypeScript is
    // right that it cannot know it is either of these until the browser says so.
    return options as unknown as PublicKeyCredentialCreationOptions & PublicKeyCredentialRequestOptions;

}

/**
 * Register a passkey for the account that is signed in. The name is what the
 * list on the settings page shows, so that two keys can be told apart.
 */
export async function register(name: string): Promise<void> {

    const ceremony = await api.passkeys.registerOptions();

    const created = await navigator.credentials.create({
                              publicKey: withBuffers(ceremony.publicKey)
                          }) as PublicKeyCredential | null;

    if (created === null)
        throw new Error('The authenticator did not answer.');

    const response = created.response as AuthenticatorAttestationResponse;

    await api.passkeys.register({
              name,
              ceremonyId:  ceremony.ceremonyId,
              credential:  {
                  id:        created.id,
                  rawId:     toBase64Url(created.rawId),
                  type:      created.type,
                  response:  {
                      clientDataJSON:     toBase64Url(response.clientDataJSON),
                      attestationObject:  toBase64Url(response.attestationObject)
                  }
              }
          });

}

/**
 * Sign in with a passkey. The login is optional: without it the authenticator
 * offers whatever discoverable credential it holds for this site, which is the
 * shorter way in and the reason a passkey is worth having.
 */
export async function signIn(login?: string): Promise<Me> {

    const ceremony = await api.passkeys.loginOptions(login);

    const got = await navigator.credentials.get({
                          publicKey: withBuffers(ceremony.publicKey)
                      }) as PublicKeyCredential | null;

    if (got === null)
        throw new Error('The authenticator did not answer.');

    const response = got.response as AuthenticatorAssertionResponse;

    return await api.passkeys.login({
                     ceremonyId:  ceremony.ceremonyId,
                     credential:  {
                         id:        got.id,
                         rawId:     toBase64Url(got.rawId),
                         type:      got.type,
                         response:  {
                             clientDataJSON:     toBase64Url(response.clientDataJSON),
                             authenticatorData:  toBase64Url(response.authenticatorData),
                             signature:          toBase64Url(response.signature),
                             userHandle:         response.userHandle !== null
                                                     ? toBase64Url(response.userHandle)
                                                     : null
                         }
                     }
                 });

}

export type { Ceremony };

/**
 * The same ceremony as a sign-in, asked of somebody who is already signed in.
 * What it proves is not who they are - the session says that - but that they
 * are still there, which is what the two account routes want before they act.
 */
export async function confirm(): Promise<Confirmation> {

    const ceremony = await api.passkeys.loginOptions();

    const got = await navigator.credentials.get({
                          publicKey: withBuffers(ceremony.publicKey)
                      }) as PublicKeyCredential | null;

    if (got === null)
        throw new Error('The authenticator did not answer.');

    const response = got.response as AuthenticatorAssertionResponse;

    return {
        ceremonyId:  ceremony.ceremonyId,
        credential:  {
            id:        got.id,
            rawId:     toBase64Url(got.rawId),
            type:      got.type,
            response:  {
                clientDataJSON:     toBase64Url(response.clientDataJSON),
                authenticatorData:  toBase64Url(response.authenticatorData),
                signature:          toBase64Url(response.signature),
                userHandle:         response.userHandle !== null
                                        ? toBase64Url(response.userHandle)
                                        : null
            }
        }
    };

}
