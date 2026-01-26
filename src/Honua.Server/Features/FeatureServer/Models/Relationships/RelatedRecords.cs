// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Related records result set
/// </summary>
public sealed class RelatedRecords
{
    /// <summary>
    /// Object ID field name
    /// </summary>
    public string ObjectIdFieldName { get; init; } = FieldNames.ObjectId;

    /// <summary>
    /// Global ID field name (if used)
    /// </summary>
    public string? GlobalIdFieldName { get; init; }

    /// <summary>
    /// Array of field definitions
    /// </summary>
    public GeoServicesFieldInfo[] Fields { get; init; } = [];

    /// <summary>
    /// Spatial reference system for geometries
    /// </summary>
    public GeoServicesSpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Array of related features
    /// </summary>
    public GeoServicesFeature[] Features { get; init; } = [];
}
