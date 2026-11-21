// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

public sealed class OgcCsvGoldenTests
{
    private static readonly string[] FieldNames = ["name", "n", "x", "fid"];

    private static readonly string Expected = string.Join(Environment.NewLine,
        "id,name,n,x,fid,geometry",
        "7,'=SUM(A1),42,-1.5,7,\"{\"\"type\"\":\"\"Point\"\",\"\"coordinates\"\":[1.5,2]}\"",
        "\"q,\"\"id\",\"comma,quote\"\"x\",-7,'+abc,,\"{\"\"type\"\":\"\"Polygon\"\"}\"",
        ",\"\",True,12.25,,Point",
        "3,\"é\r\n\",' @x,,3,\"{\"\"type\"\":\"\"P\\u00F6int\"\",\"\"coordinates\"\":[0,0]}\"",
        string.Empty);

    [UnitTest]
    public Task CsvResponse_InvariantCulture_MatchesPinnedBytes()
        => AssertCsvAsync(CultureInfo.InvariantCulture, Expected);

    // Numbers format with the current culture, as value.ToString() did: a decimal comma
    // gets the cell quoted, and a signed value that does not parse invariantly is
    // neutralized as a potential formula.
    [UnitTest]
    public Task CsvResponse_GermanCulture_FormatsNumbersWithCurrentCulture()
        => AssertCsvAsync(
            CultureInfo.GetCultureInfo("de-DE"),
            Expected.Replace(",-1.5,", ",\"'-1,5\",", StringComparison.Ordinal)
                .Replace(",12.25,", ",\"12,25\",", StringComparison.Ordinal));

    private static async Task AssertCsvAsync(CultureInfo culture, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal(expected, OgcResponseFormatter.BuildCsvResponse(Features(), FieldNames, "fid"));

            using var stream = new MemoryStream();
            await OgcResponseFormatter.StreamCsvResponseAsync(stream, Enumerate(), new(FieldNames, "fid"));
            Assert.Equal(expected, Encoding.UTF8.GetString(stream.ToArray()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static GeoJsonFeature[] Features() =>
    [
        new()
        {
            Id = 7L,
            Properties = new() { ["name"] = "=SUM(A1)", ["n"] = 42, ["x"] = -1.5 },
            Geometry = new() { Type = "Point", CoordinatesJson = "[1.5, 2]" }
        },
        new()
        {
            Id = "q,\"id",
            Properties = new() { ["name"] = "comma,quote\"x", ["n"] = -7L, ["x"] = "+abc", ["fid"] = null },
            Geometry = new() { Type = "Polygon" }
        },
        new()
        {
            Properties = new() { ["name"] = string.Empty, ["n"] = true, ["x"] = 12.25m },
            Geometry = new() { Type = "Point", CoordinatesJson = "[1," }
        },
        new()
        {
            Id = 3,
            Properties = new() { ["name"] = "é\r\n", ["n"] = " @x" },
            Geometry = new() { Type = "Pöint", CoordinatesJson = "[0,0]" }
        },
    ];

    private static async IAsyncEnumerable<GeoJsonFeature> Enumerate(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var feature in Features())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
            await Task.Yield();
        }
    }
}
