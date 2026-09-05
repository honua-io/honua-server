// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>Credential-free deployment attestation for a referenced-output volume.</summary>
public sealed record GeoprocessingOutputStoreAttestation(
    string Provider,
    string StoreReference,
    string ConfigurationDigest,
    string PersistenceClass,
    string BackupIdentity)
{
    /// <summary>Deployment-provisioned marker stored and backed up at the volume root.</summary>
    public const string FileName = ".honua-gp-store.json";

    /// <summary>Creates evidence from a declared persistence and backup contract, never from a local path.</summary>
    public static GeoprocessingOutputStoreAttestation Create(GeoprocessingOutputStagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.Provider, GeoprocessingOutputStagingOptions.LocalProvider, StringComparison.OrdinalIgnoreCase)
            || options.PersistenceClass != "shared-persistent"
            || !IsIdentifier(options.StoreReference)
            || !IsIdentifier(options.BackupIdentity)
            || options.BackupStoreReferences is null
            || !options.BackupStoreReferences.All(IsIdentifier)
            || !options.BackupStoreReferences.Contains(options.StoreReference, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Output staging requires a shared-persistent contract and an opaque store reference included in the declared backup identity's store inventory.");
        }

        // Versioned, newline-delimited UTF-8. Paths are intentionally excluded: the
        // same volume may be mounted at different paths in server and worker hosts.
        var canonical = string.Join('\n',
            "honua-gp-store-v1", "local", options.StoreReference, options.PersistenceClass, options.BackupIdentity,
            string.Join(',', options.BackupStoreReferences.Order(StringComparer.Ordinal)), options.KeyPrefix,
            options.MaxInlineArtifactBytes.ToString(CultureInfo.InvariantCulture),
            options.ReadLeaseDuration.Ticks.ToString(CultureInfo.InvariantCulture),
            options.SweepInterval.Ticks.ToString(CultureInfo.InvariantCulture),
            options.SweepGrace.Ticks.ToString(CultureInfo.InvariantCulture),
            options.OrphanRetention.Ticks.ToString(CultureInfo.InvariantCulture));
        return new("local", options.StoreReference,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
            options.PersistenceClass, options.BackupIdentity!);
    }

    private static bool IsIdentifier(string? value)
        => value is not null && Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,159}\\z", RegexOptions.CultureInvariant);
}
