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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

using NUnit.Framework;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// What a log line of this program looks like.
    /// </summary>
    /// <remarks>
    /// A formatter is the one thing in a log that can be compared against
    /// something, because it is handed the writer it writes into: every test
    /// here is the line itself, character for character, and not an
    /// arrangement that would have produced it.
    ///
    /// Colours and the time of day are therefore switched off in all but
    /// their own tests - a line is only comparable where nothing in it comes
    /// from the clock or from the terminal the run happened in.
    /// </remarks>
    [TestFixture]
    public class LogLineTests
    {

        #region TheColumnsOfALine()

        [Test]
        public void TheColumnsOfALine()
        {

            Assert.That(WhatIsWritten(),
                        Is.EqualTo("info  Ratatoskr.XMPPConnection          WebSocket connected" + Environment.NewLine));

        }

        #endregion

        #region TheTimeOfDay_GoesInFront()

        [Test]
        public void TheTimeOfDay_GoesInFront()
        {

            Assert.Multiple(() =>
            {

                Assert.That(WhatIsWritten(Setup: options => options.TimestampFormat = "HH:mm:ss"),
                            Does.Match(@"^\d\d:\d\d:\d\d info  Ratatoskr\.XMPPConnection"),
                            "the format brings no trailing space of its own");

                Assert.That(WhatIsWritten(),
                            Does.StartWith("info"),
                            "no format, no timestamp, and no space held open for one");

            });

        }

        #endregion

        #region AnEventId_IsWrittenOnlyWhereThereIsOne()

        [Test]
        public void AnEventId_IsWrittenOnlyWhereThereIsOne()
        {

            Assert.Multiple(() =>
            {

                // What the stock formatter writes as "[0]" in every line of
                // every run, and the whole reason this one exists.
                Assert.That(WhatIsWritten(),
                            Does.Not.Contain("["),
                            "id 0 and no name is what a Log* call without an event id leaves behind");

                Assert.That(WhatIsWritten(EventId: new EventId(42)),
                            Does.Contain("[42] WebSocket connected"));

                Assert.That(WhatIsWritten(EventId: new EventId(7, "Resumed")),
                            Does.Contain("[Resumed] WebSocket connected"),
                            "a named event id says more than its number");

            });

        }

        #endregion

        #region TheMessageColumn_DoesNotMove()

        [Test]
        public void TheMessageColumn_DoesNotMove()
        {

            var shortName  = WhatIsWritten(Category: "org.GraphDefined.Vanaheimr.Styx");
            var longName   = WhatIsWritten(Category: "org.GraphDefined.Vanaheimr.Ratatoskr.StreamManagementManager");

            Assert.Multiple(() =>
            {

                Assert.That(shortName.IndexOf("WebSocket connected", StringComparison.Ordinal),
                            Is.EqualTo(longName.IndexOf("WebSocket connected", StringComparison.Ordinal)),
                            "four characters of name or thirty-three, the message begins in the same place");

                Assert.That(longName,
                            Does.Contain("Ratatoskr.StreamManagementManager WebSocket"),
                            "the longest name there is leaves exactly one space");

            });

        }

        #endregion

        #region ALongerName_IsNotCutToFit()

        [Test]
        public void ALongerName_IsNotCutToFit()
        {

            // Hermod has such names - thirty-six characters here - and this
            // application never opens one. If it ever does, the line grows
            // rather than the name shrinking: a name cut to fit names nothing.
            var line = WhatIsWritten(Category: "org.GraphDefined.Vanaheimr.Hermod.DNS.InMemoryMulticastDNSTransport");

            Assert.That(line,
                        Is.EqualTo("info  Hermod.InMemoryMulticastDNSTransport WebSocket connected" + Environment.NewLine));

        }

        #endregion

        #region TheCategory_ComesShortened()

        [Test]
        public void TheCategory_ComesShortened()
        {

            Assert.Multiple(() =>
            {

                Assert.That(WhatIsWritten(Category: "org.GraphDefined.Vanaheimr.Hermod.HTTP.HTTPServer"),
                            Does.Contain("Hermod.HTTPServer"));

                Assert.That(WhatIsWritten(Category: "org.GraphDefined.Vanaheimr.Hermod.HTTP.HTTPServer"),
                            Does.Not.Contain("org.GraphDefined"),
                            "the organisation is the same in every line of every run");

                Assert.That(WhatIsWritten(Category: "Microsoft.Hosting.Lifetime"),
                            Does.Contain("Microsoft.Hosting.Lifetime"),
                            "somebody else's naming is left alone; see LogCategoryTests");

            });

        }

        #endregion

        #region TheColumn_FitsWhatThisApplicationLogsThrough()

        [Test]
        public void TheColumn_FitsWhatThisApplicationLogsThrough()
        {

            // Where the thirty-three come from. Not every category in the
            // three libraries - Hermod's multicast DNS transports are longer,
            // and nothing here opens one - but every one this application can
            // reach, so that no line of a real run is pushed out of the
            // column.
            var categories = new[] {
                                 "org.GraphDefined.Vanaheimr.Ratatoskr.StreamManagementManager",
                                 "org.GraphDefined.Vanaheimr.Ratatoskr.EntityCapsManager",
                                 "org.GraphDefined.Vanaheimr.Ratatoskr.ReceiptTracker",
                                 "org.GraphDefined.Vanaheimr.Ratatoskr.XMPPConnection",
                                 "org.GraphDefined.Vanaheimr.Ratatoskr.XMPPClient",
                                 "org.GraphDefined.Vanaheimr.Ratatoskr.Server.XMPPServer",
                                 "org.GraphDefined.Vanaheimr.XMPPWebApp.RotatingCertificate",
                                 "org.GraphDefined.Vanaheimr.XMPPWebApp.ChatArchive",
                                 "org.GraphDefined.Vanaheimr.XMPPWebApp.XMPPWebAPI",
                                 "org.GraphDefined.Vanaheimr.Hermod.WebSocket.AWebSocketEndpoint",
                                 "org.GraphDefined.Vanaheimr.Hermod.HTTP.AHTTPServer",
                                 "org.GraphDefined.Vanaheimr.Hermod.DNS.DNSHTTPSClient",
                                 "org.GraphDefined.Vanaheimr.Hermod.TCPConnection",
                                 "Microsoft.Hosting.Lifetime"
                             };

            var width = new LogLineFormatterOptions().CategoryWidth;

            Assert.Multiple(() =>
            {

                foreach (var category in categories)
                    Assert.That(LogCategories.Shorten(category).Length,
                                Is.LessThanOrEqualTo(width),
                                category);

                Assert.That(categories.Max(category => LogCategories.Shorten(category).Length),
                            Is.EqualTo(width),
                            "and not one column wider than the longest of them");

            });

        }

        #endregion

        #region AMessageOfSeveralLines_StaysOneLine()

        [Test]
        public void AMessageOfSeveralLines_StaysOneLine()
        {

            var line = WhatIsWritten(Message: "an answer arrived:\r\n<iq type='result'/>\n");

            Assert.Multiple(() =>
            {

                Assert.That(line,
                            Does.EndWith("an answer arrived: <iq type='result'/>" + Environment.NewLine));

                Assert.That(line.Split('\n'),
                            Has.Length.EqualTo(2),
                            "one line and the break that ends it, whatever the message brought");

            });

        }

        #endregion

        #region AnException_GoesBelowTheLine()

        [Test]
        public void AnException_GoesBelowTheLine()
        {

            var line = WhatIsWritten(Level:      LogLevel.Error,
                                     Message:    "the stream fell over",
                                     Exception:  new InvalidOperationException("no such session"));

            var lines = line.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.Multiple(() =>
            {

                Assert.That(lines[0],
                            Does.EndWith("the stream fell over"),
                            "the message keeps its line");

                // Folded into the line, as the stock formatter does it, forty
                // frames of a stack trace are not something anybody reads.
                Assert.That(lines[1],
                            Is.EqualTo("  System.InvalidOperationException: no such session"));

                Assert.That(lines, Has.Length.EqualTo(2));

            });

        }

        #endregion

        #region TheLevels_AreTheStockFourCharacterTags()

        [Test]
        public void TheLevels_AreTheStockFourCharacterTags()
        {

            Assert.Multiple(() =>
            {

                Assert.That(WhatIsWritten(Level: LogLevel.Trace),        Does.StartWith("trce  "));
                Assert.That(WhatIsWritten(Level: LogLevel.Debug),        Does.StartWith("dbug  "));
                Assert.That(WhatIsWritten(Level: LogLevel.Information),  Does.StartWith("info  "));
                Assert.That(WhatIsWritten(Level: LogLevel.Warning),      Does.StartWith("warn  "));
                Assert.That(WhatIsWritten(Level: LogLevel.Error),        Does.StartWith("fail  "));
                Assert.That(WhatIsWritten(Level: LogLevel.Critical),     Does.StartWith("crit  "));

            });

        }

        #endregion

        #region TheColour_IsAroundTheLevelAndNowhereElse()

        [Test]
        public void TheColour_IsAroundTheLevelAndNowhereElse()
        {

            // The line entire, and not a prefix of it: Is.EqualTo compares
            // strings ordinally, while Does.Contain and Does.StartWith go
            // through a culture-sensitive IndexOf, and ICU counts an escape
            // character as ignorable - which makes "\u001B" a substring of
            // every string there is, and "does not contain it" a test that
            // could never hold.
            Assert.Multiple(() =>
            {

                // Dark green on nothing, and back to the console's own
                // colours before the name of the logger - the codes the stock
                // console formatter writes for an information line, which the
                // provider knows how to strip where no terminal is watching.
                Assert.That(WhatIsWritten(Setup: options => options.ColorBehavior = LoggerColorBehavior.Enabled),
                            Is.EqualTo("\u001B[32minfo\u001B[39m\u001B[22m  Ratatoskr.XMPPConnection          WebSocket connected" + Environment.NewLine),
                            "around the level, and nowhere else in the line");

                Assert.That(WhatIsWritten(Level: LogLevel.Error,
                                          Setup: options => options.ColorBehavior = LoggerColorBehavior.Enabled),
                            Is.EqualTo("\u001B[41m\u001B[30mfail\u001B[39m\u001B[22m\u001B[49m  Ratatoskr.XMPPConnection          WebSocket connected" + Environment.NewLine),
                            "black on dark red, and the background put back as well");

                Assert.That(WhatIsWritten().Contains('\u001B'),
                            Is.False,
                            "switched off, a redirected log is a text file and nothing more");

            });

        }

        #endregion

        #region TheScopes_ComeBeforeTheMessage()

        [Test]
        public void TheScopes_ComeBeforeTheMessage()
        {

            var scopes = new LoggerExternalScopeProvider();

            using (scopes.Push("session 17"))
            {

                Assert.Multiple(() =>
                {

                    Assert.That(WhatIsWritten(Scopes:  scopes,
                                              Setup:   options => options.IncludeScopes = true),
                                Does.Contain("=> session 17 WebSocket connected"));

                    Assert.That(WhatIsWritten(Scopes: scopes),
                                Does.Not.Contain("session 17"),
                                "not asked for, not written - this program does not ask");

                });

            }

        }

        #endregion


        #region TheLogOfThisProgram_WritesItsLinesItself()

        [Test]
        public void TheLogOfThisProgram_WritesItsLinesItself()
        {

            var services = new ServiceCollection();

            services.AddLogging(builder => Program.ConsoleLines(builder, false));

            using var serviceProvider = services.BuildServiceProvider();

            // All there is to ask: the console provider is the stock one, and
            // whom it hands its lines to is a name in its options. Without
            // this, the formatter above could be perfect and unused - the
            // console would quietly go back to "[0]" and nothing would fail.
            Assert.Multiple(() =>
            {

                Assert.That(serviceProvider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName,
                            Is.EqualTo(LogLineFormatter.FormatterName));

                Assert.That(serviceProvider.GetServices<ConsoleFormatter>(),
                            Has.Some.InstanceOf<LogLineFormatter>(),
                            "and it is registered under that name");

                Assert.That(serviceProvider.GetRequiredService<IOptions<LogLineFormatterOptions>>().Value.TimestampFormat,
                            Is.EqualTo("HH:mm:ss"),
                            "with the time of day in front of every line");

            });

        }

        #endregion


        #region (private static) WhatIsWritten(...)

        /// <summary>
        /// One entry through the formatter, and the line it wrote.
        /// </summary>
        private static String WhatIsWritten(LogLevel                          Level      = LogLevel.Information,
                                            String                            Category   = "org.GraphDefined.Vanaheimr.Ratatoskr.XMPPConnection",
                                            String                            Message    = "WebSocket connected",
                                            EventId                           EventId    = default,
                                            Exception?                        Exception  = null,
                                            IExternalScopeProvider?           Scopes     = null,
                                            Action<LogLineFormatterOptions>?  Setup      = null)
        {

            var options = new LogLineFormatterOptions {
                              TimestampFormat  = null,
                              ColorBehavior    = LoggerColorBehavior.Disabled
                          };

            Setup?.Invoke(options);

            using var formatter  = new LogLineFormatter(new TheseOptions<LogLineFormatterOptions>(options));
            using var writer     = new StringWriter();

            formatter.Write(
                new LogEntry<String>(
                    Level,
                    Category,
                    EventId,
                    Message,
                    Exception,
                    (state, _) => state
                ),
                Scopes,
                writer
            );

            return writer.ToString();

        }

        #endregion

        #region (private class) TheseOptions<T>(Value)

        /// <summary>
        /// The options and no others, for as long as the formatter lives.
        /// </summary>
        private sealed class TheseOptions<T>(T Value) : IOptionsMonitor<T>
        {

            public T CurrentValue
                => Value;

            public T Get(String? Name)
                => Value;

            public IDisposable? OnChange(Action<T, String?> Listener)
                => null;

        }

        #endregion

    }

}
