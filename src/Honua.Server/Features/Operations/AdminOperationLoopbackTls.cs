// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Honua.Server.Features.Operations;

// Trust for an approved operation replayed to this server's own admin route over the TLS loopback.
// When Kestrel terminates TLS, the replay connects to 127.0.0.1 and receives the server's own certificate, which
// may be self-signed or issued by a private CA the host does not trust, so default validation would fail the
// approved operation. The replay pins the exact certificate Kestrel served on the originating connection and
// accepts only that certificate; without a pin (plain HTTP, or no SslStream as on HTTP/3) default validation
// applies unchanged. The replay's TLS server name follows the copied Host header: if Kestrel selects certificates
// per server name and a client sent this request on a connection negotiated for a different name, the replay can
// receive another of the server's certificates and fails closed.
internal static class AdminOperationLoopbackTls
{
    public static readonly HttpRequestOptionsKey<string> ExpectedServerCertificateHash =
        new("Honua.AdminOperationLoopback.ExpectedServerCertificateSha256");

    public static void AddClient(IServiceCollection services, string clientName) =>
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(CreateHandler);

    public static void PinServedCertificate(HttpRequestMessage message, HttpContext current)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(current);
        if (message.RequestUri?.Scheme != Uri.UriSchemeHttps)
            return;
        var certificate = current.Features.Get<ISslStreamFeature>()?.SslStream.LocalCertificate;
        if (certificate is not null)
            message.Options.Set(ExpectedServerCertificateHash, HashOf(certificate));
    }

    public static HttpMessageHandler CreateHandler() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, certificate, _, errors) => Validate(request, certificate, errors),
    };

    public static bool Validate(HttpRequestMessage request, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Options.TryGetValue(ExpectedServerCertificateHash, out var expected) || string.IsNullOrEmpty(expected))
            return errors == SslPolicyErrors.None;
        if (certificate is null)
            return false;
        var presented = Convert.FromHexString(HashOf(certificate));
        byte[] pinned;
        try
        {
            pinned = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            // A malformed pin cannot match any certificate; refuse rather than fall back to default validation.
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presented, pinned);
    }

    private static string HashOf(X509Certificate certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);
}
