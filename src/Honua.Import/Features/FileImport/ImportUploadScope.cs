// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Import.FileImport;

/// <summary>
/// Namespaces untrusted upload correlation IDs by authenticated owner and tenant.
/// This is an observation/cancellation boundary, not an import acceptance or deduplication receipt.
/// </summary>
internal sealed class ImportUploadScope
{
    private readonly string _prefix;

    internal ImportUploadScope(ImportOperationOwnership ownership)
    {
        var tenant = ownership.TenantId is { } value ? $"{value.Length}:{value}" : "none";
        var binding = $"{ownership.OwnerActorId.Length}:{ownership.OwnerActorId}:{tenant}";
        // Slash is forbidden in caller-supplied IDs, including on pre-scope servers.
        // A legacy raw upload ID therefore cannot masquerade as a scoped record.
        _prefix = $"honua-import/v1/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding)))}/";
    }

    internal static ImportUploadScope? Capture(HttpContext context) =>
        ImportOperationAuthorization.Capture(context) is { } ownership ? new(ownership) : null;

    internal static bool IsClientId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 128 &&
        id.All(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');

    internal string StorageId(string clientId) => _prefix + clientId;

    internal UploadProgress? Project(UploadProgress? progress)
    {
        if (progress is null || !progress.UploadId.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var clientId = progress.UploadId[_prefix.Length..];
        return IsClientId(clientId) ? progress with { UploadId = clientId } : null;
    }

    internal UploadProgress[] Project(IEnumerable<UploadProgress> uploads) =>
        uploads.Select(Project).OfType<UploadProgress>().ToArray();
}
