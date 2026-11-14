// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Trust for an approved operation replayed to this server's own admin route over the TLS loopback.
/// When Kestrel terminates TLS, the replay connects to 127.0.0.1 and receives the server's own certificate.
/// That certificate may be self-signed or issued by a private CA the host does not trust, so default validation
/// would fail the approved operation. The replay pins the exact certificate Kestrel served on the originating
/// connection and accepts only that certificate; without a pin, default validation applies unchanged.
/// The replay's TLS server name follows the copied Host header. If Kestrel selects certificates per server name
/// and a client sent this request on a connection negotiated for a different name, the replay can receive another
/// of the server's certificates and fails closed.
/// </summary>
internal static class AdminOperationLoopbackTls
{
    /// <summary>SHA-256 hash of the certificate the server presented on the request that submitted the replay.</summary>
    public static readonly HttpRequestOptionsKey<string> ExpectedServerCertificateHash =
        new("Honua.AdminOperationLoopback.ExpectedServerCertificateSha256");

    /// <summary>
    /// Pins a TLS loopback replay to the certificate this server presented on the originating connection.
    /// Plain-HTTP replays and connections without an <see cref="ISslStreamFeature"/> (for example HTTP/3) are left
    /// to default validation.
    /// </summary>
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

    /// <summary>Creates the primary handler for every admin operation loopback replay client.</summary>
    public static HttpMessageHandler CreateHandler() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = Validate,
    };

    /// <summary>
    /// Accepts the presented certificate only when it is the pinned certificate the server itself served; with no
    /// pin, requires an error-free default validation.
    /// </summary>
    public static bool Validate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
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
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presented, pinned);
    }

    private static string HashOf(X509Certificate certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);
}
