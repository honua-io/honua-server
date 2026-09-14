// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Contract coverage for replica downloads (#4027) and the synchronize upload fingerprint (#4031) below the
/// HTTP surface. The endpoint tests run on the in-memory feature store, which ignores
/// <see cref="FeatureQuery.IncludeZ"/>, <see cref="FeatureQuery.IncludeM"/> and
/// <see cref="FeatureQuery.OutputSrid"/>; these cases pin what the handler asks a real provider for.
/// The Postgres half (plain WKB unless Z/M is requested) is FeatureQueryBuilderZMTests.
/// </summary>
public sealed class FeatureServerReplicaDownloadContractTests
{
    private const string Edits = """[{"id":0,"adds":[{"attributes":{"name":"a"}}]}]""";

    [UnitTest]
    public void ReplicaGeometryQuery_SpatialLayer_RequestsZMFromStorageInTheAdvertisedCrs()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "parcels" },
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Polygon,
                SpatialReference = new MetadataV2SpatialReference { Srid = 2193 },
                StorageCrs = new MetadataV2SpatialReference { Srid = 4326 }
            }
        };

        var query = FeatureServerEndpoints.ReplicaGeometryQuery(new FeatureQuery { Limit = 10 }, resource);

        query.IncludeZ.Should().BeTrue("the provider's plain WKB read strips Z unless it is requested");
        query.IncludeM.Should().BeTrue("the provider's plain WKB read strips M unless it is requested");
        query.OutputSrid.Should().Be(2193, "storage-CRS coordinates must be reprojected to the CRS the download is labelled with");
        query.Limit.Should().Be(10);
        FeatureServerEndpoints.CreateReplicaLayerSpatialReference(resource)!.Wkid.Should().Be(2193);
    }

    [UnitTest]
    public void ReplicaGeometryQuery_AttributeOnlyTable_RequestsNoReprojectionAndCarriesNoSpatialReference()
    {
        var table = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "inspections" },
            Type = MetadataV2ResourceType.Table
        };

        FeatureServerEndpoints.ReplicaGeometryQuery(new FeatureQuery(), table).OutputSrid.Should().BeNull();
        FeatureServerEndpoints.ResolveReplicaLayerSrid(table).Should().BeNull();
        FeatureServerEndpoints.CreateReplicaLayerSpatialReference(table).Should().BeNull(
            "a table has no geometry, so a WGS 84 label would claim a CRS it does not have");
    }

    [UnitTest]
    public void ComputeReplicaUploadFingerprint_OmittedRollbackOnFailure_KeepsThePreDefaultChangeFingerprint()
    {
        // The expected values hash the material layout by hand, independent of the implementation.
        static string Material(string mode)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"upload\n{mode}\nlastWriteWins\n{Edits}")));

        FeatureServerEndpoints.ComputeReplicaUploadFingerprint("upload", rollbackOnFailure: true, rollbackOnFailureSupplied: false, lastWriteWins: true, Edits)
            .Should().Be(Material("best-effort"), "a retry spanning the #4031 default change must find its first attempt's record");
        FeatureServerEndpoints.ComputeReplicaUploadFingerprint("upload", rollbackOnFailure: false, rollbackOnFailureSupplied: true, lastWriteWins: true, Edits)
            .Should().Be(Material("best-effort"));
        FeatureServerEndpoints.ComputeReplicaUploadFingerprint("upload", rollbackOnFailure: true, rollbackOnFailureSupplied: true, lastWriteWins: true, Edits)
            .Should().Be(Material("rollback"));
    }

    [UnitTest]
    public void ConvertWkb_ZMLineWithAMissingZ_KeepsPositionalSlotsAndRoundTripsTheMeasure()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var line = factory.CreateLineString([new CoordinateZM(1, 2, 3, 4), new CoordinateZM(5, 6, double.NaN, 8)]);
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true).Write(line);

        var geometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(wkb, srid: 4326, geometryLimits: null, includeZ: true, includeM: true)!;
        var json = JsonSerializer.Serialize(geometry, FeatureServerJsonContext.Default.GeoServicesGeometry);

        using var document = JsonDocument.Parse(json);
        var path = document.RootElement.GetProperty("paths")[0];
        path[0].EnumerateArray().Select(value => value.GetDouble()).Should().Equal([1d, 2d, 3d, 4d], json);
        path[1].GetArrayLength().Should().Be(4, "the missing Z keeps its slot so the measure is not read as elevation: {0}", json);
        path[1][2].ValueKind.Should().Be(JsonValueKind.Null, json);
        path[1][3].GetDouble().Should().Be(8, json);

        var parsed = JsonSerializer.Deserialize(json, FeatureServerJsonContext.Default.GeoServicesGeometry)!;
        var roundTrip = (LineString)new WKBReader().Read(GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(parsed));
        roundTrip.CoordinateSequence.GetOrdinate(0, Ordinate.Z).Should().Be(3);
        roundTrip.CoordinateSequence.GetOrdinate(0, Ordinate.M).Should().Be(4);
        double.IsNaN(roundTrip.CoordinateSequence.GetOrdinate(1, Ordinate.Z)).Should().BeTrue();
        roundTrip.CoordinateSequence.GetOrdinate(1, Ordinate.M).Should().Be(8);
    }

    [UnitTest]
    public void ConvertWkb_TwoDimensionalLine_EmitsOnlyXYSlots()
    {
        var line = new GeometryFactory().CreateLineString([new Coordinate(1, 2), new Coordinate(3, 4)]);
        var wkb = new WKBWriter().Write(line);

        var geometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(wkb, srid: 4326, geometryLimits: null, includeZ: true, includeM: true)!;

        geometry.HasZ.Should().BeFalse();
        geometry.HasM.Should().BeFalse();
        var path = geometry.Paths![0];
        path.Should().HaveCount(2);
        path[0].Should().Equal(1d, 2d);
        path[1].Should().Equal(3d, 4d);
    }

    [UnitTest]
    public void ToPublicObjectIds_CustomPrimaryId_ProjectsTheChangeLogPublicIdAndKeepsUnmappedStorageIds()
    {
        // A layer whose id.primary differs from the storage identity: clients hold the public id (#4017).
        var changes = new[]
        {
            Change(objectId: 11, publicObjectId: 9001, FeatureChangeOperation.Insert),
            Change(objectId: 12, publicObjectId: 9002, FeatureChangeOperation.Delete),
            Change(objectId: 13, publicObjectId: null, FeatureChangeOperation.Update)
        };

        FeatureServerEndpoints.ToPublicObjectIds(changes, [11, 12, 13]).Should().Equal(9001, 9002, 13);
        FeatureServerEndpoints.ToPublicObjectIds(null, [11]).Should().Equal(11);
    }

    [UnitTest]
    public void TryFindNarrowerReplicaWindow_CountsOnlyDeliverableGenerationsPerLayer()
    {
        // Layer A has 3 deliverable changes against a limit of 2: the window ends at its second generation.
        FeatureServerEndpoints.TryFindNarrowerReplicaWindow([[40, 10, 30], [5]], 2, 50, out var narrowed).Should().BeTrue();
        narrowed.Should().Be(30);

        // Every layer fits (a large unrelated history is filtered out before this call): no narrowing.
        FeatureServerEndpoints.TryFindNarrowerReplicaWindow([[10, 20], [5]], 2, 50, out var unchanged).Should().BeFalse();
        unchanged.Should().Be(50);

        // The limit falls inside the first generation, which cannot be split.
        FeatureServerEndpoints.TryFindNarrowerReplicaWindow([[50, 50, 50]], 2, 50, out _).Should().BeFalse();
    }

    private static FeatureChange Change(long objectId, long? publicObjectId, FeatureChangeOperation operation)
        => new()
        {
            ChangeId = objectId,
            Generation = objectId,
            LayerId = 1,
            ObjectId = objectId,
            PublicObjectId = publicObjectId,
            Operation = operation,
            ChangedAt = DateTimeOffset.UnixEpoch
        };
}
