// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal sealed class ClientCertificateExtractor(
    IOptionsMonitor<ClientCertificateAuthenticationOptions> options)
{
    private readonly IOptionsMonitor<ClientCertificateAuthenticationOptions> _options = options;

    public ClientCertificateExtractionResult Extract(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(ClientCertificateHttpContextItems.ExtractionResult, out var cached) &&
            cached is ClientCertificateExtractionResult cachedResult)
        {
            return cachedResult;
        }

        var result = ExtractCore(context, _options.CurrentValue);
        context.Items[ClientCertificateHttpContextItems.ExtractionResult] = result;
        return result;
    }

    private static ClientCertificateExtractionResult ExtractCore(
        HttpContext context,
        ClientCertificateAuthenticationOptions options)
    {
        var directCertificate = context.Connection.ClientCertificate;
        if (directCertificate is not null)
        {
            return ClientCertificateExtractionResult.Success(directCertificate, ClientCertificateSource.DirectTls);
        }

        var forwarded = options.ForwardedCertificate;
        if (!forwarded.Enabled ||
            string.IsNullOrWhiteSpace(forwarded.HeaderName) ||
            !context.Request.Headers.TryGetValue(forwarded.HeaderName, out var values) ||
            string.IsNullOrWhiteSpace(values.FirstOrDefault()))
        {
            return ClientCertificateExtractionResult.NotPresent();
        }

        var remoteIp = ResolveProxyPeerIpAddress(context);
        if (remoteIp is null || !IsTrustedProxy(remoteIp, forwarded.TrustedProxyNetworks))
        {
            return ClientCertificateExtractionResult.Failure(
                ClientCertificateValidationErrorCode.ForwardedCertificateUntrusted,
                "Forwarded client certificate headers are only accepted from configured trusted proxies.");
        }

        try
        {
            var certificate = ParseConfiguredCertificate(values.First()!, forwarded.Encoding);
            return ClientCertificateExtractionResult.Success(certificate, ClientCertificateSource.ForwardedHeader);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or FormatException)
        {
            return ClientCertificateExtractionResult.Failure(
                ClientCertificateValidationErrorCode.ForwardedCertificateInvalid,
                "Forwarded client certificate header could not be parsed.");
        }
    }

    public static X509Certificate2 ParseConfiguredCertificate(
        string headerValue,
        ForwardedClientCertificateEncoding encoding)
    {
        var value = headerValue.Trim();
        return encoding switch
        {
            ForwardedClientCertificateEncoding.Base64Der =>
                X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value)),
            ForwardedClientCertificateEncoding.Pem =>
                X509Certificate2.CreateFromPem(value),
            _ => ParseUrlEncodedPemOrDer(value),
        };
    }

    private static X509Certificate2 ParseUrlEncodedPemOrDer(string headerValue)
    {
        var decoded = Uri.UnescapeDataString(headerValue).Trim();
        if (decoded.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            return X509Certificate2.CreateFromPem(decoded);
        }

        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(decoded));
    }

    private static bool IsTrustedProxy(IPAddress remoteIp, IEnumerable<string> configuredNetworks)
    {
        foreach (var network in configuredNetworks)
        {
            if (string.IsNullOrWhiteSpace(network))
            {
                continue;
            }

            if (TryMatchesNetwork(remoteIp, network.Trim()))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress? ResolveProxyPeerIpAddress(HttpContext context)
    {
        if (context.Items.TryGetValue(ClientCertificateHttpContextItems.OriginalProxyPeerIpAddress, out var value) &&
            value is IPAddress originalProxyPeerIpAddress)
        {
            return originalProxyPeerIpAddress;
        }

        return context.Connection.RemoteIpAddress;
    }

    private static bool TryMatchesNetwork(IPAddress remoteIp, string network)
    {
        var slashIndex = network.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex < 0)
        {
            return IPAddress.TryParse(network, out var exact) && NormalizeAddress(exact).Equals(NormalizeAddress(remoteIp));
        }

        var addressPart = network[..slashIndex];
        var prefixPart = network[(slashIndex + 1)..];
        if (!IPAddress.TryParse(addressPart, out var baseAddress) ||
            !int.TryParse(prefixPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefixLength))
        {
            return false;
        }

        var normalizedBase = NormalizeAddress(baseAddress);
        var normalizedRemote = NormalizeAddress(remoteIp);
        var baseBytes = normalizedBase.GetAddressBytes();
        var remoteBytes = normalizedRemote.GetAddressBytes();
        if (baseBytes.Length != remoteBytes.Length || prefixLength < 0 || prefixLength > baseBytes.Length * 8)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (baseBytes[i] != remoteBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (baseBytes[fullBytes] & mask) == (remoteBytes[fullBytes] & mask);
    }

    private static IPAddress NormalizeAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

internal sealed record ClientCertificateExtractionResult
{
    public bool HasCertificate => Certificate is not null;

    public X509Certificate2? Certificate { get; init; }

    public ClientCertificateSource? Source { get; init; }

    public ClientCertificateValidationErrorCode? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public static ClientCertificateExtractionResult NotPresent() => new();

    public static ClientCertificateExtractionResult Success(
        X509Certificate2 certificate,
        ClientCertificateSource source)
        => new() { Certificate = certificate, Source = source };

    public static ClientCertificateExtractionResult Failure(
        ClientCertificateValidationErrorCode errorCode,
        string detail)
        => new() { ErrorCode = errorCode, ErrorDetail = detail };
}

internal enum ClientCertificateSource
{
    DirectTls = 0,
    ForwardedHeader = 1,
}
