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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.XMPPWebApp.Account;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// The account file: what the account page writes and every start reads.
    /// </summary>
    [TestFixture]
    public class AccountFileTests
    {

        private String  path  = null!;

        [SetUp]
        public void PickATempFile()
        {
            path = Path.Combine(Path.GetTempPath(), $"xmpp-account-test-{Guid.NewGuid():N}.json");
        }

        [TearDown]
        public void RemoveTheTempFile()
        {
            if (File.Exists(path))
                File.Delete(path);
        }


        #region NoFile_IsNoAccountAndNoError()

        [Test]
        public void NoFile_IsNoAccountAndNoError()
        {

            var file = new AccountFile(path);

            Assert.Multiple(() =>
            {
                Assert.That(file.Exists,                          Is.False);
                Assert.That(file.TryLoad(out var settings, out var error), Is.False);
                Assert.That(settings,                             Is.Null);
                Assert.That(error,                                Is.Null, "the normal first start is not an error");
            });

        }

        #endregion

        #region SaveThenLoad_IsTheSameAccount()

        [Test]
        public void SaveThenLoad_IsTheSameAccount()
        {

            var file = new AccountFile(path);

            AccountSettings.TryCreate("alice@example.org", "secret", "wss://x.example/ws", "SCRAM-SHA-1", false, true, out var settings, out _);

            file.Save(settings!);

            Assert.That(file.Exists, Is.True);
            Assert.That(file.TryLoad(out var loaded, out var error), Is.True);
            Assert.That(error, Is.Null);

            Assert.Multiple(() =>
            {
                Assert.That(loaded!.JID,                  Is.EqualTo(settings!.JID));
                Assert.That(loaded.Password,              Is.EqualTo("secret"), "the password is in the file");
                Assert.That(loaded.WebSocketURI,          Is.EqualTo("wss://x.example/ws"));
                Assert.That(loaded.MinimumSaslMechanism,  Is.EqualTo("SCRAM-SHA-1"));
                Assert.That(loaded.TrustAnnouncement,     Is.True);
            });

        }

        #endregion

        #region Save_ReplacesWhatWasThere()

        [Test]
        public void Save_ReplacesWhatWasThere()
        {

            var file = new AccountFile(path);

            AccountSettings.TryCreate("alice@example.org", "one", "wss://a.example/ws", null, false, false, out var first,  out _);
            AccountSettings.TryCreate("bob@example.org",   "two", "wss://b.example/ws", null, false, false, out var second, out _);

            file.Save(first!);
            file.Save(second!);

            file.TryLoad(out var loaded, out _);

            Assert.That(loaded!.JID.ToString(),  Is.EqualTo("bob@example.org"));
            Assert.That(loaded.Password,         Is.EqualTo("two"));

        }

        #endregion

        #region ADamagedFile_IsAnError()

        [Test]
        public void ADamagedFile_IsAnError()
        {

            File.WriteAllText(path, "{ not json");

            var file = new AccountFile(path);

            Assert.That(file.TryLoad(out var settings, out var error), Is.False);
            Assert.That(settings, Is.Null);
            Assert.That(error,    Is.Not.Null, "a file that is there but broken is worth saying so");

        }

        #endregion

        #region AFileWithoutAnAccount_IsAnError()

        [Test]
        public void AFileWithoutAnAccount_IsAnError()
        {

            File.WriteAllText(path, "{ \"jid\": \"alice@example.org\" }");

            var file = new AccountFile(path);

            Assert.That(file.TryLoad(out _, out var error), Is.False);
            Assert.That(error, Does.Contain("does not hold an account"));

        }

        #endregion

        #region Delete_RemovesTheFile()

        [Test]
        public void Delete_RemovesTheFile()
        {

            var file = new AccountFile(path);

            AccountSettings.TryCreate("alice@example.org", "secret", null, null, false, false, out var settings, out _);
            file.Save(settings!);

            Assert.Multiple(() =>
            {
                Assert.That(file.Delete(),  Is.True);
                Assert.That(file.Exists,    Is.False);
                Assert.That(file.Delete(),  Is.False, "already gone");
            });

        }

        #endregion

    }

}
