// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Feature-store capabilities advertised by a provider implementation.
/// </summary>
public sealed record FeatureProviderCapabilities
{
    /// <summary>
    /// Whether the provider supports feature query/read operations.
    /// </summary>
    public bool SupportsQuery { get; init; } = true;

    /// <summary>
    /// Whether the provider supports count queries.
    /// </summary>
    public bool SupportsCount { get; init; } = true;

    /// <summary>
    /// Whether the provider supports extent queries.
    /// </summary>
    public bool SupportsExtent { get; init; } = true;

    /// <summary>
    /// Whether the provider supports aggregate statistics.
    /// </summary>
    public bool SupportsStatistics { get; init; } = true;

    /// <summary>
    /// Edit capabilities for this provider.
    /// </summary>
    public FeatureProviderEditCapabilities Edits { get; init; } = FeatureProviderEditCapabilities.ReadOnly;

    /// <summary>
    /// Output capabilities for this provider.
    /// </summary>
    public FeatureProviderOutputCapabilities Outputs { get; init; } = FeatureProviderOutputCapabilities.FormattedFallback;

    /// <summary>
    /// Capabilities for a read-write PostGIS-style provider.
    /// </summary>
    public static FeatureProviderCapabilities ReadWritePostgis { get; } = new()
    {
        Edits = FeatureProviderEditCapabilities.ReadWrite,
        Outputs = new FeatureProviderOutputCapabilities
        {
            SupportsNativeMvt = true,
            SupportsNativeFlatGeobuf = true,
            SupportsNativeGeobuf = true,
            SupportsNativeGml = true,
            SupportsStreamingGeoJson = true
        }
    };

    /// <summary>
    /// Capabilities for a read-only analytical provider.
    /// </summary>
    public static FeatureProviderCapabilities ReadOnlyAnalytical { get; } = new()
    {
        Edits = FeatureProviderEditCapabilities.ReadOnly,
        Outputs = FeatureProviderOutputCapabilities.FormattedFallback
    };

    /// <summary>
    /// Capabilities for the read-only MySQL/MariaDB provider thin slice.
    /// Query, count, and extent are supported. Statistics, native MVT, FlatGeobuf,
    /// Geobuf, GML, streaming GeoJSON, and KNN/nearest-neighbor are intentionally
    /// disabled because they are out of scope for this initial slice.
    /// </summary>
    public static FeatureProviderCapabilities ReadOnlyMySql { get; } = new()
    {
        SupportsQuery = true,
        SupportsCount = true,
        SupportsExtent = true,
        SupportsStatistics = false,
        Edits = FeatureProviderEditCapabilities.ReadOnly,
        Outputs = new FeatureProviderOutputCapabilities
        {
            SupportsNativeMvt = false,
            SupportsNativeFlatGeobuf = false,
            SupportsNativeGeobuf = false,
            SupportsNativeGml = false,
            SupportsStreamingGeoJson = false
        }
    };
}

/// <summary>
/// Edit capabilities advertised by a feature-store provider.
/// </summary>
public sealed record FeatureProviderEditCapabilities
{
    /// <summary>
    /// Whether the provider can create features.
    /// </summary>
    public bool SupportsCreate { get; init; }

    /// <summary>
    /// Whether the provider can update features.
    /// </summary>
    public bool SupportsUpdate { get; init; }

    /// <summary>
    /// Whether the provider can delete features.
    /// </summary>
    public bool SupportsDelete { get; init; }

    /// <summary>
    /// Whether the provider can apply multiple edits transactionally.
    /// </summary>
    public bool SupportsTransactions { get; init; }

    /// <summary>
    /// Read-only edit capability declaration.
    /// </summary>
    public static FeatureProviderEditCapabilities ReadOnly { get; } = new();

    /// <summary>
    /// Read-write edit capability declaration.
    /// </summary>
    public static FeatureProviderEditCapabilities ReadWrite { get; } = new()
    {
        SupportsCreate = true,
        SupportsUpdate = true,
        SupportsDelete = true,
        SupportsTransactions = true
    };
}

/// <summary>
/// Output capabilities advertised by a feature-store provider.
/// </summary>
public sealed record FeatureProviderOutputCapabilities
{
    /// <summary>
    /// Whether the provider can emit native Mapbox Vector Tile payloads.
    /// </summary>
    public bool SupportsNativeMvt { get; init; }

    /// <summary>
    /// Whether the provider can emit native FlatGeobuf payloads.
    /// </summary>
    public bool SupportsNativeFlatGeobuf { get; init; }

    /// <summary>
    /// Whether the provider can emit native Geobuf payloads.
    /// </summary>
    public bool SupportsNativeGeobuf { get; init; }

    /// <summary>
    /// Whether the provider can emit native GML payloads.
    /// </summary>
    public bool SupportsNativeGml { get; init; }

    /// <summary>
    /// Whether the provider can stream GeoJSON rows without full buffering.
    /// </summary>
    public bool SupportsStreamingGeoJson { get; init; }

    /// <summary>
    /// Output declaration for providers that rely on shared formatters.
    /// </summary>
    public static FeatureProviderOutputCapabilities FormattedFallback { get; } = new()
    {
        SupportsStreamingGeoJson = true
    };
}
