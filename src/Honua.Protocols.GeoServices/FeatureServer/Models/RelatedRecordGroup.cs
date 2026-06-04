// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Related records grouped by source object ID.
/// Per the Esri queryRelatedRecords contract, <c>relatedRecords</c> is a flat
/// array of records (each with <c>attributes</c> and optional <c>geometry</c>);
/// the field/geometry metadata is carried at the response top level.
/// </summary>
public sealed class RelatedRecordGroup
{
    /// <summary>
    /// Object ID of the source feature
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Flat array of related records for this source feature. Omitted when the
    /// source feature has no related records.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoServicesFeature[]? RelatedRecords { get; init; }

    /// <summary>
    /// Count of related records for this source feature. Populated only for
    /// <c>returnCountOnly=true</c> queries (Esri queryRelatedRecords count-only
    /// mode), in which case <see cref="RelatedRecords"/> is omitted.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; init; }
}
