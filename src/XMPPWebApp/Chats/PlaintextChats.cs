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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Chats
{

    /// <summary>
    /// The conversations somebody has told this program to write in the clear,
    /// kept between starts.
    /// </summary>
    /// <remarks>
    /// <b>A switch that forgets is no switch.</b> Whoever turns encryption off
    /// for one contact does it because that contact's client is broken, and a
    /// broken client is still broken after a restart - a setting that lasted
    /// until the next one would have to be found and set again every morning,
    /// which is a thing people stop doing.
    ///
    /// One file for every account, keyed by account, because the account can be
    /// changed while the program runs and two accounts do not share their
    /// contacts' broken clients. It lives in the OMEMO directory, which is
    /// created 0700: <b>this is a list of who somebody talks to</b>, and that is
    /// nobody else's business even though it holds no keys.
    ///
    /// Written in full at every change, like the account file and the OMEMO
    /// store. A handful of JIDs is not a database.
    /// </remarks>
    public sealed class PlaintextChats
    {

        #region Data

        /// <summary>
        /// What the file is called below the OMEMO directory.
        /// </summary>
        public const String DefaultFileName = "plaintext-chats.json";

        private readonly String                            path;
        private readonly Lock                              @lock  = new();
        private readonly Dictionary<JID, HashSet<JID>>     off    = [];

        #endregion

        #region Properties

        /// <summary>
        /// The file this lives in.
        /// </summary>
        public String Path
            => path;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Reads the file, or starts empty when there is none.
        /// </summary>
        /// <remarks>
        /// <b>An unreadable file is not an error here, unlike the OMEMO store.</b>
        /// There the convenient answer would have been the dangerous one - a
        /// fresh identity is a silently changed fingerprint. Here the worst a
        /// lost file can do is encrypt to somebody whose client cannot read it,
        /// and they will say so. Refusing to start over a list of preferences
        /// would be the larger harm.
        /// </remarks>
        public PlaintextChats(String Path)
        {

            path = System.IO.Path.GetFullPath(Path);

            if (!File.Exists(path))
                return;

            try
            {

                var json = JObject.Parse(File.ReadAllText(path));

                foreach (var account in json.Properties())
                {

                    if (!JID.TryParse(account.Name, out var accountJID) ||
                        account.Value is not JArray chats)
                    {
                        continue;
                    }

                    var set = new HashSet<JID>();

                    foreach (var chat in chats)
                        if (JID.TryParse(chat.Value<String>(), out var chatJID))
                            set.Add(chatJID.Bare);

                    if (set.Count > 0)
                        off[accountJID.Bare] = set;

                }

            }
            catch (Exception)
            {
                // Started empty. See the remarks above.
            }

        }

        #endregion


        #region Chats(Account)

        /// <summary>
        /// The conversations of this account that are written in the clear.
        /// </summary>
        public IReadOnlyCollection<JID> Chats(JID Account)
        {
            lock (@lock)
                return off.TryGetValue(Account.Bare, out var set)
                           ? [.. set]
                           : [];
        }

        #endregion

        #region IsOff(Account, Chat) / Set(Account, Chat, Off)

        /// <summary>
        /// Whether this conversation is written in the clear.
        /// </summary>
        public Boolean IsOff(JID  Account,
                             JID  Chat)
        {
            lock (@lock)
                return off.TryGetValue(Account.Bare, out var set) &&
                       set.Contains(Chat.Bare);
        }

        /// <summary>
        /// Turns encryption off for one conversation, or back on, and writes
        /// the file.
        /// </summary>
        public void Set(JID      Account,
                        JID      Chat,
                        Boolean  Off)
        {
            lock (@lock)
            {

                if (Off)
                {

                    if (!off.TryGetValue(Account.Bare, out var set))
                        off[Account.Bare] = set = [];

                    if (!set.Add(Chat.Bare))
                        return;

                }

                else
                {

                    if (!off.TryGetValue(Account.Bare, out var set) ||
                        !set.Remove(Chat.Bare))
                    {
                        return;
                    }

                    if (set.Count == 0)
                        off.Remove(Account.Bare);

                }

                Write();

            }
        }

        #endregion

        #region (private) Write()

        /// <summary>
        /// Owner-only, like everything else this program keeps: a list of who
        /// somebody talks to is not a secret in the cryptographic sense and is
        /// still nobody else's business.
        /// </summary>
        private void Write()
        {

            var json = new JObject();

            foreach (var account in off)
                json.Add(new JProperty(account.Key.ToString(),
                                       new JArray(account.Value.Select(chat => chat.ToString()))));

            var directory = System.IO.Path.GetDirectoryName(path);

            if (!String.IsNullOrEmpty(directory))
                OwnerOnlyFile.CreateDirectory(directory);

            OwnerOnlyFile.Write(path, json.ToString());

        }

        #endregion

    }

}
