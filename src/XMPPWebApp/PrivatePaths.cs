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
    /// Where this program keeps what is nobody else's business.
    /// </summary>
    /// <remarks>
    /// The account file, the web login and the archive used to default to the
    /// repository root, and a working copy is not a private place. On Windows it
    /// is often not private at all: a checkout on a data drive inherits that
    /// drive's rights, and on the machine this was written on that meant
    /// "Users: ReadAndExecute" and "Authenticated Users: Modify" on a file
    /// holding an XMPP password in the clear. The same file under the profile
    /// has neither entry, because the profile hands neither out.
    ///
    /// Which is why the answer is where the files are rather than an ACL on each
    /// one: an ACL would cover what this program writes and nothing else, while
    /// the directory decides for everything that ever lands beside them.
    /// </remarks>
    public static class PrivatePaths
    {

        #region Data

        /// <summary>
        /// The directory this program makes for itself below the per-user one.
        /// </summary>
        public const String ApplicationName      = "XMPPWebApp";

        /// <summary>
        /// Where the OMEMO keys and sessions go, below <see cref="Directory"/>.
        /// </summary>
        /// <remarks>
        /// A directory of its own rather than a file beside the others, because
        /// there is one per account and the account can change while the
        /// program runs. It is created 0700 on Unix - what lies in there is the
        /// identity key and every chain key of every session, and it is not
        /// encrypted.
        /// </remarks>
        public const String OmemoDirectoryName   = "omemo";

        #endregion


        #region Directory()

        /// <summary>
        /// %LOCALAPPDATA%\XMPPWebApp on Windows, $XDG_DATA_HOME/XMPPWebApp -
        /// ~/.local/share/XMPPWebApp - everywhere else.
        /// </summary>
        /// <remarks>
        /// SpecialFolderOption.Create because the first start is the one that
        /// has to work: without it LocalApplicationData answers with a path
        /// whether or not anything is there.
        /// </remarks>
        public static String Directory()

            => Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                             Environment.SpecialFolderOption.Create),
                   ApplicationName
               );

        #endregion

        #region For(Name, WasBelow, IsBelow, Moved)

        /// <summary>
        /// Where a file or directory lives when the command line does not say -
        /// and, when one is still lying in the old place, that one, with a
        /// sentence added to <paramref name="Moved"/> saying so.
        /// </summary>
        /// <remarks>
        /// Starting fresh in silence would leave somebody wondering where their
        /// conversations went while they sat on the disk all along, and moving
        /// somebody's files for them is not this program's business either. So
        /// the old place keeps working and says out loud that it is the old
        /// place. The new one wins as soon as there is anything there, which is
        /// what makes the move a move and not a fork.
        /// </remarks>
        /// <param name="Name">The file or directory, e.g. "xmpp-account.json".</param>
        /// <param name="WasBelow">Where it used to default to - the repository root.</param>
        /// <param name="IsBelow">Where it defaults to now - see <see cref="Directory"/>.</param>
        /// <param name="Moved">Collects a sentence per thing still found in the old place.</param>
        public static String For(String               Name,
                                 String               WasBelow,
                                 String               IsBelow,
                                 ICollection<String>  Moved)
        {

            var wasHere  = Path.Combine(WasBelow, Name);
            var isHere   = Path.Combine(IsBelow,  Name);

            if (Exists(isHere))
                return isHere;

            if (Exists(wasHere))
            {

                Moved.Add($"'{wasHere}' is still in use. The repository is no longer where this is kept, " +
                          $"because a working copy is not a private place - move it to '{isHere}'.");

                return wasHere;

            }

            return isHere;

        }

        /// <summary>
        /// A name is taken whether it names a file or a directory: the account
        /// and the login are files, the archive is a directory.
        /// </summary>
        private static Boolean Exists(String Path)

            => File.     Exists(Path) ||
               System.IO.Directory.Exists(Path);

        #endregion

    }

}
