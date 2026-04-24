// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response model for queryRelatedRecords endpoint
/// </summary>
public sealed class QueryRelatedRecordsResponse
{
    /// <summary>
    /// Array of related record groups, one per source object ID
    /// </summary>
    public required RelatedRecordGroup[] RelatedRecordGroups { get; init; }
}
