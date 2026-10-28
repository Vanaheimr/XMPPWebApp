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

using NUnit.Framework;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// What a log line calls the logger it comes from.
    /// </summary>
    /// <remarks>
    /// The rule is one line of string handling, and the two things worth
    /// holding on to are the ones a rule like that gets wrong: that a name
    /// from outside this organisation is left alone, and that the names being
    /// shortened are the names that actually arrive. The last test asks the
    /// type itself rather than a string literal, so a renamed namespace shows
    /// up here instead of in a log nobody compares.
    /// </remarks>
    [TestFixture]
    public class LogCategoryTests
    {

        #region TheOrganisationGoes_TheProductStays()

        [Test]
        public void TheOrganisationGoes_TheProductStays()
        {

            Assert.Multiple(() =>
            {

                Assert.That(LogCategories.Shorten("org.GraphDefined.Vanaheimr.Ratatoskr.XMPPConnection"),
                            Is.EqualTo("Ratatoskr.XMPPConnection"));

                Assert.That(LogCategories.Shorten("org.GraphDefined.Vanaheimr.Ratatoskr.StreamManagementManager"),
                            Is.EqualTo("Ratatoskr.StreamManagementManager"));

                Assert.That(LogCategories.Shorten("org.GraphDefined.Vanaheimr.XMPPWebApp.XMPPWebAPI"),
                            Is.EqualTo("XMPPWebApp.XMPPWebAPI"));

                Assert.That(LogCategories.Shorten("org.GraphDefined.Vanaheimr.Hermod.HTTP.HTTPServer"),
                            Is.EqualTo("Hermod.HTTPServer"),
                            "what lies between the product and the class goes as well");

                Assert.That(LogCategories.Shorten("org.GraphDefined.Vanaheimr.Styx"),
                            Is.EqualTo("Styx"),
                            "a product without a class of its own");

            });

        }

        #endregion

        #region SomebodyElsesNaming_IsLeftAlone()

        [Test]
        public void SomebodyElsesNaming_IsLeftAlone()
        {

            Assert.Multiple(() =>
            {

                Assert.That(LogCategories.Shorten("Microsoft.Hosting.Lifetime"),
                            Is.EqualTo("Microsoft.Hosting.Lifetime"),
                            "first and last would read 'Microsoft.Lifetime'");

                Assert.That(LogCategories.Shorten("Program"),
                            Is.EqualTo("Program"));

                Assert.That(LogCategories.Shorten(""),
                            Is.EqualTo(""));

            });

        }

        #endregion

        #region WhatTheFactoryHandsOn_IsTheShortName()

        [Test]
        public void WhatTheFactoryHandsOn_IsTheShortName()
        {

            var asked = new List<String>();

            using (var loggerFactory = new ShortLogCategories(
                                           LoggerFactory.Create(builder => builder.AddProvider(new WhatWasAskedFor(asked)))))
            {

                // The type and not its name in quotation marks: this is the
                // category Microsoft.Extensions.Logging builds, which is the
                // one the rule has to fit.
                loggerFactory.CreateLogger<XMPPWebAPI>();

                loggerFactory.CreateLogger("org.GraphDefined.Vanaheimr.Ratatoskr.XMPPConnection");

            }

            Assert.That(asked, Is.EqualTo(new[] {
                                   "XMPPWebApp.XMPPWebAPI",
                                   "Ratatoskr.XMPPConnection"
                               }));

        }

        #endregion


        #region TheLogOfThisProgram_ShortensItsCategories()

        [Test]
        public void TheLogOfThisProgram_ShortensItsCategories()
        {

            using var loggerFactory = Program.ConsoleLog(false);

            // All a composed factory answers about itself from outside. It is
            // worth asking anyway: this console is the whole reason the rule
            // above exists, and a factory handed to Hermod, Ratatoskr and this
            // application without the wrapper would print the full names again
            // without anything failing.
            Assert.That(loggerFactory, Is.InstanceOf<ShortLogCategories>());

        }

        #endregion


        #region (private class) WhatWasAskedFor(Categories)

        /// <summary>
        /// A provider that writes nothing and remembers every category it was
        /// asked for.
        /// </summary>
        private sealed class WhatWasAskedFor(ICollection<String> Categories) : ILoggerProvider
        {

            public ILogger CreateLogger(String CategoryName)
            {
                Categories.Add(CategoryName);
                return NullLogger.Instance;
            }

            public void Dispose()
            { }

        }

        #endregion

    }

}
