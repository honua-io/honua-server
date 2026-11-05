// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class FeatureServerResponse
{
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent GeoServices REST server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoGeoServicesServerVersionTests).

    public required string ServiceName { get; init; }

    public required string ServiceDescription { get; init; }

    public string? CopyrightText { get; init; }

    public string? License { get; init; }

    public string? Publisher { get; init; }

    public GeoServicesGovernanceLink[]? Links { get; init; }

    public required LayerInfo[] Layers { get; init; }

    public object[] Tables { get; init; } = [];

    public required SpatialReferenceInfo SpatialReference { get; init; }

    public ExtentInfo? InitialExtent { get; init; }

    public ExtentInfo? FullExtent { get; init; }

    public string Units { get; init; } = "esriMeters";

    // This value is a comma-delimited string because the ArcGIS Maps SDK for JavaScript calls split on supportedQueryFormats.
    [System.Text.Json.Serialization.JsonConverter(typeof(CommaDelimitedStringArrayConverter))]
    public string[] SupportedQueryFormats { get; init; } = ["JSON", "GeoJSON"];

    public string Capabilities { get; init; } = "Query,Extract";

    public int MaxRecordCount { get; init; } = 1000;

    public bool SupportsAdvancedQueries { get; init; } = true;

    public bool SupportsStatistics { get; init; } = true;

    public bool HasGeometryProperties { get; init; } = true;

    public string ObjectIdField { get; init; } = FieldNames.ObjectId;

    public string? GlobalIdField { get; init; }

    public string? TypeIdField { get; init; }

    public GeoServicesFieldInfo[] Fields { get; init; } = [];

    public ServiceRelationshipSummary[] Relationships { get; init; } = [];

    public bool SupportsRelationshipsResource { get; init; }

    public bool AllowGeometryUpdates { get; init; } = true;

    public bool SyncEnabled { get; init; }

    public FeatureServerSyncCapabilities? SyncCapabilities { get; init; }

    public bool HasVersionedData { get; init; }

    public bool HasBranchVersionedData => HasVersionedData;

    public bool IsDataVersioned { get; init; }

    public bool SupportsBranchVersioning { get; init; }

    public string? VersionManagementServerUrl { get; init; }
}
