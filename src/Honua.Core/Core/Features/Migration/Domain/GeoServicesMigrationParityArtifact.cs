// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoServicesMigrationParityArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.arcgis-parity";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required string Classification { get; init; }

    public string[] Reasons { get; init; } = [];

    public GeoServicesMigrationParityResourceProbe[] ResourceProbes { get; init; } = [];
}

public static class GeoServicesMigrationParityClassifications
{
    public const string Pass = "pass";

    public const string Warn = "warn";

    // A probe outside the warn band blocks cutover until it is remediated.
    public const string Fail = "fail";
}

public sealed record GeoServicesMigrationParityResourceProbe
{
    public required string SourceResourceId { get; init; }

    public required string TargetResourceId { get; init; }

    public required string Classification { get; init; }

    public required GeoServicesMigrationParityFeatureCountProbe FeatureCount { get; init; }

    public required GeoServicesMigrationParitySchemaProbe Schema { get; init; }

    public required GeoServicesMigrationParityCoverageProbe IdentityCoverage { get; init; }

    public required GeoServicesMigrationParityCoverageProbe AttachmentCoverage { get; init; }

    public required GeoServicesMigrationParityCoverageProbe RelationshipCoverage { get; init; }
}

// Feature-count parity passes at or under 5 percent, warns through 20 percent, and fails above that; a missing source count warns.
public sealed record GeoServicesMigrationParityFeatureCountProbe
{
    public long? SourceCount { get; init; }

    public long TargetCount { get; init; }

    public long? Delta { get; init; }

    public double? DeltaRatio { get; init; }

    public required string Classification { get; init; }

    public string? Reason { get; init; }
}

// A missing expected field fails schema parity with no warn band, because the attribute would be lost.
public sealed record GeoServicesMigrationParitySchemaProbe
{
    public string[] ExpectedFieldNames { get; init; } = [];

    public string[] TargetFieldNames { get; init; } = [];

    public string[] MissingFieldNames { get; init; } = [];

    public required string Classification { get; init; }

    public string? Reason { get; init; }
}

public sealed record GeoServicesMigrationParityCoverageProbe
{
    public int Expected { get; init; }

    public int Observed { get; init; }

    public double Coverage { get; init; }

    public string[] MissingIds { get; init; } = [];

    public required string Classification { get; init; }

    public string? Reason { get; init; }
}
