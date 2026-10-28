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

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// What a log line calls the logger it comes from.
    /// </summary>
    /// <remarks>
    /// Every line named its logger in full - "info:
    /// org.GraphDefined.Vanaheimr.Ratatoskr.XMPPConnection[0] WebSocket
    /// connected" - and the first twenty-seven characters of that are the same
    /// organisation in every line of every run. What tells a reader something
    /// is which of the three products is speaking and which class in it, so
    /// that is what is kept: "Ratatoskr.XMPPConnection".
    ///
    /// Shortened where the line is written and not where the loggers are
    /// made, which is to say by <see cref="LogLineFormatter"/>: a logger
    /// keeps the name of its type, every filter and every other sink sees
    /// that name, and only the console of this program says it short. It was
    /// a wrapper around the logger factory first, for as long as the stock
    /// formatter was the one writing the lines and had no say over the
    /// category it was handed.
    /// </remarks>
    public static class LogCategories
    {

        #region Data

        /// <summary>
        /// The prefix every type of this organisation carries, Hermod, Styx
        /// and Ratatoskr alike.
        /// </summary>
        public const String Organisation = "org.GraphDefined.Vanaheimr.";

        #endregion


        #region Shorten(Category)

        /// <summary>
        /// The product and the class out of a category of this organisation;
        /// anything else as it is.
        /// </summary>
        /// <remarks>
        /// For Ratatoskr and this application that is already everything the
        /// name holds, their types lying flat in one namespace each. For
        /// Hermod it drops what lies in between, so that
        /// "...Hermod.HTTP.HTTPServer" reads "Hermod.HTTPServer" - and a
        /// nested type loses its outer one the same way, which is what a rule
        /// of one line costs.
        ///
        /// Under <see cref="Organisation"/> only. "Microsoft.Hosting.Lifetime"
        /// is somebody else's naming, and its first and last would read
        /// "Microsoft.Lifetime" - a name for something that does not exist.
        /// </remarks>
        /// <param name="Category">The category a logger was asked for.</param>
        public static String Shorten(String Category)
        {

            if (!Category.StartsWith(Organisation, StringComparison.Ordinal))
                return Category;

            var rest   = Category[Organisation.Length..];
            var first  = rest.IndexOf('.');
            var last   = rest.LastIndexOf('.');

            return first < 0 || first == last
                       ? rest
                       : String.Concat(rest[..first], rest[last..]);

        }

        #endregion

    }

}
