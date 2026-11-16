// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed class GeoprocessingOutputStagingOptions
{
    public const string SectionName = "Geoprocessing:OutputStaging";

    public const string LocalProvider = "local";

    // Outputs go to the host's configured file storage (FileStorage:Provider), which every instance shares.
    public const string ObjectStorageProvider = "object-storage";

    public bool Enabled { get; set; }

    // Any value other than LocalProvider or ObjectStorageProvider fails registration closed.
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; set; } = LocalProvider;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$",
        ErrorMessage = "StoreReference must be an opaque identifier")]
    public string StoreReference { get; set; } = "gp-outputs";

    public string? LocalRootPath { get; set; }

    public string? PersistenceClass { get; set; }

    public string? BackupIdentity { get; set; }

    public string[] BackupStoreReferences { get; set; } = [];

    public string? ConfigurationDigest { get; set; }

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(?!.*\\.\\.)[A-Za-z0-9][A-Za-z0-9._/-]{0,158}$",
        ErrorMessage = "KeyPrefix must be a bounded relative key prefix without traversal segments")]
    public string KeyPrefix { get; set; } = "gp/outputs";

    [Range(1024, RasterOutputContract.MaximumInlinePayloadBytes,
        ErrorMessage = "MaxInlineArtifactBytes must be between 1 KiB and the 8 MiB contract ceiling")]
    public int MaxInlineArtifactBytes { get; set; } = 4 * 1024 * 1024;

    public TimeSpan ReadLeaseDuration { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan SweepGrace { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan OrphanRetention { get; set; } = TimeSpan.FromDays(7);
}
