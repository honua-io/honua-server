// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class DiscoveryMetadataSerializationTests
{
    private static readonly string[] OptionalFields =
        ["themes", "language", "license", "attribution", "publisher", "contactPoint", "links"];

    [UnitTest]
    public void TypedRequest_OmittedFields_RemainOmittedAfterRoundTrip()
    {
        var request = new DiscoveryMetadataUpdateRequest { Etag = "revision" };
        using var json = RoundTrip(request);
        foreach (var field in OptionalFields)
        {
            json.RootElement.TryGetProperty(field, out _).Should().BeFalse();
        }
    }

    [UnitTest]
    public void TypedRequest_ExplicitNulls_RemainPresentAfterRoundTrip()
    {
        var request = new DiscoveryMetadataUpdateRequest
        {
            Etag = "revision", Themes = null, Language = null, License = null,
            Attribution = null, Publisher = null, ContactPoint = null, Links = null
        };
        using var json = RoundTrip(request);
        foreach (var field in OptionalFields)
        {
            json.RootElement.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [UnitTest]
    public void TypedRequest_EmptyArrays_RemainPresentAfterRoundTrip()
    {
        var request = new DiscoveryMetadataUpdateRequest { Etag = "revision", Themes = [], Links = [] };
        using var json = RoundTrip(request);
        json.RootElement.GetProperty("themes").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("links").GetArrayLength().Should().Be(0);
    }

    private static JsonDocument RoundTrip(DiscoveryMetadataUpdateRequest request)
    {
        var typeInfo = DiscoveryMetadataJsonContext.Default.DiscoveryMetadataUpdateRequest;
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, typeInfo), typeInfo)!;
        return JsonDocument.Parse(JsonSerializer.Serialize(restored, typeInfo));
    }
}
