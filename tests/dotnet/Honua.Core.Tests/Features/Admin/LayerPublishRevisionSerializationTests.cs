// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Admin;

public sealed class LayerPublishRevisionSerializationTests
{
    [UnitTest]
    public void ExpectedMetadataRevision_IsNeverReadFromOrWrittenToJson()
    {
        var request = new LayerPublishRequest
        {
            Schema = "public", Table = "source", LayerName = "Layer", ExpectedMetadataRevision = 42, ExpectedMetadataEtag = "trusted"
        };
        var context = LayerPublishRevisionJsonContext.Default.LayerPublishRequest;

        var serialized = JsonSerializer.Serialize(request, context);
        using var json = JsonDocument.Parse(serialized);
        json.RootElement.TryGetProperty("expectedMetadataRevision", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("expectedMetadataEtag", out _).Should().BeFalse();
        var parsed = JsonSerializer.Deserialize(
            """{"schema":"public","table":"source","layerName":"Layer","expectedMetadataRevision":42,"expectedMetadataEtag":"untrusted"}""", context);
        parsed.Should().NotBeNull();
        parsed!.ExpectedMetadataRevision.Should().BeNull();
        parsed.ExpectedMetadataEtag.Should().BeNull();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LayerPublishRequest))]
internal sealed partial class LayerPublishRevisionJsonContext : JsonSerializerContext;
