// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Crs;

/// <summary>
/// Unit tests for <see cref="GeoParquetProjJsonCatalog"/>. Assert that the embedded,
/// pre-generated PROJJSON catalog resolves common output SRIDs to authoritative PROJJSON
/// (each carrying the matching EPSG <c>id</c>), normalizes Web Mercator aliases to
/// EPSG:3857, and reports unknown SRIDs as unresolvable so callers surface a precise error
/// (issue #2844).
/// </summary>
public sealed class GeoParquetProjJsonCatalogTests
{
    [UnitTest]
    [InlineData(3857)]
    [InlineData(25832)]
    [InlineData(27700)]
    [InlineData(32633)]
    [InlineData(2193)]
    [InlineData(4269)]
    [Theory]
    public void TryGetProjJson_KnownSrid_ReturnsProjJsonWithMatchingEpsgId(int srid)
    {
        var found = GeoParquetProjJsonCatalog.TryGetProjJson(srid, out var projJson);

        found.Should().BeTrue();
        projJson.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(projJson!);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("name").GetString().Should().NotBeNullOrEmpty();
        var id = root.GetProperty("id");
        id.GetProperty("authority").GetString().Should().Be("EPSG");
        id.GetProperty("code").GetInt32().Should().Be(srid);
    }

    [UnitTest]
    [InlineData(102100)]
    [InlineData(900913)]
    [InlineData(102113)]
    [InlineData(3785)]
    [Theory]
    public void TryGetProjJson_WebMercatorAlias_ResolvesToEpsg3857(int aliasSrid)
    {
        var found = GeoParquetProjJsonCatalog.TryGetProjJson(aliasSrid, out var projJson);

        found.Should().BeTrue();
        using var doc = JsonDocument.Parse(projJson!);
        doc.RootElement.GetProperty("id").GetProperty("code").GetInt32().Should().Be(3857);
    }

    [UnitTest]
    public void TryGetProjJson_UnknownSrid_ReturnsFalse()
    {
        GeoParquetProjJsonCatalog.TryGetProjJson(987654, out var projJson).Should().BeFalse();
        projJson.Should().BeNull();
    }

    [UnitTest]
    public void IsSupported_ReflectsCatalogMembership()
    {
        GeoParquetProjJsonCatalog.IsSupported(3857).Should().BeTrue();
        GeoParquetProjJsonCatalog.IsSupported(987654).Should().BeFalse();
    }

    [UnitTest]
    public void SupportedSrids_ContainsCuratedCommonCrs_AndAllEntriesAreValidProjJson()
    {
        var srids = GeoParquetProjJsonCatalog.SupportedSrids;

        srids.Should().Contain(new[] { 3857, 25832, 27700, 32601, 32760 });

        // Every catalog entry must parse and expose its own EPSG id so it round-trips in readers.
        foreach (var srid in srids)
        {
            GeoParquetProjJsonCatalog.TryGetProjJson(srid, out var projJson).Should().BeTrue();
            using var doc = JsonDocument.Parse(projJson!);
            doc.RootElement.GetProperty("id").GetProperty("code").GetInt32().Should().Be(srid);
        }
    }
}
