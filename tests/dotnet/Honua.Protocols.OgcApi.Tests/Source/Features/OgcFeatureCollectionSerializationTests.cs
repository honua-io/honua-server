// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Common;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeatureCollectionSerializationTests
{
    [Fact]
    public void FeatureCollectionTimestamp_UsesFixedWidthFractionalSeconds()
    {
        var collection = new FeatureCollection
        {
            Features = [],
            TimeStamp = new DateTimeOffset(2026, 8, 30, 12, 34, 56, TimeSpan.Zero).AddTicks(120_000)
        };

        var json = JsonSerializer.Serialize(collection, OgcJsonContext.Default.FeatureCollection);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("timeStamp").GetString()
            .Should().Be("2026-08-30T12:34:56.0120000Z");
    }
}
