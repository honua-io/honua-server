// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.SpatialAnalytics.Models;

/// <summary>
/// Shared parameter-name constants for the spatial analytics endpoints. The same
/// names are accepted in query-string and POST body so REST and OGC callers have
/// identical semantics.
/// </summary>
internal static class SpatialAnalyticsParameters
{
    // Shared filter parameters (same shape as queryH3, queryBins, etc.)
    public const string Where = "where";
    public const string OutStatistics = "outStatistics";
    public const string Format = "f";

    // Cluster parameters
    public const string Algorithm = "algorithm";
    public const string Eps = "eps";
    public const string MinPoints = "minPoints";
    public const string K = "k";
    public const string DistanceUnit = "distanceUnit";
    public const string ReturnHullPerCluster = "returnHullPerCluster";

    // Spatial join parameters
    public const string JoinLayerId = "joinLayerId";
    public const string Predicate = "predicate";
    public const string Distance = "distance";
    public const string CarryFields = "carryFields";

    // Buffer aggregate parameters
    public const string BufferDistance = "distance";
    public const string BufferUnit = "unit";
    public const string Dissolve = "dissolve";
    public const string GroupByFields = "groupByFields";

    // Density parameters
    public const string Mode = "mode";
    public const string CellSize = "cellSize";
    public const string WeightField = "weightField";
}
