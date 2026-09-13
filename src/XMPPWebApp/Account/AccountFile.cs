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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Account
{

    /// <summary>
    /// The file the account settings live in, JSON, written by the account
    /// page and read at every start.
    /// </summary>
    /// <remarks>
    /// It holds a working password in plain text, which is what the constants
    /// in Program.cs held before it - only that a file can be kept out of the
    /// repository, and is (.gitignore). On Unix it is created readable by its
    /// owner alone, and the mode goes on at creation, not afterwards: a file
    /// made readable and restricted once the content is in leaves a window
    /// exactly as long as the writing. On Windows permissions are ACLs
    /// inherited from the directory, so the file is as private as the
    /// directory it lies in.
    /// </remarks>
    public sealed class AccountFile
    {

        #region Data

        /// <summary>
        /// The default file name, below the repository root.
        /// </summary>
        public const String DefaultFileName = "xmpp-account.json";

        #endregion

        #region Properties

        /// <summary>
        /// The full path of the file.
        /// </summary>
        public String   Path      { get; }

        /// <summary>
        /// Whether the file exists.
        /// </summary>
        public Boolean  Exists
            => File.Exists(Path);

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The account file at the given path; it need not exist yet.
        /// </summary>
        public AccountFile(String Path)
        {

            if (String.IsNullOrWhiteSpace(Path))
                throw new ArgumentException("The path of the account file must not be empty!", nameof(Path));

            this.Path = System.IO.Path.GetFullPath(Path);

        }

        #endregion


        #region TryLoad(out Settings, out Error)

        /// <summary>
        /// The settings from the file. False without an error when there is no
        /// file, false with one when the file cannot be read or does not hold
        /// an account.
        /// </summary>
        public Boolean TryLoad(out AccountSettings?  Settings,
                               out String?           Error)
        {

            Settings  = null;
            Error     = null;

            if (!File.Exists(Path))
                return false;

            try
            {

                var json = JObject.Parse(File.ReadAllText(Path));

                if (AccountSettings.TryParse(json, out Settings, out var problem))
                    return true;

                Error = $"'{Path}' does not hold an account: {problem}";
                return false;

            }
            catch (Exception e)
            {
                Error = $"'{Path}' could not be read: {e.Message}";
                return false;
            }

        }

        #endregion

        #region Save(Settings)

        /// <summary>
        /// Write the settings, password included, replacing what was there.
        /// </summary>
        public void Save(AccountSettings Settings)
        {

            var directory = System.IO.Path.GetDirectoryName(Path);

            if (!String.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            OwnerOnlyFile.Write(Path, Settings.ToJSON(IncludePassword: true).ToString(Formatting.Indented) + Environment.NewLine);

        }

        #endregion

        #region Delete()

        /// <summary>
        /// Remove the file, e.g. to start afresh.
        /// </summary>
        /// <returns>Whether there was a file to remove.</returns>
        public Boolean Delete()
        {

            if (!File.Exists(Path))
                return false;

            File.Delete(Path);
            return true;

        }

        #endregion


    }

}
