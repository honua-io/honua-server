// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public sealed record FeatureProviderCapabilities
{
    public bool SupportsQuery { get; init; } = true;

    public bool SupportsCount { get; init; } = true;

    public bool SupportsExtent { get; init; } = true;

    public bool SupportsStatistics { get; init; } = true;

    public FeatureProviderEditCapabilities Edits { get; init; } = FeatureProviderEditCapabilities.ReadOnly;

    public FeatureProviderOutputCapabilities Outputs { get; init; } = FeatureProviderOutputCapabilities.FormattedFallback;

    // A provider that does not advertise request-scoped read policy refuses layers that policy applies to.
    public bool SupportsReadPolicyEnforcement { get; init; }

    public static FeatureProviderCapabilities ReadWritePostgis { get; } = new()
    {
        SupportsReadPolicyEnforcement = true,
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

    public static FeatureProviderCapabilities ReadOnlyAnalytical { get; } = new()
    {
        Edits = FeatureProviderEditCapabilities.ReadOnly,
        Outputs = FeatureProviderOutputCapabilities.FormattedFallback
    };

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

public sealed record FeatureProviderEditCapabilities
{
    public bool SupportsCreate { get; init; }

    public bool SupportsUpdate { get; init; }

    public bool SupportsDelete { get; init; }

    public bool SupportsTransactions { get; init; }

    public static FeatureProviderEditCapabilities ReadOnly { get; } = new();

    public static FeatureProviderEditCapabilities ReadWrite { get; } = new()
    {
        SupportsCreate = true,
        SupportsUpdate = true,
        SupportsDelete = true,
        SupportsTransactions = true
    };
}

public sealed record FeatureProviderOutputCapabilities
{
    public bool SupportsNativeMvt { get; init; }

    public bool SupportsNativeFlatGeobuf { get; init; }

    public bool SupportsNativeGeobuf { get; init; }

    public bool SupportsNativeGml { get; init; }

    public bool SupportsStreamingGeoJson { get; init; }

    public static FeatureProviderOutputCapabilities FormattedFallback { get; } = new()
    {
        SupportsStreamingGeoJson = true
    };
}
