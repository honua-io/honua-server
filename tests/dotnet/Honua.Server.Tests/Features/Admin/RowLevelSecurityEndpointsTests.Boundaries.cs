// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class RowLevelSecurityEndpointsTests
{
    private const string PolicyArea = """
        {"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}
        """;

    [IntegrationTheory]
    [InlineData("expression", new long[] { 910, 912 })]
    [InlineData("area", new long[] { 910, 911 })]
    [InlineData("combined", new long[] { 910 })]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/rls-policies")]
    [Endpoint("GET /api/v1/admin/rls-policies/{id}")]
    [Endpoint("DELETE /api/v1/admin/rls-policies/{id}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task RlsPolicy_ConfiguredBoundaries_IntersectCallerClaims(string mode, long[] expected)
    {
        await SeedPolicyBoundaryRowsAsync();
        using var admin = CreateAdminClient();
        Guid? policyId = null;
        try
        {
            using var response = await admin.PostAsJsonAsync("/api/v1/admin/rls-policies", new
            {
                role = "*", service = "*", layer = "*", attribute = "category", claimType = "category",
                filterExpression = mode == "area" ? null : "name = 'allowed'",
                areaGeometry = mode == "expression" ? null : PolicyArea
            });
            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Created, payload);
            using var document = JsonDocument.Parse(payload);
            policyId = document.RootElement.GetProperty("data").GetProperty("policyId").GetGuid();

            var rows = await QueryObjectIdsByCategoryAsync("boundary");
            rows.Select(row => row.ObjectId).Should().BeEquivalentTo(expected);
            (await QueryObjectIdsByCategoryAsync(null)).Should().BeEmpty();
            (await QueryObjectIdsByCategoryAsync("other")).Should().BeEmpty();

            using var fetched = await admin.GetAsync($"/api/v1/admin/rls-policies/{policyId}");
            fetched.StatusCode.Should().Be(HttpStatusCode.OK);
            using var stored = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync());
            var policy = stored.RootElement.GetProperty("data");
            (policy.TryGetProperty("filterExpression", out var filter) ? filter.GetString() : null).Should().Be(mode == "area" ? null : "name = 'allowed'");
            (policy.TryGetProperty("areaGeometry", out var area) ? area.GetString() : null).Should().Be(mode == "expression" ? null : PolicyArea);
        }
        finally
        {
            if (policyId.HasValue)
            {
                await admin.DeleteAsync($"/api/v1/admin/rls-policies/{policyId}");
            }
            await _fixture.Postgres.ExecuteAsync($"DELETE FROM \"{_fixture.CurrentSchema}\".features WHERE layer_id = 0 AND objectid BETWEEN 910 AND 912");
        }
    }

    [IntegrationTheory]
    [InlineData("name =", null)]
    [InlineData(null, "{}")]
    [InlineData(null, "{\"type\":\"Point\",\"coordinates\":[0,0]}")]
    [InlineData(null, """{"type":"Polygon","coordinates":[[[0,0],[2,2],[0,2],[2,0],[0,0]]]}""")]
    [Endpoint("POST /api/v1/admin/rls-policies")]
    public async Task RlsPolicy_InvalidRestrictions_RejectsCreation(string? filter, string? area)
    {
        using var admin = CreateAdminClient();
        using var response = await admin.PostAsJsonAsync("/api/v1/admin/rls-policies", new
        {
            role = "*", service = "*", layer = "*", attribute = "category", claimType = "category",
            filterExpression = filter, areaGeometry = area
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task RlsPolicy_PersistedInvalidArea_RefusesQuery()
    {
        using var admin = CreateAdminClient();
        var policyId = await CreatePolicyAsync(admin);
        try
        {
            (await QueryObjectIdsByCategoryAsync("test")).Should().NotBeEmpty();
            await _fixture.Postgres.ExecuteAsync($$"""
                UPDATE honua.rbac_rls_policies
                SET area_geometry = '{"type":"Polygon","coordinates":[[[0,0],[2,2],[0,2],[2,0],[0,0]]]}'
                WHERE policy_id = '{{policyId}}';
                """);
            using var client = _fixture.CreateClient();
            client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "rls-user");
            client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.CategoryHeader, "test");
            using var response = await client.GetAsync(
                $"/rest/services/{Honua.TestKit.WebAppFixture.TestServiceId}/FeatureServer/{Honua.TestKit.WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&f=json");

            response.IsSuccessStatusCode.Should().BeFalse();
        }
        finally
        {
            await admin.DeleteAsync($"/api/v1/admin/rls-policies/{policyId}");
        }
    }

    private Task SeedPolicyBoundaryRowsAsync() => _fixture.Postgres.ExecuteAsync($$"""
        INSERT INTO "{{_fixture.CurrentSchema}}".features (layer_id, objectid, geometry, attributes)
        VALUES
            (0, 910, ST_SetSRID(ST_MakePoint(1,1),4326), '{"category":"boundary","name":"allowed"}'),
            (0, 911, ST_SetSRID(ST_MakePoint(1,1),4326), '{"category":"boundary","name":"other"}'),
            (0, 912, ST_SetSRID(ST_MakePoint(3,3),4326), '{"category":"boundary","name":"allowed"}');
        """);
}
