// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.SharedModels;

/// <summary>
/// Exactness contract for <see cref="CrsIdentifier"/> (honua-server#3053): the
/// recognized spellings of a CRS all resolve to the same code, and identifiers
/// that merely LOOK like one — <c>EPSG:43260</c>, <c>NOT_CRS84</c> — resolve to
/// nothing. Callers gate payload acceptance on this, so a substring-ish match
/// here is a validation bypass there.
/// </summary>
public sealed class CrsIdentifierTests
{
    // The four spellings named in the imagery process contract, plus the aliases
    // and version-bearing forms other OGC servers emit. All name WGS 84.
    private static readonly string[] Wgs84Spellings =
    [
        "EPSG:4326",
        "epsg:4326",
        "urn:ogc:def:crs:EPSG::4326",
        "urn:ogc:def:crs:EPSG:6.9:4326",
        "urn:x-ogc:def:crs:EPSG::4326",
        "http://www.opengis.net/def/crs/EPSG/0/4326",
        "https://www.opengis.net/def/crs/EPSG/0/4326",
        "urn:ogc:def:crs:OGC:1.3:CRS84",
        "urn:ogc:def:crs:OGC::CRS84",
        "urn:ogc:def:crs:OGC:2:84",
        "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
        "CRS84",
        "OGC:CRS84",
        "crs84",
        "4326",
        "  EPSG:4326  ",
    ];

    // Near-misses the previous substring probe admitted, real CRSes that are NOT
    // WGS 84, and shapes that name no CRS at all.
    private static readonly string[] NonWgs84Spellings =
    [
        "EPSG:43260",
        "EPSG:14326",
        "NOT_CRS84",
        "CRS84_LOCAL",
        "my-4326-grid",
        "urn:ogc:def:crs:EPSG::3857",
        "EPSG:3857",
        "EPSG:32610",
        "EPSG:27700",
        "http://www.opengis.net/def/crs/EPSG/0/3857",
        "GEOGCS[\"WGS 84\"]",
        "",
        "   ",
    ];

    [UnitTest]
    public void IsWgs84_RecognizedWgs84Spellings_ReturnsTrue()
    {
        foreach (var spelling in Wgs84Spellings)
        {
            CrsIdentifier.IsWgs84(spelling).Should().BeTrue(
                "'{0}' is a legitimate spelling of WGS 84 longitude/latitude", spelling);
        }
    }

    [UnitTest]
    public void IsWgs84_NonWgs84AndContrivedNearMisses_ReturnsFalse()
    {
        foreach (var spelling in NonWgs84Spellings)
        {
            CrsIdentifier.IsWgs84(spelling).Should().BeFalse(
                "'{0}' does not name WGS 84", spelling);
        }
    }

    [UnitTest]
    public void IsWgs84_NullIdentifier_ReturnsFalse()
        => CrsIdentifier.IsWgs84(null).Should().BeFalse();

    [UnitTest]
    public void TryParseEpsgCode_RecognizedSpellings_ResolvesTheDeclaredCode()
    {
        var cases = new (string Identifier, int Expected)[]
        {
            ("EPSG:4326", 4326),
            ("EPSG:43260", 43260),
            ("EPSG:3857", 3857),
            ("urn:ogc:def:crs:EPSG::27700", 27700),
            ("urn:ogc:def:crs:EPSG:6.18.3:3857", 3857),
            ("http://www.opengis.net/def/crs/EPSG/0/32610", 32610),
            ("urn:ogc:def:crs:OGC:1.3:CRS84", 4326),
            ("3857", 3857),
        };

        foreach (var (identifier, expected) in cases)
        {
            CrsIdentifier.TryParseEpsgCode(identifier, out var code).Should().BeTrue(
                "'{0}' is a recognized identifier spelling", identifier);
            code.Should().Be(expected, "'{0}' names EPSG:{1}", identifier, expected);
        }
    }

    [UnitTest]
    public void TryParseEpsgCode_UnrecognizedIdentifier_ReturnsFalseRatherThanGuessing()
    {
        // Every one of these embeds digits that a lenient "last numeric token"
        // extractor would happily return as an SRID.
        var unrecognized = new[]
        {
            "NOT_CRS84",
            "WGS 84 / UTM zone 10N",
            "my-4326-grid",
            "EPSG:04326",
            "EPSG:",
            "urn:ogc:def:crs:IAU:2015:49900",
            "{\"wkid\":4326}",
        };

        foreach (var identifier in unrecognized)
        {
            CrsIdentifier.TryParseEpsgCode(identifier, out var code).Should().BeFalse(
                "'{0}' is not a recognized EPSG or CRS84 identifier spelling", identifier);
            code.Should().Be(0);
        }
    }
}
