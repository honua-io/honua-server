// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Import.FileImport;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class FilePreviewSerializationTests
{
    // The GeoJSON and GeoParquet readers keep nested attributes as JsonElement values, so the
    // preview response must serialize them; the import itself stores them in the properties document.
    [UnitTest]
    public void Preview_WithNestedSampleProperties_SerializesThemAsJson()
    {
        using var source = JsonDocument.Parse("""{"address":{"city":"Hilo","zip":96720},"tags":["a",1,null],"empty":{}}""");
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.GeoJson,
            TotalFeatureCount = 1,
            SampleProperties = new Dictionary<string, object?>
            {
                ["name"] = "parcel",
                ["count"] = 7L,
                ["ratio"] = 0.5d,
                ["active"] = true,
                ["missing"] = null,
                ["address"] = source.RootElement.GetProperty("address").Clone(),
                ["tags"] = source.RootElement.GetProperty("tags").Clone(),
                ["empty"] = source.RootElement.GetProperty("empty").Clone(),
            },
        };

        var json = JsonSerializer.Serialize(preview, ImportJsonContext.Default.FilePreview);

        using var result = JsonDocument.Parse(json);
        var sample = result.RootElement.GetProperty("sampleProperties");
        sample.GetProperty("name").GetString().Should().Be("parcel");
        sample.GetProperty("count").GetInt64().Should().Be(7);
        sample.GetProperty("ratio").GetDouble().Should().Be(0.5);
        sample.GetProperty("active").GetBoolean().Should().BeTrue();
        sample.GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        sample.GetProperty("address").GetProperty("city").GetString().Should().Be("Hilo");
        sample.GetProperty("address").GetProperty("zip").GetInt32().Should().Be(96720);
        sample.GetProperty("tags").GetRawText().Should().Be("""["a",1,null]""");
        sample.GetProperty("empty").GetRawText().Should().Be("{}");
    }
}
