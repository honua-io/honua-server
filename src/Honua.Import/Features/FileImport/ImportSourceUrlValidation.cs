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

namespace Honua.Import.FileImport;

internal readonly record struct ImportSourceUrlValidationResult(bool IsValid, string? ErrorMessage)
{
    public static ImportSourceUrlValidationResult Success()
        => new(true, null);

    public static ImportSourceUrlValidationResult Failure(string message)
        => new(false, message);
}

internal static class ImportSourceUrlValidation
{
    internal const string InvalidSourceUrlMessage = "SourceUrl must be a valid HTTPS URL.";
    internal const string EmbeddedCredentialsMessage = "SourceUrl must not include embedded credentials.";
    internal const string DisallowedAddressMessage =
        "SourceUrl resolves to a private, loopback, or unresolvable network address, which is not allowed.";
    internal const string UnsupportedHostMessage =
        "SourceUrl must point to a supported public S3, Azure Blob, or Azure File host.";

    public static Task<ImportSourceUrlValidationResult> ValidateAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default)
        => ValidateAsync(sourceUrl, ResolveHostAddressesAsync, cancellationToken);

    internal static async Task<ImportSourceUrlValidationResult> ValidateAsync(
        string sourceUrl,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return ImportSourceUrlValidationResult.Failure(InvalidSourceUrlMessage);
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return ImportSourceUrlValidationResult.Failure(EmbeddedCredentialsMessage);
        }

        if (await NetworkAddressValidator.IsDisallowedAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return ImportSourceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        if (!IsSupportedPublicObjectHost(uri.Host))
        {
            return ImportSourceUrlValidationResult.Failure(UnsupportedHostMessage);
        }

        return ImportSourceUrlValidationResult.Success();
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    private static bool IsSupportedPublicObjectHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.ToLowerInvariant();

        if (host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal) ||
            host.EndsWith(".file.core.windows.net", StringComparison.Ordinal))
        {
            return true;
        }

        if (host == "s3.amazonaws.com" ||
            host.EndsWith(".s3.amazonaws.com", StringComparison.Ordinal) ||
            host.StartsWith("s3.", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal) ||
            host.StartsWith("s3-", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal) ||
            host.Contains(".s3.", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
