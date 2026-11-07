// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

public sealed class StudioPackageDisplayTests
{
    [UnitTest]
    public void StudioEnvelope_DisplayMetadata_RoundTripsThroughCanonicalSerializationAndHash()
    {
        var envelope = new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.App, SchemaVersion = "studio_app.v1",
            Display = new() { Title = "Survey", Description = "Published survey", Thumbnail = "/images/survey.png" }
        };
        var json = JsonSerializer.Serialize(envelope, StudioJsonContext.Default.StudioPackageEnvelope);
        var restored = JsonSerializer.Deserialize(json, StudioJsonContext.Default.StudioPackageEnvelope)!;
        Assert.Equal(envelope.Display, restored.Display);
        Assert.Equal(StudioPackageHash.Compute(envelope), StudioPackageHash.Compute(restored));
        Assert.NotEqual(StudioPackageHash.Compute(envelope), StudioPackageHash.Compute(envelope with
        {
            Display = envelope.Display with { Thumbnail = "/images/revised.png" }
        }));
    }

    [UnitTest]
    public void StudioEnvelope_NoDisplayMetadata_PreservesLegacySerializedEnvelope()
    {
        const string legacy = "{\"family\":\"app\",\"schemaVersion\":\"studio_app.v1\"}";
        var envelope = JsonSerializer.Deserialize(legacy, StudioJsonContext.Default.StudioPackageEnvelope)!;
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(envelope, StudioJsonContext.Default.StudioPackageEnvelope));
        Assert.False(serialized.RootElement.TryGetProperty("display", out _));
        Assert.Null(envelope.Display);
    }
}
