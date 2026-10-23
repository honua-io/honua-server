// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

internal static class SensorThingsTestData
{
    internal static async Task<long> CreateFeatureAsync(WebAppFixture fixture)
    {
        using var admin = fixture.CreateAdminClient();
        using var content = new StringContent("""
            {"name":"Test sampling site","description":"Explicit synthetic integration-test site",
             "encodingType":"application/vnd.geo+json","feature":{"type":"Feature","geometry":{"type":"Point","coordinates":[-157.8,21.3]},"properties":{"fixture":true}}}
            """, Encoding.UTF8, "application/json");
        using var response = await admin.PostAsync("/sta/v1.1/FeaturesOfInterest", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("@iot.id").GetInt64();
    }

    internal static StringContent ObservationJson(string json, long featureId)
    {
        var root = JsonNode.Parse(json)!;
        if (root is JsonObject body && body.ContainsKey("result")) body["FeatureOfInterest"] ??= new JsonObject { ["@iot.id"] = featureId };
        if (root["value"] is JsonArray bulk)
            foreach (var item in bulk.OfType<JsonObject>()) item["FeatureOfInterest"] ??= new JsonObject { ["@iot.id"] = featureId };
        return new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");
    }
}
