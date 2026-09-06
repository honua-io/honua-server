// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Protocols.OData;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed partial class ODataDeltaTests
{
    [IntegrationTheory]
    [InlineData("expired", HttpStatusCode.Gone, "DeltaTokenExpired")]
    [InlineData("future", HttpStatusCode.Gone, "DeltaTokenExpired")]
    [InlineData("missing", HttpStatusCode.Gone, "DeltaTokenExpired")]
    [InlineData("malformed", HttpStatusCode.BadRequest, "InvalidQueryOption")]
    [InlineData("query", HttpStatusCode.BadRequest, "DeltaQueryMismatch")]
    [InlineData("scope", HttpStatusCode.Gone, "DeltaScopeChanged")]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Delta_InvalidContinuation_ReturnsTypedRecoveryWithoutValues(string scenario, HttpStatusCode status, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/Features(0)?$top=100");
        request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");
        using var baselineResponse = await _fixture.Client.SendAsync(request);
        baselineResponse.StatusCode.Should().Be(HttpStatusCode.OK, string.Join(Environment.NewLine, _queryErrors));
        using var baseline = JsonDocument.Parse(await baselineResponse.Content.ReadAsStringAsync());
        var link = new Uri(baseline.RootElement.GetProperty("@odata.deltaLink").GetString()!);
        var token = QueryHelpers.ParseQuery(link.Query)["$deltatoken"].ToString();
        var id = Guid.ParseExact(token.Split('.')[1], "N");
        var store = _fixture.GetService<IQuerySnapshotStore>();
        var payload = await store.ReadAsync(id);
        payload.Should().NotBeNull();
        var snapshot = JsonSerializer.Deserialize(payload!, ODataQuerySnapshotJsonContext.Default.ODataQuerySnapshot)!;
        if (scenario is "expired" or "future")
        {
            var replacement = snapshot with
            {
                Id = Guid.NewGuid(),
                CreatedAt = scenario == "future" ? DateTimeOffset.UtcNow.AddHours(1) : DateTimeOffset.UtcNow.AddDays(-2)
            };
            await store.SaveAsync(replacement.Id,
                JsonSerializer.SerializeToUtf8Bytes(replacement, ODataQuerySnapshotJsonContext.Default.ODataQuerySnapshot),
                scenario == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddDays(1));
            token = $"v2.{replacement.Id:N}.0.t";
        }
        else if (scenario == "missing") { token = $"v2.{Guid.NewGuid():N}.0.t"; }
        else if (scenario == "malformed") { token = $"v2.{id:N}.-1.p"; }
        else if (scenario == "scope")
        {
            // A canonical metadata change invalidates the stored authorization
            // binding even while the route remains readable to this principal.
            _fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Description = "changed policy binding" });
        }
        var path = "/odata/Features(0)?$deltatoken=" + token + (scenario == "query" ? "&$filter=ObjectId%20gt%201" : "");
        using var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(status);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be(code);
        error.RootElement.TryGetProperty("value", out _).Should().BeFalse();
        error.RootElement.TryGetProperty("@odata.deltaLink", out _).Should().BeFalse("recovery cannot silently rebaseline");
    }
}

public sealed class ODataDeltaValueTests
{
    [UnitTest]
    public void Changes_CompareValuesAndGeometry_EmitOnlyNetChangesAndKeyOnlyRemovals()
    {
        using var before = JsonDocument.Parse("""
            [{"ObjectId":1,"name":"old","Geometry":null},
             {"ObjectId":2,"name":"same","Geometry":null},
             {"ObjectId":3,"name":"private-old","Geometry":null}]
            """);
        using var after = JsonDocument.Parse("""
            [{"Geometry":null,"name":"same","ObjectId":2},
             {"ObjectId":1,"name":"updated","Geometry":{"type":"Point","coordinates":[2.5,3.75]}},
             {"ObjectId":4,"value":19.25,"Geometry":null}]
            """);
        var actual = ODataStreamingQueryHandler.ComputeDeltaChanges(
            before.RootElement.EnumerateArray().ToArray(), after.RootElement.EnumerateArray().ToArray(), 0);
        using var expected = JsonDocument.Parse("""
            [{"ObjectId":1,"name":"updated","Geometry":{"type":"Point","coordinates":[2.5,3.75]}},
             {"ObjectId":3,"LayerId":0,"@removed":{"reason":"changed"}},
             {"ObjectId":4,"value":19.25,"Geometry":null}]
            """);
        actual.Should().HaveCount(3);
        for (var index = 0; index < actual.Length; index++)
        {
            JsonElement.DeepEquals(actual[index], expected.RootElement[index]).Should().BeTrue($"net delta entry {index} has independently specified values and geometry");
        }
    }
}
