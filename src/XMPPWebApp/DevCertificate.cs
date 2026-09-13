using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace org.GraphDefined.Vanaheimr.XMPPWebApp
{

    /// <summary>
    /// TLS certificates for development: a self-signed one for localhost made on
    /// the fly, or a PKCS#12 file from disk.
    /// </summary>
    public static class DevCertificate
    {

        /// <summary>
        /// The environment variable holding the password of a PKCS#12 file
        /// given with --cert, so that it never appears on a command line.
        /// </summary>
        public const String PasswordVariable = "XMPPWEBAPP_CERT_PASSWORD";


        #region CreateSelfSigned(HostName = "localhost")

        /// <summary>
        /// Create a self-signed server certificate for the given host name and
        /// the loopback addresses, valid for one year. Browsers will warn about
        /// it; that is expected for a development certificate.
        /// </summary>
        public static X509Certificate2 CreateSelfSigned(String HostName = "localhost")
        {

            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest(
                              $"CN={HostName}, O=Vanaheimr XMPPWebApp (development)",
                              rsa,
                              HashAlgorithmName.SHA256,
                              RSASignaturePadding.Pkcs1
                          );

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));   // serverAuth

            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName(HostName);
            subjectAlternativeNames.AddDnsName("localhost");
            subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
            subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());

            var now = DateTimeOffset.UtcNow;

            using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));

            // Round-trip through PKCS#12: the key of a freshly created
            // certificate is ephemeral, which SslStream on Windows rejects.
            return X509CertificateLoader.LoadPkcs12(
                       ephemeral.Export(X509ContentType.Pfx),
                       null
                   );

        }

        #endregion

        #region Load(Path)

        /// <summary>
        /// Load a PKCS#12 file; the password comes from the environment.
        /// </summary>
        public static X509Certificate2 Load(String Path)

            => X509CertificateLoader.LoadPkcs12FromFile(
                   Path,
                   Environment.GetEnvironmentVariable(PasswordVariable)
               );

        #endregion

    }

}
