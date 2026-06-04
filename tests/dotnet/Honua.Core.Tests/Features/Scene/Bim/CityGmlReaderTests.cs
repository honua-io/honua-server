// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Bim;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Bim;

/// <summary>
/// Unit tests for the pure-managed CityGML building reader (#1207).
/// </summary>
public sealed class CityGmlReaderTests
{
    [UnitTest]
    public void Read_SingleBuilding_ParsesHierarchyAndAttributes()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());

        model.SourceCrs.Should().Be("urn:ogc:def:crs:EPSG::4326");
        model.Buildings.Should().HaveCount(1);

        var building = model.Buildings[0];
        building.Id.Should().Be("BLDG_1");
        building.Name.Should().Be("Test Tower");
        building.StoreysAboveGround.Should().Be(3);
        building.Attributes.Should().ContainKey("usage").WhoseValue.Should().Be("office");

        building.Storeys.Should().HaveCount(1);
        var storey = building.Storeys[0];
        storey.Surfaces.Should().HaveCount(4);
        storey.Surfaces.Select(s => s.SurfaceType).Should()
            .ContainInOrder("GroundSurface", "RoofSurface", "WallSurface", "WallSurface");
    }

    [UnitTest]
    public void Read_GroundSurface_MapsToStructuralDiscipline()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());
        var surfaces = model.Buildings[0].Storeys[0].Surfaces;

        var ground = surfaces.Single(s => s.SurfaceType == "GroundSurface");
        ground.Discipline.Should().Be("Structural");

        var walls = surfaces.Where(s => s.SurfaceType == "WallSurface");
        walls.Should().OnlyContain(s => s.Discipline == "Architectural");
    }

    [UnitTest]
    public void Read_SurfaceLevelGenericAttribute_IsParsed()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());
        var roof = model.Buildings[0].Storeys[0].Surfaces.Single(s => s.SurfaceType == "RoofSurface");

        roof.Attributes.Should().ContainKey("material").WhoseValue.Should().Be("concrete");
    }

    [UnitTest]
    public void Read_PosListRing_DecodesEveryVertex()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());
        var ground = model.Buildings[0].Storeys[0].Surfaces.Single(s => s.SurfaceType == "GroundSurface");

        // The closed ring has 5 posList tuples; the reader keeps them verbatim.
        ground.Ring.Should().HaveCount(5);
        ground.Ring[0].Should().Be(new CityGmlVertex(0.0, 0.0, 0.0));
        ground.Ring[2].Should().Be(new CityGmlVertex(0.001, 0.001, 0.0));
    }

    [UnitTest]
    public void Read_GmlPosEncoding_ParsesIndividualPositions()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.GmlPosEncoding());
        var ground = model.Buildings[0].Storeys[0].Surfaces.Single();

        ground.Ring.Should().HaveCount(4);
        ground.Ring[1].Should().Be(new CityGmlVertex(0.002, 0.0, 0.0));
    }

    [UnitTest]
    public void Read_NoBuildings_Throws()
    {
        var act = () => CityGmlReader.Read(CityGmlFixtures.NoBuildings());

        act.Should().Throw<CityGmlFormatException>()
            .Which.Code.Should().Be("SCENE_MODEL_ASSET_INVALID");
    }

    [UnitTest]
    public void Read_MalformedXml_Throws()
    {
        var act = () => CityGmlReader.Read("<not-closed"u8.ToArray());

        act.Should().Throw<CityGmlFormatException>()
            .Which.Code.Should().Be("SCENE_MODEL_ASSET_INVALID");
    }
}
