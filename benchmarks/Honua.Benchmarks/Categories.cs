// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Benchmarks;

/// <summary>
/// Shared category constants used by <c>[BenchmarkCategory(...)]</c> attributes
/// so callers can filter via <c>--anyCategories</c> / <c>--allCategories</c>.
/// </summary>
internal static class Categories
{
    public const string Tile = "tile";
    public const string Stac = "stac";
    public const string Ogc = "ogc";
    public const string Filter = "filter";
    public const string Serialization = "serialization";
    public const string Cache = "cache";
    public const string Raster = "raster";
    public const string Spec = "spec";
    public const string ConnectionPool = "connection-pool";
}
