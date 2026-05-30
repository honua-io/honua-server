// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Related records grouped by source object ID
/// </summary>
public sealed class RelatedRecordGroup
{
    /// <summary>
    /// Object ID of the source feature
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Related records for this source feature (null if no related records)
    /// </summary>
    public RelatedRecords? RelatedRecords { get; init; }
}
