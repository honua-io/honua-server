// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Constants and helpers for service protocol identifiers.
/// </summary>
public static class ServiceProtocols
{
    /// <summary>GeoServices FeatureServer protocol.</summary>
    public const string FeatureServer = "FeatureServer";

    /// <summary>GeoServices MapServer protocol.</summary>
    public const string MapServer = "MapServer";

    /// <summary>OGC API Features protocol.</summary>
    public const string OgcFeatures = "OgcFeatures";

    /// <summary>OData v4 protocol.</summary>
    public const string OData = "OData";

    /// <summary>
    /// All supported protocol identifiers.
    /// </summary>
    public static readonly string[] All = [FeatureServer, MapServer, OgcFeatures, OData];

    /// <summary>
    /// Checks whether a protocol is enabled for a service based on its metadata.
    /// When <see cref="CatalogMetadata.EnabledProtocols"/> is null, all protocols are enabled.
    /// </summary>
    public static bool IsProtocolEnabled(CatalogMetadata? metadata, string protocol)
        => metadata?.EnabledProtocols is null || metadata.EnabledProtocols.Contains(protocol);
}
