/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of XMPPWebApp <https://www.github.com/Vanaheimr/XMPPWebApp>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// What <see cref="LogLineFormatter"/> is allowed to be told.
    /// </summary>
    /// <remarks>
    /// <see cref="ConsoleFormatterOptions"/> brings the timestamp and the
    /// scopes; the two below are what this formatter adds. Colours have to be
    /// askable because a test comparing lines cannot have the answer depend on
    /// whether it was run in a terminal.
    /// </remarks>
    public sealed class LogLineFormatterOptions : ConsoleFormatterOptions
    {

        /// <summary>
        /// Whether the level is written in colour. Default asks the console:
        /// colours for a terminal, none for a redirected stream and none where
        /// NO_COLOR is set.
        /// </summary>
        public LoggerColorBehavior  ColorBehavior    { get; set; } = LoggerColorBehavior.Default;

        /// <summary>
        /// The column the logger's name is given, so that the message after it
        /// begins in the same place on every line.
        /// </summary>
        /// <remarks>
        /// Thirty-three characters is "Ratatoskr.StreamManagementManager",
        /// the longest name anything this application logs through can be
        /// shortened to. A longer one is written whole and pushes its own line
        /// to the right - a name cut to fit names nothing.
        /// </remarks>
        public Int32                CategoryWidth    { get; set; } = 33;

    }


    /// <summary>
    /// One line per entry, in columns: the time of day, the level, the logger,
    /// the message.
    /// </summary>
    /// <remarks>
    /// <code>
    /// 01:45:08 info  Ratatoskr.XMPPConnection          WebSocket connected
    /// 01:45:08 warn  Ratatoskr.PubSubManager           caps of a stranger discarded
    /// 01:45:08 info  Ratatoskr.StreamManagementManager resumed, 3 stanzas replayed
    /// </code>
    ///
    /// Two things the stock console formatter does not let go of brought this
    /// one about. It writes "[0]" after every category, the event id, whether
    /// one was given or not - and nothing in this application, Hermod or
    /// Ratatoskr gives one, so those four characters say nothing in every line
    /// of every run. And it puts the message straight after the category, so
    /// where a line's message begins depends on the name of whoever wrote it,
    /// which is the one thing in the line a reader is not looking for.
    ///
    /// What that cost is the whole of this file: the level tags, their
    /// colours, the scopes and the exception are written out again by hand,
    /// because <see cref="ConsoleFormatter"/> hands over the line as a whole
    /// and nothing in it piecemeal. The six tags and their colours are the
    /// stock ones to the letter, so that a log of this program still reads
    /// like every other .NET log; only the four characters of the tag are
    /// coloured, and the escape codes are the ones the console provider
    /// already knows how to strip when nobody is watching through a terminal.
    ///
    /// The exception, if there is one, goes on its own lines below, indented
    /// by two. The stock formatter folds it into the line, and a stack trace
    /// of forty frames in one line is not something anybody reads.
    ///
    /// Hermod has a one-liner of its own, OneLineLoggerProvider, and this is
    /// not it: that one is a provider, with a file beside the console, a
    /// thread id, and a rule of its own for naming DNS. This is the console of
    /// this program, so it stays with this program - and it stays a formatter,
    /// which is to say the provider keeps the queue, the colour handling and
    /// the stderr it already has.
    /// </remarks>
    public sealed class LogLineFormatter : ConsoleFormatter, IDisposable
    {

        #region Data

        /// <summary>
        /// The name this formatter is asked for by.
        /// </summary>
        public const String FormatterName = "logline";


        private const String Gray                = "\u001B[37m";
        private const String DarkGreen           = "\u001B[32m";
        private const String Yellow              = "\u001B[1m\u001B[33m";
        private const String Black               = "\u001B[30m";
        private const String White               = "\u001B[1m\u001B[37m";
        private const String DarkRedBackground   = "\u001B[41m";
        private const String NoForeground        = "\u001B[39m\u001B[22m";
        private const String NoBackground        = "\u001B[49m";

        /// <summary>
        /// What <see cref="LoggerColorBehavior.Default"/> answers: a terminal
        /// gets colours, a redirected stream does not, and NO_COLOR is a
        /// no wherever it is set.
        /// </summary>
        private static readonly Boolean coloursUnlessTold =
                                    Environment.GetEnvironmentVariable("NO_COLOR") is null &&
                                    !Console.IsOutputRedirected;

        private readonly IDisposable?              optionsReloadToken;
        private          LogLineFormatterOptions   options;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create a new formatter for the log lines of this program.
        /// </summary>
        /// <param name="Options">Its options, as the console provider hands them over.</param>
        public LogLineFormatter(IOptionsMonitor<LogLineFormatterOptions> Options)

            : base(FormatterName)

        {

            options             = Options.CurrentValue;
            optionsReloadToken  = Options.OnChange(changed => options = changed);

        }

        #endregion


        #region Write(LogEntry, ScopeProvider, TextWriter)

        /// <summary>
        /// Write one entry as one line.
        /// </summary>
        /// <param name="logEntry">The entry.</param>
        /// <param name="scopeProvider">The scopes it was written inside, if they are wanted.</param>
        /// <param name="textWriter">Where the line goes.</param>
        public override void Write<TState>(in LogEntry<TState>      logEntry,
                                           IExternalScopeProvider?  scopeProvider,
                                           TextWriter               textWriter)
        {

            var message = logEntry.Formatter(logEntry.State, logEntry.Exception);

            // Nothing to say and nothing to show: the entry was a scope or a
            // state somebody else makes sense of.
            if (String.IsNullOrEmpty(message) && logEntry.Exception is null)
                return;

            var colours = options.ColorBehavior switch {
                              LoggerColorBehavior.Disabled  => false,
                              LoggerColorBehavior.Enabled   => true,
                              _                             => coloursUnlessTold
                          };

            #region The time of day

            if (options.TimestampFormat is String timestampFormat)
            {
                textWriter.Write((options.UseUtcTimestamp
                                      ? DateTimeOffset.UtcNow
                                      : DateTimeOffset.Now).ToString(timestampFormat));
                textWriter.Write(' ');
            }

            #endregion

            #region The level, four characters wide and the only thing in colour

            var (tag, foreground, background) = Level(logEntry.LogLevel);

            if (colours && background is not null)
                textWriter.Write(background);

            if (colours && foreground is not null)
                textWriter.Write(foreground);

            textWriter.Write(tag);

            if (colours && foreground is not null)
                textWriter.Write(NoForeground);

            if (colours && background is not null)
                textWriter.Write(NoBackground);

            textWriter.Write("  ");

            #endregion

            #region The logger, in its column

            var category = LogCategories.Shorten(logEntry.Category);

            textWriter.Write(category.Length < options.CategoryWidth
                                 ? category.PadRight(options.CategoryWidth)
                                 : category);

            textWriter.Write(' ');

            #endregion

            #region An event id, where there is one

            // Id 0 and no name is what every Log* call without an event id
            // leaves behind, and writing "[0]" for it is what this formatter
            // exists to stop.
            if (logEntry.EventId.Id != 0 || logEntry.EventId.Name is not null)
            {
                textWriter.Write('[');
                textWriter.Write(logEntry.EventId.ToString());
                textWriter.Write("] ");
            }

            #endregion

            #region The scopes, where they are wanted

            if (options.IncludeScopes && scopeProvider is not null)
                scopeProvider.ForEachScope(
                    (scope, writer) => {
                        writer.Write("=> ");
                        writer.Write(scope);
                        writer.Write(' ');
                    },
                    textWriter
                );

            #endregion

            #region The message, on one line whatever it brought

            textWriter.Write(message?.ReplaceLineEndings(" ").TrimEnd());

            #endregion

            #region The exception, on its own lines below

            if (logEntry.Exception is not null)
            {
                textWriter.Write(Environment.NewLine);
                textWriter.Write("  ");
                textWriter.Write(logEntry.Exception.ToString().ReplaceLineEndings(Environment.NewLine + "  "));
            }

            #endregion

            textWriter.Write(Environment.NewLine);

        }

        #endregion

        #region (private static) Level(LogLevel)

        /// <summary>
        /// The four characters a level is written as, and the colours they are
        /// written in - the stock console formatter's, to the letter.
        /// </summary>
        /// <param name="LogLevel">The level of an entry.</param>
        private static (String Tag, String? Foreground, String? Background) Level(LogLevel LogLevel)

            => LogLevel switch {
                   LogLevel.Trace        => ("trce", Gray,       null),
                   LogLevel.Debug        => ("dbug", Gray,       null),
                   LogLevel.Information  => ("info", DarkGreen,  null),
                   LogLevel.Warning      => ("warn", Yellow,     null),
                   LogLevel.Error        => ("fail", Black,      DarkRedBackground),
                   LogLevel.Critical     => ("crit", White,      DarkRedBackground),
                   _                     => ("none", null,       null)
               };

        #endregion

        #region Dispose()

        /// <summary>
        /// Stop listening for changed options.
        /// </summary>
        public void Dispose()

            => optionsReloadToken?.Dispose();

        #endregion

    }

}
