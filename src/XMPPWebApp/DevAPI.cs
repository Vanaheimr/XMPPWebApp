using System.Collections.Concurrent;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// Development helpers, registered at "/dev/" only when the server runs
    /// with --dev: watches the webpack output directory and publishes a
    /// "reload" Server-Sent Event whenever it changes, so that the page in
    /// the browser reloads itself after "npm run watch" wrote a new bundle.
    /// </summary>
    public sealed class DevAPI : HTTPAPI,
                                 IDisposable
    {

        #region Data

        /// <summary>
        /// The default root path of this API (trailing slash, see XMPPWebAPI).
        /// </summary>
        public static readonly HTTPPath  DefaultRootPath  = HTTPPath.Parse("/dev/");

        /// <summary>
        /// The name of the Server-Sent Event.
        /// </summary>
        public const String              EventName        = "reload";

        private readonly HTTPEventSource<JObject>              reloadEvents;
        private readonly FileSystemWatcher                     watcher;
        private readonly Timer                                 debounce;
        private readonly TimeSpan                              debounceDelay;
        private readonly ConcurrentDictionary<String, Byte>    changedFiles  = new(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region Properties

        /// <summary>
        /// The watched directory.
        /// </summary>
        public String  WatchedDirectory    { get; }

        /// <summary>
        /// The number of reload events published so far.
        /// </summary>
        public Int32   ReloadsPublished    { get; private set; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the development API within the given HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="WatchedDirectory">The webpack output directory to watch.</param>
        /// <param name="DebounceDelay">How long to wait after the last change before publishing, 400 ms by default.</param>
        public DevAPI(HTTPServer  HTTPServer,
                      String      WatchedDirectory,
                      TimeSpan?   DebounceDelay   = null)

            : base(HTTPServer,
                   RootPath:     DefaultRootPath,
                   Description:  I18NString.Create("XMPPWebApp development helpers"))

        {

            this.WatchedDirectory  = Path.GetFullPath(WatchedDirectory);
            this.debounceDelay     = DebounceDelay ?? TimeSpan.FromMilliseconds(400);

            // Hermod replays cached events to a freshly connected client; the
            // browser therefore ignores events older than its own page load.
            reloadEvents = this.AddJSONEventSource(
                               HTTPEventSource_Id.Parse(EventName),
                               MaxNumberOfCachedEvents:  10,
                               RetryInterval:            TimeSpan.FromSeconds(1),
                               EnableLogging:            false
                           );

            this.MapJSONEventSource(
                reloadEvents,
                HTTPPath.Parse("/reload"),
                RequireAuthentication:  false
            );

            debounce = new Timer(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);

            watcher  = new FileSystemWatcher(this.WatchedDirectory) {
                           IncludeSubdirectories  = true,
                           NotifyFilter           = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                       };

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Error   += (_, e) => Console.Error.WriteLine($"File watcher error: {e.GetException().Message}");

            watcher.EnableRaisingEvents = true;

        }

        #endregion


        #region (private) OnChanged(Sender, EventArgs)

        private void OnChanged(Object Sender, FileSystemEventArgs EventArgs)
        {

            changedFiles[EventArgs.Name ?? EventArgs.FullPath] = 0;

            // webpack writes many files in quick succession: wait for the last one.
            debounce.Change(debounceDelay, Timeout.InfiniteTimeSpan);

        }

        #endregion

        #region (private) Publish()

        private void Publish()
        {

            var files = changedFiles.Keys.OrderBy(file => file, StringComparer.Ordinal).ToArray();
            changedFiles.Clear();

            ReloadsPublished++;

            _ = reloadEvents.SubmitEvent(
                    EventName,
                    new JObject(
                        new JProperty("timestamp",  DateTimeOffset.UtcNow.ToString("o")),
                        new JProperty("files",      new JArray(files))
                    )
                );

        }

        #endregion


        #region Dispose()

        public void Dispose()
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            debounce.Dispose();
        }

        #endregion

    }

}
