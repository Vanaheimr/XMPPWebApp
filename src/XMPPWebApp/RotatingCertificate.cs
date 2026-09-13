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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// The TLS certificate the server answers with, re-read from disk when it
    /// changes - so that a renewal is picked up without a restart.
    /// </summary>
    /// <remarks>
    /// A Let's Encrypt certificate is replaced every few weeks, and a server
    /// that read its certificate once at the start serves the old one until
    /// somebody notices. Hermod asks for the certificate <i>per connection</i>
    /// (TCPConnection invokes the ServerCertificateSelector when it accepts a
    /// client), so the whole of the fix fits here.
    ///
    /// <b>Asked, not told.</b> The files are compared - last write time and
    /// length - rather than watched. A FileSystemWatcher answers "did something
    /// happen while I was listening", which is the wrong question: an event
    /// lost to a buffer overflow, a container bind mount or a replaced symlink
    /// is lost for good, and nothing would ever ask again. What that costs is
    /// up to <see cref="CheckInterval"/> of delay for a certificate that was
    /// renewed a month before it expires.
    ///
    /// <b>A renewal may not take the server down.</b> A file being written is
    /// what a check usually catches - certbot does not write atomically - so a
    /// load that fails changes nothing: the certificate in force stays in
    /// force, the failure is logged once, and the next check tries again.
    ///
    /// <b>The replaced certificate is not disposed.</b> A handshake that is
    /// under way still holds it, and Hermod caches an SslStreamCertificateContext
    /// per thumbprint. Freeing the handle underneath either is a crash for the
    /// sake of a few hundred bytes per rotation, which the finalizer reclaims
    /// anyway.
    /// </remarks>
    public sealed class RotatingCertificate
    {

        #region Data

        /// <summary>
        /// How often the files are looked at, at the most.
        /// </summary>
        public static readonly TimeSpan  DefaultCheckInterval  = TimeSpan.FromSeconds(5);

        private readonly Func<ServerCertificateChain>  load;
        private readonly Int64                        checkIntervalMilliseconds;
        private readonly ILogger                      logger;
        private readonly Lock                         @lock                      = new();

        private ServerCertificateChain  current;
        private String                  stamp;
        private Int64                   nextCheck;
        private String?                 reportedFailure;
        private Int32                   rotations;

        #endregion

        #region Properties

        /// <summary>
        /// The files this certificate is read from; empty when it was not read
        /// from any (a self-signed one made at the start).
        /// </summary>
        public IReadOnlyList<String>  Files          { get; }

        /// <summary>
        /// How often the files are looked at, at the most.
        /// </summary>
        public TimeSpan               CheckInterval  { get; }

        /// <summary>
        /// How many times a new certificate has been picked up.
        /// </summary>
        public Int32                  Rotations
            => Volatile.Read(ref rotations);

        /// <summary>
        /// Whether this certificate can change at all.
        /// </summary>
        public Boolean                Rotates
            => Files.Count > 0;

        /// <summary>
        /// The certificate to answer with now, together with the intermediates
        /// that lead to it. Called once per accepted connection, so the usual
        /// path through it is one comparison.
        /// </summary>
        public ServerCertificateChain  CurrentChain
        {
            get
            {

                if (Rotates && Environment.TickCount64 >= Volatile.Read(ref nextCheck))
                    Reload();

                return Volatile.Read(ref current);

            }
        }

        /// <summary>
        /// The certificate alone - what the narrower selector of Hermod asks
        /// for, and what the console line reports.
        /// </summary>
        public X509Certificate2        Current
            => CurrentChain.Certificate;

        #endregion

        #region Events

        /// <summary>
        /// A new certificate took the place of the old one.
        /// </summary>
        public event Action<ServerCertificateChain>?  OnRotated;

        #endregion

        #region Constructor(s)

        private RotatingCertificate(Func<ServerCertificateChain>  Load,
                                    IReadOnlyList<String>         Files,
                                    TimeSpan?                     CheckInterval,
                                    ILogger?                      Logger)
        {

            this.load                       = Load;
            this.Files                      = Files;
            this.CheckInterval              = CheckInterval ?? DefaultCheckInterval;
            this.checkIntervalMilliseconds  = (Int64) this.CheckInterval.TotalMilliseconds;
            this.logger                     = Logger ?? NullLogger.Instance;

            this.current                    = Load();
            this.stamp                      = Stamp();
            this.nextCheck                  = Environment.TickCount64 + checkIntervalMilliseconds;

        }

        #endregion


        #region (static) FromPKCS12(Path, Password, ...)

        /// <summary>
        /// A certificate and its key from one PKCS#12 file.
        /// </summary>
        public static RotatingCertificate FromPKCS12(String           Path,
                                                     String?          Password        = null,
                                                     TimeSpan?        CheckInterval   = null,
                                                     ILoggerFactory?  LoggerFactory   = null)
        {

            var path = System.IO.Path.GetFullPath(Path);

            return new RotatingCertificate(
                       () => {

                           // The whole file, not just the leaf: a PKCS#12 may
                           // carry the intermediates, and they belong on the
                           // wire.
                           var everything = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, Password);

                           return Chain(everything);

                       },
                       [ path ],
                       CheckInterval,
                       LoggerFactory?.CreateLogger<RotatingCertificate>()
                   );

        }

        #endregion

        #region (static) FromPEM(CertificatePath, KeyPath, ...)

        /// <summary>
        /// A certificate and its key from PEM files - what an ACME client such
        /// as certbot writes, so that no conversion step has to sit in a
        /// renewal hook and be forgotten.
        /// </summary>
        /// <param name="CertificatePath">The certificate, e.g. fullchain.pem.</param>
        /// <param name="KeyPath">
        /// The private key, e.g. privkey.pem, or null when the key is in the
        /// certificate file itself.
        /// </param>
        public static RotatingCertificate FromPEM(String           CertificatePath,
                                                  String?          KeyPath         = null,
                                                  TimeSpan?        CheckInterval   = null,
                                                  ILoggerFactory?  LoggerFactory   = null)
        {

            var certificatePath  = System.IO.Path.GetFullPath(CertificatePath);
            var keyPath          = KeyPath is not null
                                       ? System.IO.Path.GetFullPath(KeyPath)
                                       : null;

            return new RotatingCertificate(
                       () => {

                           using var fromPem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);

                           // Round-trip through PKCS#12: a key read from a PEM
                           // file is ephemeral, and SslStream on Windows refuses
                           // an ephemeral key. The same detour CreateSelfSigned
                           // takes, for the same reason.
                           var leaf = X509CertificateLoader.LoadPkcs12(
                                          fromPem.Export(X509ContentType.Pfx),
                                          null
                                      );

                           // CreateFromPemFile reads the first certificate and
                           // stops. A fullchain.pem holds the intermediates
                           // after it, and they are the reason the file is
                           // called that.
                           var everything = new X509Certificate2Collection();
                           everything.ImportFromPemFile(certificatePath);

                           return new ServerCertificateChain(
                                      leaf,
                                      everything.OfType<X509Certificate2>().Skip(1)
                                  );

                       },
                       keyPath is not null
                           ? [ certificatePath, keyPath ]
                           : [ certificatePath ],
                       CheckInterval,
                       LoggerFactory?.CreateLogger<RotatingCertificate>()
                   );

        }

        #endregion

        #region (static) Fixed(Certificate)

        /// <summary>
        /// A certificate that came from nowhere on disk and therefore never
        /// changes - the self-signed one of --https.
        /// </summary>
        public static RotatingCertificate Fixed(X509Certificate2 Certificate)

            => new (() => new ServerCertificateChain(Certificate),
                    [],
                    null,
                    null);

        #endregion


        #region (private) Reload()

        private void Reload()
        {
            lock (@lock)
            {

                // Somebody else looked while this call was waiting for the lock.
                if (Environment.TickCount64 < nextCheck)
                    return;

                nextCheck = Environment.TickCount64 + checkIntervalMilliseconds;

                var stamp = Stamp();

                if (stamp == this.stamp)
                    return;

                ServerCertificateChain loaded;

                try
                {
                    loaded = load();
                }
                catch (Exception e)
                {

                    // Half a file is the usual reason and it cures itself: the
                    // stamp is deliberately not taken over, so the next check
                    // tries again. Said once per reason, or a renewal that
                    // leaves something broken would fill the log.
                    if (reportedFailure != e.Message)
                    {
                        reportedFailure = e.Message;
                        logger.LogWarning("The TLS certificate changed but could not be loaded: {Error}. " +
                                          "The certificate in force is kept; trying again every {Interval}.",
                                          e.Message, CheckInterval);
                    }

                    return;

                }

                this.stamp       = stamp;
                reportedFailure  = null;

                if (loaded.CacheKey == current.CacheKey)
                {
                    // Touched, rewritten, but the same certificate. Nothing to
                    // announce - and nothing to swap, so that the cached TLS
                    // context of the one in force stays warm.
                    return;
                }

                var previous = current;

                Volatile.Write(ref current, loaded);
                Interlocked.Increment(ref rotations);

                logger.LogInformation("A new TLS certificate is in force: {Subject}, valid until {NotAfter:u}, " +
                                      "SHA-256 {Thumbprint}, {Intermediates} intermediate(s) (was {Previous})",
                                      loaded.Certificate.Subject,
                                      loaded.Certificate.NotAfter.ToUniversalTime(),
                                      loaded.Certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
                                      loaded.Intermediates.Count,
                                      previous.Certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));

                try
                {
                    OnRotated?.Invoke(loaded);
                }
                catch (Exception e)
                {
                    logger.LogDebug("A listener of the certificate rotation failed: {Error}", e.Message);
                }

            }
        }

        #endregion

        #region (private static) Chain(Collection)

        /// <summary>
        /// A loaded collection sorted into "the certificate of this server" and
        /// "what leads to it".
        /// </summary>
        /// <remarks>
        /// The one with the private key is this server's: a PKCS#12 holds
        /// exactly one such, and the rest of the file is the chain. Without one
        /// there is nothing to serve TLS with, which is worth an exception
        /// rather than a puzzling handshake failure later.
        /// </remarks>
        private static ServerCertificateChain Chain(X509Certificate2Collection Collection)
        {

            var all   = Collection.OfType<X509Certificate2>().ToList();
            var leaf  = all.FirstOrDefault(certificate => certificate.HasPrivateKey)
                            ?? throw new CryptographicException("None of the certificates in this file has a private key!");

            return new ServerCertificateChain(
                       leaf,
                       all.Where(certificate => certificate != leaf)
                   );

        }

        #endregion

        #region (private) Stamp()

        /// <summary>
        /// What the files look like from outside: when they were written and
        /// how long they are.
        /// </summary>
        /// <remarks>
        /// Not a hash of the content: the point is to be cheap enough to do on
        /// the way to a TLS handshake. A renewal changes both of these in
        /// practice, and a change that changed neither would be picked up by
        /// nothing short of reading the file every time.
        ///
        /// A file that is not there right now reads as absent rather than as an
        /// error - certbot replaces the symlinks in live/ one at a time, and
        /// the moment in between is not worth a warning.
        /// </remarks>
        private String Stamp()
        {

            var builder = new StringBuilder();

            foreach (var file in Files)
            {

                try
                {

                    var info = new FileInfo(file);

                    builder.Append(info.Exists
                                       ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}"
                                       : "-");

                }
                catch (Exception e)
                {
                    builder.Append("?:").Append(e.GetType().Name);
                }

                builder.Append('|');

            }

            return builder.ToString();

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{current.Certificate.Subject}, valid until {current.Certificate.NotAfter.ToUniversalTime():u}" +
               (Rotates
                    ? $", re-read from {String.Join(" and ", Files)} every {CheckInterval.TotalSeconds:0}s"
                    : ", not from a file");

        #endregion

    }

}
