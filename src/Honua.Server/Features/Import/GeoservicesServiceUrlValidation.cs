// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.Server.Features.Import;

internal readonly record struct GeoservicesServiceUrlValidationResult(bool IsValid, string? ErrorMessage)
{
    public static GeoservicesServiceUrlValidationResult Success()
        => new(true, null);

    public static GeoservicesServiceUrlValidationResult Failure(string message)
        => new(false, message);
}

internal static class GeoservicesServiceUrlValidation
{
    internal const string InvalidHttpsUrlMessage = "ServiceUrl must be a valid HTTPS URL";
    internal const string EmbeddedCredentialsMessage = "ServiceUrl must not include embedded credentials.";
    internal const string CredentialQueryParameterMessage =
        "ServiceUrl must not include credential query parameters; use the credentials object instead.";
    internal const string InvalidServiceRootMessage =
        "ServiceUrl must target an ArcGIS Service root URL (FeatureServer or MapServer), not a layer/table URL.";
    internal const string DisallowedAddressMessage =
        "ServiceUrl resolves to a private, loopback, or unresolvable network address, which is not allowed.";

    public static Task<GeoservicesServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        CancellationToken cancellationToken = default)
        => ValidateAsync(serviceUrl, ResolveHostAddressesAsync, cancellationToken);

    internal static async Task<GeoservicesServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return GeoservicesServiceUrlValidationResult.Failure(InvalidHttpsUrlMessage);
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return GeoservicesServiceUrlValidationResult.Failure(EmbeddedCredentialsMessage);
        }

        if (HasCredentialQueryParameter(uri))
        {
            return GeoservicesServiceUrlValidationResult.Failure(CredentialQueryParameterMessage);
        }

        if (!IsServiceRootUrl(uri))
        {
            return GeoservicesServiceUrlValidationResult.Failure(InvalidServiceRootMessage);
        }

        if (await NetworkAddressValidator.IsDisallowedAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return GeoservicesServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return GeoservicesServiceUrlValidationResult.Success();
    }

    private static bool IsServiceRootUrl(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        var lastSegment = segments[^1];
        return lastSegment.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
               || lastSegment.Equals("MapServer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCredentialQueryParameter(Uri uri)
    {
        var query = uri.Query;
        if (query.Length <= 1)
        {
            return false;
        }

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equalsIndex >= 0 ? pair[..equalsIndex] : pair;
            var decodedName = DecodeQueryName(name);
            if (IsCredentialQueryName(decodedName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCredentialQueryName(string name)
        => name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("accessToken", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("password", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("pass", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("pwd", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("apiKey", StringComparison.OrdinalIgnoreCase);

    private static string DecodeQueryName(string name)
    {
        try
        {
            return Uri.UnescapeDataString(name.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return name;
        }
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

}
