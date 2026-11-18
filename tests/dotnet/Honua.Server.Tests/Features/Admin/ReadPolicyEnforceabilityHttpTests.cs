// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

// Drives the read-policy enforceability rule through the real admin HTTP pipeline
// (routing, authentication, body binding, DI) instead of calling the handlers directly.
// The checker is swapped for one over a graph where "roads" lives on DuckDB, which
// cannot enforce row or field policies, and "parcels" lives on PostGIS.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class ReadPolicyEnforceabilityHttpTests : IAsyncLifetime
{
    private const string AdminPassword = "read-policy-enforceability-admin-key";
    private const string RlsPoliciesPath = "/api/v1/admin/rls-policies";
    private const string FieldMaskPoliciesPath = "/api/v1/admin/field-mask-policies";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        })
        .ConfigureServices(services =>
            services.AddScoped<ReadPolicyEnforceabilityChecker>(_ => ReadPolicyEnforceabilityTestGraph.CreateChecker()));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/rls-policies")]
    [Endpoint("GET /api/v1/admin/rls-policies")]
    public async Task CreateRlsPolicy_OverHttp_OnLayerWithoutEnforcement_Returns400AndStoresNothing()
    {
        using var admin = CreateAdminClient();
        var before = await CountPoliciesAsync(admin, RlsPoliciesPath);
        using var content = JsonContent.Create(
            new CreateRlsPolicyRequest { Role = "analyst", Service = "*", Layer = "roads", Attribute = "region", ClaimType = "region" },
            RlsPolicyJsonContext.Default.CreateRlsPolicyRequest);

        using var response = await admin.PostAsync(RlsPoliciesPath, content);

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, payload);
        payload.Should().Contain("roads").And.Contain("duckdb");
        (await CountPoliciesAsync(admin, RlsPoliciesPath)).Should().Be(before);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/field-mask-policies")]
    [Endpoint("GET /api/v1/admin/field-mask-policies")]
    public async Task CreateFieldMaskPolicy_OverHttp_WildcardCoveringLayerWithoutEnforcement_Returns400AndStoresNothing()
    {
        using var admin = CreateAdminClient();
        var before = await CountPoliciesAsync(admin, FieldMaskPoliciesPath);

        using var response = await PostFieldMaskPolicyAsync(admin, layer: "*");

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, payload);
        payload.Should().Contain("roads").And.Contain("duckdb");
        (await CountPoliciesAsync(admin, FieldMaskPoliciesPath)).Should().Be(before);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/field-mask-policies")]
    [Endpoint("DELETE /api/v1/admin/field-mask-policies/{id}")]
    public async Task CreateFieldMaskPolicy_OverHttp_OnEnforcingLayer_IsCreated()
    {
        using var admin = CreateAdminClient();

        using var response = await PostFieldMaskPolicyAsync(admin, layer: "parcels");

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);
        using var document = JsonDocument.Parse(payload);
        var policyId = document.RootElement.GetProperty("data").GetProperty("policyId").GetGuid();
        using var deleted = await admin.DeleteAsync($"{FieldMaskPoliciesPath}/{policyId}");
        deleted.IsSuccessStatusCode.Should().BeTrue();
    }

    private HttpClient CreateAdminClient()
        => _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

    private static async Task<HttpResponseMessage> PostFieldMaskPolicyAsync(HttpClient admin, string layer)
    {
        using var content = JsonContent.Create(
            new CreateFieldMaskPolicyRequest { Role = "analyst", Service = "*", Layer = layer, Attribute = "name" },
            FieldMaskPolicyJsonContext.Default.CreateFieldMaskPolicyRequest);
        return await admin.PostAsync(FieldMaskPoliciesPath, content);
    }

    private static async Task<int> CountPoliciesAsync(HttpClient admin, string path)
    {
        using var response = await admin.GetAsync(path);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("data").GetArrayLength();
    }
}
