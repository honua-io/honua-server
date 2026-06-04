// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response for querying feature attachments
/// </summary>
public sealed class AttachmentQueryResponse
{
    /// <summary>
    /// Attachment groups keyed by parent feature.
    /// </summary>
    public AttachmentGroup[] AttachmentGroups { get; init; } = [];

    /// <summary>
    /// Legacy flattened attachment list for single-feature queries.
    /// </summary>
    public AttachmentInfo[]? AttachmentInfos { get; init; }
}

/// <summary>
/// Group of attachments associated with a feature object ID.
/// </summary>
public sealed class AttachmentGroup
{
    /// <summary>
    /// Parent feature object ID.
    /// </summary>
    public required long ParentObjectId { get; init; }

    /// <summary>
    /// Parent feature global ID. Esri always emits this key on every attachment
    /// group; the ArcGIS API for Python <c>AttachmentManager.search()</c> reads
    /// <c>group['parentGlobalId']</c> unconditionally and raises
    /// <c>KeyError('parentGlobalId')</c> when it is absent. Honua attachments are
    /// keyed by integer object IDs and the layer has no global-id identity column,
    /// so this is emitted as an empty string per Esri convention rather than omitted.
    /// </summary>
    public string ParentGlobalId { get; init; } = string.Empty;

    /// <summary>
    /// Attachment infos associated with the parent feature.
    /// </summary>
    public AttachmentInfo[] AttachmentInfos { get; init; } = [];
}
