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

using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Account
{

    /// <summary>
    /// Writing files and directories that only their owner may read.
    /// </summary>
    /// <remarks>
    /// The two settings files below this namespace hold something that is
    /// nobody else's business: the XMPP password in the clear, because SCRAM
    /// needs it, and the hash of the web password, which is worth a dictionary
    /// attack to whoever gets it. So does the archive, which is every word that
    /// was ever said and every file that was shared - it is written through
    /// here as well, which it was not at first: the credentials were 0600 on a
    /// server while the conversations beside them took whatever the umask
    /// happened to be, and 0644 is a readable conversation.
    ///
    /// <b>The mode goes on at creation and not afterwards.</b> Creating a file
    /// readable and restricting it once the content is in leaves a window, and
    /// the window is exactly as long as the writing.
    ///
    /// On Windows there is no mode to set - permissions there are ACLs, and a
    /// new file inherits them from its directory - so these fall back to an
    /// ordinary write, and what decides the answer is where the directory is.
    /// That is why the defaults moved out of the repository and into the
    /// per-user application data directory: a working copy on a data drive
    /// inherits rights for "Users" and "Authenticated Users" that the profile
    /// does not hand out. An ACL could be set here instead, and the reason not
    /// to is that it would only cover the files this program writes, while the
    /// directory decides for everything else that ever lands beside them.
    ///
    /// The same reasoning, and the same shape, as Ratatoskr's OwnerOnlyFile.
    /// </remarks>
    public static class OwnerOnlyFile
    {

        #region Write(Path, Content)

        /// <summary>
        /// Writes a file readable and writable by its owner alone (0600 on
        /// Unix); an ordinary write on Windows, where the directory decides.
        /// </summary>
        public static void Write(String Path, String Content)
        {

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path, Content);
                return;
            }

            using var stream = File.Open(Path,
                                         new FileStreamOptions {
                                             Mode            = FileMode.Create,
                                             Access          = FileAccess.Write,
                                             UnixCreateMode  = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                         });

            using var writer = new StreamWriter(stream);

            writer.Write(Content);

        }

        #endregion

        #region CreateDirectory(Path)

        /// <summary>
        /// Creates a directory nobody but its owner may enter (0700 on Unix),
        /// parents included; an ordinary one on Windows, where the parent
        /// decides.
        /// </summary>
        /// <remarks>
        /// The execute bit is what makes a directory enterable, so 0700 rather
        /// than 0600: without it the owner could not reach their own files.
        /// </remarks>
        public static void CreateDirectory(String Path)
        {

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path);
                return;
            }

            Directory.CreateDirectory(Path,
                                      UnixFileMode.UserRead |
                                      UnixFileMode.UserWrite |
                                      UnixFileMode.UserExecute);

        }

        #endregion

        #region Append(Path, Content)

        /// <summary>
        /// Appends text to a file that only its owner may read (0600 on Unix
        /// when this is what creates it).
        /// </summary>
        /// <remarks>
        /// UnixCreateMode applies to a file this call brings into being and
        /// says nothing about one that is already there - which is the right
        /// way round for an append: the mode of an existing month is whatever
        /// it was given when its first line was written.
        /// </remarks>
        public static void Append(String Path, String Content)
        {

            if (OperatingSystem.IsWindows())
            {
                File.AppendAllText(Path, Content, Encoding.UTF8);
                return;
            }

            using var stream = File.Open(Path,
                                         new FileStreamOptions {
                                             Mode            = FileMode.Append,
                                             Access          = FileAccess.Write,
                                             UnixCreateMode  = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                         });

            // Encoding.UTF8 and not a BOM-less one: StreamWriter writes the
            // preamble only at position zero, so this is byte for byte what
            // File.AppendAllText(..., Encoding.UTF8) wrote before - a month
            // whose first line arrived under the old code reads the same as one
            // that arrived under this.
            using var writer = new StreamWriter(stream, Encoding.UTF8);

            writer.Write(Content);

        }

        #endregion

        #region WriteAllBytesAsync(Path, Content, CancellationToken)

        /// <summary>
        /// Writes a file that only its owner may read (0600 on Unix).
        /// </summary>
        public static async Task WriteAllBytesAsync(String             Path,
                                                    Byte[]             Content,
                                                    CancellationToken  CancellationToken = default)
        {

            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllBytesAsync(Path, Content, CancellationToken);
                return;
            }

            using var stream = File.Open(Path,
                                         new FileStreamOptions {
                                             Mode            = FileMode.Create,
                                             Access          = FileAccess.Write,
                                             UnixCreateMode  = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                         });

            await stream.WriteAsync(Content, CancellationToken);

        }

        #endregion

    }

}
