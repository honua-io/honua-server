// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoServerServiceInfo
{
    public required string GeoServerRestUrl { get; init; }

    public string? Version { get; init; }

    public string? BuildTimestamp { get; init; }

    public string? GitRevision { get; init; }

    public GeoServerGlobalSettings? GlobalSettings { get; init; }

    public GeoServerWorkspaceInfo[] Workspaces { get; init; } = [];

    public GeoServerDataStoreInfo[] DataStores { get; init; } = [];

    public GeoServerCoverageStoreInfo[] CoverageStores { get; init; } = [];

    public GeoServerLayerInfo[] Layers { get; init; } = [];

    public GeoServerLayerGroupInfo[] LayerGroups { get; init; } = [];

    public GeoServerStyleInfo[] Styles { get; init; } = [];

    public GeoServerServiceEndpointInfo[] ServiceEndpoints { get; init; } = [];

    public GeoServerMigrationCompatibility? CompatibilityAssessment { get; init; }

    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record GeoServerWorkspaceInfo
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool IsDefault { get; init; }

    public bool IsIsolated { get; init; }

    public DateTimeOffset? DateCreated { get; init; }

    public DateTimeOffset? DateModified { get; init; }
}

public sealed record GeoServerDataStoreInfo
{
    public required string Name { get; init; }

    public required string WorkspaceName { get; init; }

    public string? Description { get; init; }

    public required string Type { get; init; }

    public IReadOnlyDictionary<string, object> ConnectionParameters { get; init; } = new Dictionary<string, object>();

    public bool Enabled { get; init; } = true;

    public bool IsDefault { get; init; }

    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

public sealed record GeoServerCoverageStoreInfo
{
    public required string Name { get; init; }

    public required string WorkspaceName { get; init; }

    public string? Description { get; init; }

    public required string Type { get; init; }

    public IReadOnlyDictionary<string, object> ConnectionParameters { get; init; } = new Dictionary<string, object>();

    public bool Enabled { get; init; } = true;

    public bool IsDefault { get; init; }

    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

public sealed record GeoServerLayerInfo
{
    public required string Name { get; init; }

    public required string WorkspaceName { get; init; }

    public string? Title { get; init; }

    public string? Abstract { get; init; }

    public string? DefaultStyle { get; init; }

    public string[] AlternativeStyles { get; init; } = [];

    public string? DataStoreName { get; init; }

    public string? CoverageStoreName { get; init; }

    public string? NativeName { get; init; }

    public string? SRS { get; init; }

    public string? NativeCRS { get; init; }

    public GeoServerBoundingBox? LatLonBoundingBox { get; init; }

    public GeoServerBoundingBox? NativeBoundingBox { get; init; }

    public bool Enabled { get; init; } = true;

    public bool Queryable { get; init; } = true;

    public bool Opaque { get; init; }

    public string[] Keywords { get; init; } = [];

    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

public sealed record GeoServerLayerGroupInfo
{
    public required string Name { get; init; }

    public required string WorkspaceName { get; init; }

    public string? Title { get; init; }

    public string? Abstract { get; init; }

    public string? Mode { get; init; }

    public GeoServerLayerGroupEntry[] Layers { get; init; } = [];

    public GeoServerLayerGroupEntry[] Styles { get; init; } = [];

    public GeoServerBoundingBox? Bounds { get; init; }

    public bool Enabled { get; init; } = true;

    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

public sealed record GeoServerLayerGroupEntry
{
    public required string Name { get; init; }

    public string? WorkspaceName { get; init; }

    public required string Type { get; init; }
}

public sealed record GeoServerStyleInfo
{
    public required string Name { get; init; }

    public string? WorkspaceName { get; init; }

    public string? Filename { get; init; }

    public string Format { get; init; } = "sld";

    public string? LanguageVersion { get; init; }

    public string? SldContent { get; init; }

    public DateTimeOffset? DateCreated { get; init; }

    public DateTimeOffset? DateModified { get; init; }

    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

public sealed record GeoServerServiceEndpointInfo
{
    public required string Protocol { get; init; }

    public required string Url { get; init; }

    public bool? Enabled { get; init; }

    public string[] Capabilities { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record GeoServerBoundingBox
{
    public double MinX { get; init; }

    public double MinY { get; init; }

    public double MaxX { get; init; }

    public double MaxY { get; init; }

    public string? CRS { get; init; }
}

public sealed record GeoServerGlobalSettings
{
    public GeoServerContactInfo? ContactInfo { get; init; }

    public string? Title { get; init; }

    public string? Abstract { get; init; }

    public string? OnlineResource { get; init; }

    public string? SchemaBaseUrl { get; init; }

    public string? Charset { get; init; }

    public string? ProxyBaseUrl { get; init; }

    public int? NumDecimals { get; init; }

    public bool? VerboseMessages { get; init; }

    public bool? VerboseExceptions { get; init; }
}

public sealed record GeoServerContactInfo
{
    public string? ContactPerson { get; init; }

    public string? ContactOrganization { get; init; }

    public string? ContactPosition { get; init; }

    public string? AddressType { get; init; }

    public string? Address { get; init; }

    public string? AddressCity { get; init; }

    public string? AddressState { get; init; }

    public string? AddressPostalCode { get; init; }

    public string? AddressCountry { get; init; }

    public string? ContactVoice { get; init; }

    public string? ContactFacsimile { get; init; }

    public string? ContactEmail { get; init; }
}

public sealed record GeoServerMigrationCompatibility
{
    public int FullyCompatibleResources { get; init; }

    public int PartiallyCompatibleResources { get; init; }

    public int IncompatibleResources { get; init; }

    public double OverallCompatibilityPercentage => FullyCompatibleResources + PartiallyCompatibleResources + IncompatibleResources > 0
        ? (double)(FullyCompatibleResources + PartiallyCompatibleResources) / (FullyCompatibleResources + PartiallyCompatibleResources + IncompatibleResources) * 100
        : 0;

    public GeoServerResourceTypeCompatibility[] ResourceTypeBreakdown { get; init; } = [];

    public string[] UnsupportedFeatures { get; init; } = [];

    public string[] RequiredManualSteps { get; init; } = [];

    public string[] Warnings { get; init; } = [];
}

public sealed record GeoServerResourceCompatibility
{
    public required GeoServerCompatibilityLevel CompatibilityLevel { get; init; }

    public required string Reason { get; init; }

    public string[] RequiredManualSteps { get; init; } = [];

    public string[] Warnings { get; init; } = [];

    public string? Code { get; init; }

    public bool CanMigrateAutomatically => CompatibilityLevel != GeoServerCompatibilityLevel.Incompatible;
}

public sealed record GeoServerResourceTypeCompatibility
{
    public required string ResourceType { get; init; }

    public int FullyCompatible { get; init; }

    public int PartiallyCompatible { get; init; }

    public int Incompatible { get; init; }

    public int Total => FullyCompatible + PartiallyCompatible + Incompatible;

    public double CompatibilityPercentage => Total > 0
        ? (double)(FullyCompatible + PartiallyCompatible) / Total * 100
        : 0;
}

public enum GeoServerCompatibilityLevel
{
    FullyCompatible,

    PartiallyCompatible,

    Incompatible
}
