
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

string outputDirectory = Path.GetFullPath("certs");
Directory.CreateDirectory(outputDirectory);

string pfxPath = Path.Combine(outputDirectory, "server.pfx");
string cerPath = Path.Combine(outputDirectory, "server.cer");

using RSA rsa = RSA.Create(2048);

var request = new CertificateRequest(
    "CN=localhost",
    rsa,
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1);

request.CertificateExtensions.Add(
    new X509BasicConstraintsExtension(
        certificateAuthority: false,
        hasPathLengthConstraint: false,
        pathLengthConstraint: 0,
        critical: true));

request.CertificateExtensions.Add(
    new X509KeyUsageExtension(
        X509KeyUsageFlags.DigitalSignature |
        X509KeyUsageFlags.KeyEncipherment,
        critical: true));

request.CertificateExtensions.Add(
    new X509EnhancedKeyUsageExtension(
        new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1") // Server Authentication
        },
        critical: true));

var san = new SubjectAlternativeNameBuilder();
san.AddDnsName("localhost");
san.AddIpAddress(System.Net.IPAddress.Loopback);
san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
request.CertificateExtensions.Add(san.Build());

using X509Certificate2 certificate = request.CreateSelfSigned(
    DateTimeOffset.UtcNow.AddMinutes(-5),
    DateTimeOffset.UtcNow.AddYears(1));

string pfxPassword = Convert.ToBase64String(
    RandomNumberGenerator.GetBytes(24));

byte[] pfxBytes = certificate.Export(
    X509ContentType.Pfx,
    pfxPassword);

byte[] cerBytes = certificate.Export(
    X509ContentType.Cert);

File.WriteAllBytes(pfxPath, pfxBytes);
File.WriteAllBytes(cerPath, cerBytes);

Console.WriteLine("Certificate created successfully.");
Console.WriteLine($"PFX: {pfxPath}");
Console.WriteLine($"CER: {cerPath}");
Console.WriteLine($"PFX password: {pfxPassword}");
Console.WriteLine("Store the password securely; do not commit it to source control.");
