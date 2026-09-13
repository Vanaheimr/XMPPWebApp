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
    /// The file the web login lives in, JSON, written by the settings page and
    /// read at every start.
    /// </summary>
    /// <remarks>
    /// Beside the account file rather than inside it, because the two hold
    /// different things and deserve different care: this one keeps a hash and
    /// could be shown to somebody without handing them an account, the other
    /// keeps a password in the clear because SCRAM needs it. Kept out of the
    /// repository all the same (.gitignore).
    /// </remarks>
    public sealed class WebLoginFile
    {

        #region Data

        /// <summary>
        /// The default file name, below the repository root.
        /// </summary>
        public const String DefaultFileName = "web-login.json";

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
        /// The web login file at the given path; it need not exist yet.
        /// </summary>
        public WebLoginFile(String Path)
        {

            if (String.IsNullOrWhiteSpace(Path))
                throw new ArgumentException("The path of the web login file must not be empty!", nameof(Path));

            this.Path = System.IO.Path.GetFullPath(Path);

        }

        #endregion


        #region TryLoad(out Login, out Error)

        /// <summary>
        /// The login from the file. False without an error when there is no
        /// file, false with one when the file cannot be read or does not hold
        /// a login.
        /// </summary>
        public Boolean TryLoad(out WebLoginSettings?  Login,
                               out String?            Error)
        {

            Login  = null;
            Error  = null;

            if (!File.Exists(Path))
                return false;

            try
            {

                var json = JObject.Parse(File.ReadAllText(Path));

                if (WebLoginSettings.TryParse(json, out Login, out var problem))
                    return true;

                Error = $"'{Path}' does not hold a web login: {problem}";
                return false;

            }
            catch (Exception e)
            {
                Error = $"'{Path}' could not be read: {e.Message}";
                return false;
            }

        }

        #endregion

        #region Save(Login)

        /// <summary>
        /// Write the login, password hash included, replacing what was there.
        /// </summary>
        public void Save(WebLoginSettings Login)
        {

            var directory = System.IO.Path.GetDirectoryName(Path);

            if (!String.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            OwnerOnlyFile.Write(Path, Login.ToJSON(IncludePasswordHash: true).ToString(Formatting.Indented) + Environment.NewLine);

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
