// Live reload for "npm run watch" + "dotnet run -- --dev": the server watches
// the webpack output directory and publishes a "reload" Server-Sent Event
// after every change; the page then reloads itself. A reconnect after the
// server restarted reloads as well, so a backend rebuild is picked up too.

export function startLiveReload(url = '/dev/reload'): void {

    // When this page's navigation started. Server and browser share the clock
    // in development, so timestamps are comparable.
    const navigationStart  = performance.timeOrigin;
    let   disconnected     = false;

    const source = new EventSource(url);

    source.addEventListener('reload', event => {

        // Hermod replays its cached events to every new client: an event that
        // was published before this page was requested is history, not a
        // request to reload (that would loop).
        try
        {
            const data = JSON.parse((event as MessageEvent<string>).data) as { timestamp?: string };

            if (data.timestamp && Date.parse(data.timestamp) <= navigationStart)
                return;
        }
        catch
        {
            // Unparseable payload: reload anyway.
        }

        console.info('[dev] the frontend bundle changed, reloading …');
        location.reload();

    });

    source.addEventListener('error', () => {
        disconnected = true;
    });

    source.addEventListener('open', () => {
        if (disconnected) {
            console.info('[dev] the server is back, reloading …');
            location.reload();
        }
    });

}
