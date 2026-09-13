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

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using NUnit.Framework;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp.Tests
{

    /// <summary>
    /// The TLS certificate the server answers with, and what happens when it is
    /// replaced on disk.
    /// </summary>
    /// <remarks>
    /// A Let's Encrypt certificate is renewed every few weeks, so this is the
    /// one part of the setup that changes on its own while the process runs.
    /// The failure it has to rule out is silent: serving an expired certificate
    /// because nobody restarted the process. The second one is louder but
    /// worse: refusing every connection because a renewal was caught halfway
    /// through writing a file.
    /// </remarks>
    [TestFixture]
    public class RotatingCertificateTests
    {

        #region Data

        private String directory = "";

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void CreateDirectory()
        {
            directory = Path.Combine(Path.GetTempPath(), $"xmppwebapp-tls-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveDirectory()
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (IOException)
            { }
        }

        #endregion

        #region (private) Make(CommonName) / WritePKCS12(...) / WritePEM(...) / Touch(...)

        /// <summary>
        /// A self-signed certificate with its key - a stand-in for whatever an
        /// ACME client would have put there.
        /// </summary>
        private static X509Certificate2 Make(String CommonName)
        {

            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest($"CN={CommonName}",
                                                 rsa,
                                                 HashAlgorithmName.SHA256,
                                                 RSASignaturePadding.Pkcs1);

            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(CommonName);
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());

            var now = DateTimeOffset.UtcNow;

            return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(90));

        }


        private String WritePKCS12(String CommonName, String? Password = null)
        {

            var path = Path.Combine(directory, "certificate.pfx");

            using var certificate = Make(CommonName);

            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));

            return path;

        }


        /// <summary>
        /// What certbot leaves behind: the certificate in one file, the key in
        /// another.
        /// </summary>
        private (String Certificate, String Key) WritePEM(String CommonName)
        {

            var certificatePath  = Path.Combine(directory, "fullchain.pem");
            var keyPath          = Path.Combine(directory, "privkey.pem");

            using var certificate = Make(CommonName);

            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
            File.WriteAllText(keyPath,         certificate.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

            return (certificatePath, keyPath);

        }


        /// <summary>
        /// Writes a file in a way a later check cannot miss. The stamp is the
        /// last write time and the length, and a file system whose time stamps
        /// are coarse would otherwise hide a change made in the same
        /// millisecond as the one before it - which no renewal ever is, but
        /// this test is.
        /// </summary>
        private static void Replace(String Path, String Content)
        {
            File.WriteAllText(Path, Content);
            File.SetLastWriteTimeUtc(Path, DateTime.UtcNow.AddSeconds(1));
        }

        private static void Replace(String Path, Byte[] Content)
        {
            File.WriteAllBytes(Path, Content);
            File.SetLastWriteTimeUtc(Path, DateTime.UtcNow.AddSeconds(1));
        }

        #endregion


        #region (private) MakeChain(CommonName)

        /// <summary>
        /// A root, an intermediate signed by it, and a server certificate
        /// signed by that - the shape a real certificate authority hands out.
        /// </summary>
        private static (X509Certificate2 Leaf, X509Certificate2 Intermediate) MakeChain(String CommonName)
        {

            var now = DateTimeOffset.UtcNow;

            using var rootKey      = RSA.Create(2048);
            var       rootRequest  = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

            using var intermediateKey      = RSA.Create(2048);
            var       intermediateRequest  = new CertificateRequest("CN=Test Intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));

            var       intermediate  = intermediateRequest.Create(root, now.AddDays(-1), now.AddYears(3), RandomNumberGenerator.GetBytes(16));
            using var signer        = intermediate.CopyWithPrivateKey(intermediateKey);

            using var leafKey      = RSA.Create(2048);
            var       leafRequest  = new CertificateRequest($"CN={CommonName}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            using var leaf = leafRequest.Create(signer, now.AddDays(-1), now.AddYears(1), RandomNumberGenerator.GetBytes(16));

            return (leaf.CopyWithPrivateKey(leafKey), intermediate);

        }

        #endregion


        #region AFullchainPEM_CarriesItsIntermediates()

        /// <summary>
        /// The file is called fullchain.pem because of what follows the first
        /// certificate in it, and that remainder is what a client which does
        /// not already know the intermediate needs.
        /// </summary>
        [Test]
        public void AFullchainPEM_CarriesItsIntermediates()
        {

            var (leaf, intermediate)  = MakeChain("chained.example.org");
            var certificatePath       = Path.Combine(directory, "fullchain.pem");
            var keyPath               = Path.Combine(directory, "privkey.pem");

            using (leaf)
            using (intermediate)
            {

                File.WriteAllText(certificatePath,
                                  leaf.ExportCertificatePem() +
                                  Environment.NewLine +
                                  intermediate.ExportCertificatePem());

                File.WriteAllText(keyPath, leaf.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

                var certificate = RotatingCertificate.FromPEM(certificatePath, keyPath);
                var chain       = certificate.CurrentChain;

                Assert.Multiple(() =>
                {
                    Assert.That(chain.Certificate.Subject,             Does.Contain("chained.example.org"));
                    Assert.That(chain.Certificate.HasPrivateKey,       Is.True);
                    Assert.That(chain.HasIntermediates,                Is.True);
                    Assert.That(chain.Intermediates,                   Has.Count.EqualTo(1));
                    Assert.That(chain.Intermediates[0].Subject,        Is.EqualTo("CN=Test Intermediate"));
                    Assert.That(chain.Intermediates[0].HasPrivateKey,  Is.False, "only the certificate of this server needs its key");
                });

            }

        }

        #endregion

        #region APKCS12WithAChain_CarriesItToo()

        /// <summary>
        /// The certificate of this server is the one with the private key; the
        /// rest of the file is what leads to it.
        /// </summary>
        [Test]
        public void APKCS12WithAChain_CarriesItToo()
        {

            var (leaf, intermediate)  = MakeChain("chained.example.org");
            var path                  = Path.Combine(directory, "certificate.pfx");

            using (leaf)
            using (intermediate)
            {

                var everything = new X509Certificate2Collection { leaf, intermediate };

                File.WriteAllBytes(path, everything.Export(X509ContentType.Pfx)!);

                var chain = RotatingCertificate.FromPKCS12(path).CurrentChain;

                Assert.Multiple(() =>
                {
                    Assert.That(chain.Certificate.Subject,       Does.Contain("chained.example.org"));
                    Assert.That(chain.Certificate.HasPrivateKey, Is.True);
                    Assert.That(chain.Intermediates,             Has.Count.EqualTo(1));
                    Assert.That(chain.Intermediates[0].Subject,  Is.EqualTo("CN=Test Intermediate"));
                });

            }

        }

        #endregion

        #region ASelfSignedPEM_HasNothingToSendAlong()

        /// <summary>
        /// A certificate that is its own issuer leads nowhere, and a root sent
        /// along is bytes on the wire that change nothing.
        /// </summary>
        [Test]
        public void ASelfSignedPEM_HasNothingToSendAlong()
        {

            var (certificatePath, keyPath) = WritePEM("alone.example.org");

            var chain = RotatingCertificate.FromPEM(certificatePath, keyPath).CurrentChain;

            Assert.Multiple(() =>
            {
                Assert.That(chain.Certificate.Subject,  Does.Contain("alone.example.org"));
                Assert.That(chain.HasIntermediates,     Is.False);
                Assert.That(chain.Intermediates,        Is.Empty);
            });

        }

        #endregion


        #region AFixedCertificate_NeverLooksAtADisk()

        [Test]
        public void AFixedCertificate_NeverLooksAtADisk()
        {

            using var made = DevCertificate.CreateSelfSigned();

            var certificate = RotatingCertificate.Fixed(made);

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Rotates,                Is.False);
                Assert.That(certificate.Files,                  Is.Empty);
                Assert.That(certificate.Current.Thumbprint,     Is.EqualTo(made.Thumbprint));
                Assert.That(certificate.Rotations,              Is.EqualTo(0));
                Assert.That(certificate.ToString(),             Does.Contain("not from a file"));
            });

        }

        #endregion

        #region APKCS12File_IsReadAndReReadWhenItChanges()

        [Test]
        public void APKCS12File_IsReadAndReReadWhenItChanges()
        {

            var path   = WritePKCS12("first.example.org");
            var kept   = new List<String>();

            var certificate = RotatingCertificate.FromPKCS12(path, CheckInterval: TimeSpan.Zero);

            certificate.OnRotated += now => kept.Add(now.Certificate.Subject);

            var before = certificate.Current.Thumbprint;

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current.Subject,  Does.Contain("first.example.org"));
                Assert.That(certificate.Rotates,          Is.True);
                Assert.That(certificate.Files,            Has.Count.EqualTo(1));
            });

            // The renewal.
            using (var renewed = Make("second.example.org"))
                Replace(path, renewed.Export(X509ContentType.Pfx));

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current.Subject,     Does.Contain("second.example.org"), "the new one took over without a restart");
                Assert.That(certificate.Current.Thumbprint,  Is.Not.EqualTo(before));
                Assert.That(certificate.Rotations,           Is.EqualTo(1));
                Assert.That(kept,                            Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region APEMPair_IsReadAndReReadWhenItChanges()

        /// <summary>
        /// The shape Let's Encrypt actually leaves on disk.
        /// </summary>
        [Test]
        public void APEMPair_IsReadAndReReadWhenItChanges()
        {

            var (certificatePath, keyPath) = WritePEM("first.example.org");

            var certificate = RotatingCertificate.FromPEM(certificatePath, keyPath, CheckInterval: TimeSpan.Zero);

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current.Subject,        Does.Contain("first.example.org"));
                Assert.That(certificate.Current.HasPrivateKey,  Is.True, "without the key it cannot serve TLS at all");
                Assert.That(certificate.Files,                  Has.Count.EqualTo(2), "both files are watched: either one may change");
            });

            using (var renewed = Make("second.example.org"))
            {
                Replace(certificatePath, renewed.ExportCertificatePem());
                Replace(keyPath,         renewed.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
            }

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current.Subject,        Does.Contain("second.example.org"));
                Assert.That(certificate.Current.HasPrivateKey,  Is.True);
                Assert.That(certificate.Rotations,              Is.EqualTo(1));
            });

        }

        #endregion

        #region AKeyInTheCertificateFile_NeedsNoSecondFile()

        [Test]
        public void AKeyInTheCertificateFile_NeedsNoSecondFile()
        {

            var path = Path.Combine(directory, "combined.pem");

            using (var made = Make("combined.example.org"))
                File.WriteAllText(path,
                                  made.ExportCertificatePem() +
                                  Environment.NewLine +
                                  made.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

            var certificate = RotatingCertificate.FromPEM(path);

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current.Subject,        Does.Contain("combined.example.org"));
                Assert.That(certificate.Current.HasPrivateKey,  Is.True);
                Assert.That(certificate.Files,                  Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region AHalfWrittenFile_KeepsTheCertificateInForce()

        /// <summary>
        /// The ordinary case, not the exception: certbot does not write
        /// atomically, so a check will sooner or later catch a file in the
        /// middle of being written. Refusing every TLS connection until the
        /// next renewal would be a far worse outcome than a moment of old
        /// certificate.
        /// </summary>
        [Test]
        public void AHalfWrittenFile_KeepsTheCertificateInForce()
        {

            var (certificatePath, keyPath) = WritePEM("first.example.org");

            var certificate = RotatingCertificate.FromPEM(certificatePath, keyPath, CheckInterval: TimeSpan.Zero);
            var before      = certificate.Current.Thumbprint;

            // Halfway through writing the new one.
            using var renewed = Make("second.example.org");
            var       pem     = renewed.ExportCertificatePem();

            Replace(certificatePath, pem[..(pem.Length / 2)]);

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current.Thumbprint,  Is.EqualTo(before), "the old one stays in force");
                Assert.That(certificate.Rotations,           Is.EqualTo(0));
            });

            // And when the writing finishes, the next look picks it up - the
            // failure did not poison anything.
            Replace(certificatePath, pem);
            Replace(keyPath,         renewed.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current.Subject,  Does.Contain("second.example.org"));
                Assert.That(certificate.Rotations,        Is.EqualTo(1));
            });

        }

        #endregion

        #region AFileThatVanishes_KeepsTheCertificateInForce()

        /// <summary>
        /// certbot replaces the symlinks in live/ one after the other, so one
        /// of the two may be missing for an instant.
        /// </summary>
        [Test]
        public void AFileThatVanishes_KeepsTheCertificateInForce()
        {

            var (certificatePath, keyPath) = WritePEM("first.example.org");

            var certificate = RotatingCertificate.FromPEM(certificatePath, keyPath, CheckInterval: TimeSpan.Zero);
            var before      = certificate.Current.Thumbprint;

            File.Delete(keyPath);

            Assert.That(certificate.Current.Thumbprint, Is.EqualTo(before), "a missing file is not a reason to stop serving TLS");

            using (var renewed = Make("second.example.org"))
            {
                Replace(certificatePath, renewed.ExportCertificatePem());
                Replace(keyPath,         renewed.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
            }

            Assert.That(certificate.Current.Subject, Does.Contain("second.example.org"), "and it recovers when both are there again");

        }

        #endregion

        #region AnUnchangedFile_IsNotSwappedForItself()

        /// <summary>
        /// A file rewritten with the same content is not a rotation: swapping
        /// the instance would throw away the TLS context Hermod caches per
        /// thumbprint for nothing.
        /// </summary>
        [Test]
        public void AnUnchangedFile_IsNotSwappedForItself()
        {

            var path         = WritePKCS12("first.example.org");
            var certificate  = RotatingCertificate.FromPKCS12(path, CheckInterval: TimeSpan.Zero);
            var before       = certificate.Current;

            Replace(path, File.ReadAllBytes(path));

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current,    Is.SameAs(before), "the very same object, so the cached TLS context stays warm");
                Assert.That(certificate.Rotations,  Is.EqualTo(0));
            });

        }

        #endregion

        #region TheInterval_KeepsTheConnectionPathOffTheDisk()

        /// <summary>
        /// The certificate is asked for once per accepted connection, so a
        /// check per connection would be a file system call per connection.
        /// </summary>
        [Test]
        public void TheInterval_KeepsTheConnectionPathOffTheDisk()
        {

            var path         = WritePKCS12("first.example.org");
            var certificate  = RotatingCertificate.FromPKCS12(path, CheckInterval: TimeSpan.FromMinutes(5));
            var before       = certificate.Current.Thumbprint;

            using (var renewed = Make("second.example.org"))
                Replace(path, renewed.Export(X509ContentType.Pfx));

            // A thousand connections within the interval: none of them looks.
            for (var i = 0; i < 1000; i++)
                _ = certificate.Current;

            Assert.Multiple(() =>
            {
                Assert.That(certificate.Current.Thumbprint,  Is.EqualTo(before));
                Assert.That(certificate.Rotations,           Is.EqualTo(0), "not yet - the interval has not passed");
            });

        }

        #endregion

        #region AFileThatIsNotACertificate_IsAnErrorAtTheStart()

        /// <summary>
        /// Only at the start: a server that cannot serve the certificate it was
        /// told to serve should say so and stop, rather than come up on a
        /// certificate nobody asked for.
        /// </summary>
        [Test]
        public void AFileThatIsNotACertificate_IsAnErrorAtTheStart()
        {

            var path = Path.Combine(directory, "nonsense.pem");

            File.WriteAllText(path, "-----BEGIN CERTIFICATE-----\nnot base64 at all\n-----END CERTIFICATE-----\n");

            Assert.Multiple(() =>
            {
                Assert.Throws<CryptographicException>(() => RotatingCertificate.FromPEM(path));
                Assert.Throws<CryptographicException>(() => RotatingCertificate.FromPKCS12(Path.Combine(directory, "not-there.pfx")));
            });

        }

        #endregion

        #region APasswordProtectedPKCS12_NeedsItsPassword()

        [Test]
        public void APasswordProtectedPKCS12_NeedsItsPassword()
        {

            var path = WritePKCS12("first.example.org", "s3cret");

            Assert.Multiple(() =>
            {
                Assert.That(RotatingCertificate.FromPKCS12(path, "s3cret").Current.Subject, Does.Contain("first.example.org"));
                Assert.Throws<CryptographicException>(() => RotatingCertificate.FromPKCS12(path, "wrong"));
            });

        }

        #endregion

        #region ManyThreadsAtOnce_SeeOneCertificate()

        /// <summary>
        /// Every accepted connection asks, and they do not queue up politely.
        /// </summary>
        [Test]
        public void ManyThreadsAtOnce_SeeOneCertificate()
        {

            var path         = WritePKCS12("first.example.org");
            var certificate  = RotatingCertificate.FromPKCS12(path, CheckInterval: TimeSpan.Zero);
            var seen         = new System.Collections.Concurrent.ConcurrentBag<String>();

            using (var renewed = Make("second.example.org"))
                Replace(path, renewed.Export(X509ContentType.Pfx));

            Parallel.For(0, 200, _ => seen.Add(certificate.Current.Thumbprint));

            Assert.Multiple(() =>
            {
                Assert.That(seen,                   Has.Count.EqualTo(200));
                Assert.That(seen.Distinct().Count(), Is.LessThanOrEqualTo(2), "the old one or the new one, never anything else");
                Assert.That(certificate.Rotations,  Is.EqualTo(1),            "and the swap happened exactly once");
            });

        }

        #endregion

    }

}
