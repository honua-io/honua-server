// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Constants;

/// <summary>
/// Operation identifiers for test trait attributes.
/// Used with <see cref="Attributes.OperationAttribute"/> for test categorization.
/// </summary>
public static class Operations
{
    // Common Operations
    public const string Query = "Query";
    public const string GetById = "GetById";
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public const string BulkCreate = "BulkCreate";
    public const string BulkUpdate = "BulkUpdate";
    public const string BulkDelete = "BulkDelete";

    // Spatial Operations
    public const string SpatialQuery = "SpatialQuery";
    public const string BufferQuery = "BufferQuery";
    public const string NearestQuery = "NearestQuery";

    // Metadata Operations
    public const string GetMetadata = "GetMetadata";
    public const string GetLayerInfo = "GetLayerInfo";
    public const string GetServiceInfo = "GetServiceInfo";

    // Health Operations
    public const string LivenessCheck = "LivenessCheck";
    public const string ReadinessCheck = "ReadinessCheck";

    // Tile Operations
    public const string GetTile = "GetTile";
    public const string GetTileMetadata = "GetTileMetadata";

    // Filter Operations
    public const string WhereClause = "WhereClause";
    public const string CqlFilter = "CqlFilter";
    public const string ODataFilter = "ODataFilter";
}
