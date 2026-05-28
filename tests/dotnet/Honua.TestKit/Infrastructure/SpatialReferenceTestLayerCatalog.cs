// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Shared identifiers for the spatial-reference (non-4326 SRID) integration test fixture.
/// Tests seed the Metadata v2 graph with these ids/SRID; the constants keep the seeded
/// graph and the test assertions in sync.
/// </summary>
public static class SpatialReferenceTestLayerCatalog
{
    public const string ServiceId = "srid-test";
    public const int PointLayerId = 101;
    public const int LineLayerId = 102;
    public const int PolygonLayerId = 103;
    public const int LayerSrid = 3857;
}
