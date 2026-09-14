// Runtime configuration, read from <meta> tags in index.html so that no
// inline script is needed (keeps the Content-Security-Policy strict). The
// {{placeholders}} in those tags are filled in by the C# server.

function meta(name: string): string | undefined {
    return document.querySelector<HTMLMetaElement>(`meta[name="${name}"]`)?.content;
}

export const config = {
    apiBase:          meta('api-base')         ?? '/api/v1',
    /** Where the accounts live: HTTPExtAPI's routes, which are not under /v1. */
    authBase:         meta('auth-base')        ?? '/api',
    frontendVersion:  meta('frontend-version') ?? '?',
    serverVersion:    meta('server-version')   ?? '?',
    /** True when the server runs with --dev and offers /dev/reload. */
    devMode:          meta('dev-mode') === 'true'
};
