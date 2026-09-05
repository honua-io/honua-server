// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;

namespace Honua.Server.Tests.Features.Protocols.Stac;

public sealed class OpenDataStacProjectionTests
{
    [Theory]
    [InlineData("2024-01-01T00:00:00+10:00", "2023-12-31T14:00:00Z")]
    [InlineData("2024-01-01T00:00:00-10:00", "2024-01-01T10:00:00Z")]
    public void OffsetExtent_PreservesInstantInItemAndCollection(string source, string expected)
    {
        var instant = DateTimeOffset.Parse(source, CultureInfo.InvariantCulture);
        var page = new ConsoleOpenDataPage
        {
            ItemId = "dataset",
            TemporalExtent = new ConsoleTemporalExtent { Start = instant, End = instant }
        };
        var publication = new ConsoleStacPublicationState { ItemId = page.ItemId };

        var item = ConsoleOpenDataMapper.BuildStacItem(page, publication, "https://example.test/stac");
        var collection = ConsoleOpenDataMapper.BuildStacCollection(page, publication, "https://example.test/stac");

        item.Properties["datetime"].Should().Be(expected);
        collection.Extent.Temporal.Interval.Single().Should().Equal(expected, expected);
    }

    [Fact]
    public void EmptyOptionalMetadata_SerializesRequiredStac10Members()
    {
        var page = new ConsoleOpenDataPage { ItemId = "dataset", UpdatedAt = DateTimeOffset.UnixEpoch };
        var publication = new ConsoleStacPublicationState { ItemId = page.ItemId };
        var item = ConsoleOpenDataMapper.BuildStacItem(page, publication, "https://example.test/stac");
        var collection = ConsoleOpenDataMapper.BuildStacCollection(page, publication, "https://example.test/stac");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(item, ConsoleJsonContext.Default.StacProjectionItem));
        json.RootElement.GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("assets").ValueKind.Should().Be(JsonValueKind.Object);
        json.RootElement.GetProperty("assets").EnumerateObject().Should().BeEmpty();
        collection.License.Should().Be("proprietary");
    }
}
