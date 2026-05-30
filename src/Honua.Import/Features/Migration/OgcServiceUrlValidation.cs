// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Server.Features.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Server.Features.Migration;

internal readonly record struct OgcServiceUrlValidationResult(bool IsValid, string? ErrorMessage)
{
    public static OgcServiceUrlValidationResult Success()
        => new(true, null);

    public static OgcServiceUrlValidationResult Failure(string message)
        => new(false, message);
}

internal static class OgcServiceUrlValidation
{
    internal const string InvalidHttpsUrlMessage = "SourceUrl must be a valid HTTPS URL.";
    internal const string InvalidHttpOrHttpsUrlMessage =
        "SourceUrl must be a valid HTTP or HTTPS URL when unsafe test OGC URLs are enabled.";
    internal const string EmbeddedCredentialsMessage = "SourceUrl must not include embedded credentials.";
    internal const string DisallowedAddressMessage =
        "SourceUrl resolves to a private, loopback, or unresolvable network address, which is not allowed.";

    public static Task<OgcServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        bool allowUnsafeLocalUrls,
        CancellationToken cancellationToken = default)
        => ValidateAsync(serviceUrl, allowUnsafeLocalUrls, ResolveHostAddressesAsync, cancellationToken);

    internal static async Task<OgcServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        bool allowUnsafeLocalUrls,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri))
        {
            return OgcServiceUrlValidationResult.Failure(
                allowUnsafeLocalUrls ? InvalidHttpOrHttpsUrlMessage : InvalidHttpsUrlMessage);
        }

        if (allowUnsafeLocalUrls)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return OgcServiceUrlValidationResult.Failure(InvalidHttpOrHttpsUrlMessage);
            }
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return OgcServiceUrlValidationResult.Failure(InvalidHttpsUrlMessage);
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return OgcServiceUrlValidationResult.Failure(EmbeddedCredentialsMessage);
        }

        if (allowUnsafeLocalUrls)
        {
            return OgcServiceUrlValidationResult.Success();
        }

        if (await NetworkAddressValidator.IsDisallowedAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return OgcServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return OgcServiceUrlValidationResult.Success();
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}
