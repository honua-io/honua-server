// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.TestKit.Helpers;

/// <summary>Provisions the deployment-owned contract on a test volume, never in the runtime store.</summary>
public static class GeoprocessingOutputStoreTestHelper
{
    public static GeoprocessingOutputStagingOptions Attest(GeoprocessingOutputStagingOptions options)
    {
        options.PersistenceClass = "shared-persistent";
        options.BackupIdentity = "qualification-backup";
        options.BackupStoreReferences = [options.StoreReference];
        var attestation = GeoprocessingOutputStoreAttestation.Create(options);
        options.ConfigurationDigest = attestation.ConfigurationDigest;
        File.WriteAllText(Path.Join(options.LocalRootPath!, GeoprocessingOutputStoreAttestation.FileName),
            JsonSerializer.Serialize(attestation));
        return options;
    }

    public static Dictionary<string, string?> Configuration(GeoprocessingOutputStagingOptions options)
        => new()
        {
            ["Geoprocessing:OutputStaging:Enabled"] = "true",
            ["Geoprocessing:OutputStaging:Provider"] = options.Provider,
            ["Geoprocessing:OutputStaging:LocalRootPath"] = options.LocalRootPath,
            ["Geoprocessing:OutputStaging:StoreReference"] = options.StoreReference,
            ["Geoprocessing:OutputStaging:PersistenceClass"] = options.PersistenceClass,
            ["Geoprocessing:OutputStaging:BackupIdentity"] = options.BackupIdentity,
            ["Geoprocessing:OutputStaging:BackupStoreReferences:0"] = options.StoreReference,
            ["Geoprocessing:OutputStaging:ConfigurationDigest"] = options.ConfigurationDigest,
            ["Geoprocessing:OutputStaging:MaxInlineArtifactBytes"] = options.MaxInlineArtifactBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
}
