// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for row-level security (RLS) — issue #502, epic #1275. Proves the
/// row-level core end-to-end against real PostGIS + the canonical FeatureServer query
/// pipeline: an RLS policy filtering by attribute returns only the features matching the
/// caller's claim, and that claim values are bound as parameters (injection-safe).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class RowLevelSecurityEndpointsTests : IAsyncLifetime
{
    // The default seed for layer 0 has five features: three with category 'test' and
    // two with category 'sample' (tests/seed/server.yaml).
    private const int TestFeatureCount = 5;
    private const int CategoryTestCount = 3;
    private const int CategorySampleCount = 2;

    private const string AdminPassword = "rls-admin-key";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            // Real admin gating (not dev-bypass) so anonymous admin calls are rejected.
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        })
        .ConfigureServices(services =>
        {
            // Add the header-driven test auth scheme so query requests can carry a
            // 'category' claim (the X-Test-* headers), and make it the default scheme
            // so the (anonymous-allowed) FeatureServer query authenticates with the
            // claim when headers are present. The admin policy uses its own ApiKey
            // scheme explicitly, so this default override does not affect admin gating.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                    RlsClaimsTestAuthHandler.SchemeName, _ => { });
            services.PostConfigureAll<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = RlsClaimsTestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = RlsClaimsTestAuthHandler.SchemeName;
                options.DefaultScheme = RlsClaimsTestAuthHandler.SchemeName;
            });
        });

    private HttpClient CreateAdminClient()
        => _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/rls-policies")]
    [Endpoint("GET /api/v1/admin/rls-policies")]
    [Endpoint("GET /api/v1/admin/rls-policies/{id}")]
    [Endpoint("DELETE /api/v1/admin/rls-policies/{id}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task RlsPolicy_FiltersFeatureServerQuery_ByCallerClaim()
    {
        var adminClient = CreateAdminClient();
        Guid? policyId = null;

        try
        {
            // Sanity: with no policy the layer returns every feature regardless of claim.
            var unrestricted = await QueryCategoriesAsync(claimCategory: "test");
            unrestricted.Should().HaveCount(TestFeatureCount);

            // Create an RLS policy: any role, any service/layer, attribute 'category'
            // constrained by the caller's 'category' claim value(s).
            policyId = await CreatePolicyAsync(adminClient);

            // The policy is retrievable by id via the admin API.
            var fetched = await GetPolicyAsync(adminClient, policyId.Value);
            fetched.Attribute.Should().Be("category");
            fetched.ClaimType.Should().Be("category");

            // A caller whose 'category' claim is 'test' sees only the three 'test' rows.
            var testRows = await QueryCategoriesAsync(claimCategory: "test");
            testRows.Should().HaveCount(CategoryTestCount);
            testRows.Should().OnlyContain(category => category == "test");

            // A caller whose 'category' claim is 'sample' sees only the two 'sample' rows
            // — the same policy yields different results purely from the claim (RLS works).
            var sampleRows = await QueryCategoriesAsync(claimCategory: "sample");
            sampleRows.Should().HaveCount(CategorySampleCount);
            sampleRows.Should().OnlyContain(category => category == "sample");

            // Injection-safety: a SQL-injection-style claim value is bound as a parameter,
            // so it matches no literal category and returns zero rows (never the whole table).
            var injection = await QueryCategoriesAsync(claimCategory: "' OR '1'='1");
            injection.Should().BeEmpty("RLS claim values must be parameterized, not interpolated into SQL");

            // Fail-secure: a caller with NO 'category' claim sees no rows under the policy.
            var noClaim = await QueryCategoriesAsync(claimCategory: null);
            noClaim.Should().BeEmpty("a missing claim must hide every row, never widen visibility");

            // The policy is listed via the admin API.
            var listed = await ListPoliciesAsync(adminClient);
            listed.Should().Contain(p => p.PolicyId == policyId.Value);
        }
        finally
        {
            if (policyId.HasValue)
            {
                _ = await adminClient.DeleteAsync($"/api/v1/admin/rls-policies/{policyId.Value}");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/rls-policies")]
    public async Task RlsPolicyEndpoints_RejectAnonymousCallers()
    {
        var anonymous = _fixture.CreateClient();
        var request = new CreateRlsPolicyRequest
        {
            Role = "*",
            Service = "*",
            Layer = "*",
            Attribute = "category",
            ClaimType = "category",
        };

        var response = await anonymous.PostAsync(
            "/api/v1/admin/rls-policies",
            JsonContent.Create(request, RlsPolicyJsonContext.Default.CreateRlsPolicyRequest));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private async Task<Guid> CreatePolicyAsync(HttpClient adminClient)
    {
        var request = new CreateRlsPolicyRequest
        {
            Role = "*",
            Service = "*",
            Layer = "*",
            Attribute = "category",
            ClaimType = "category",
            Comparison = "in",
            Description = "RLS test: restrict by category claim",
        };

        var response = await adminClient.PostAsync(
            "/api/v1/admin/rls-policies",
            JsonContent.Create(request, RlsPolicyJsonContext.Default.CreateRlsPolicyRequest));

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);

        using var document = JsonDocument.Parse(payload);
        var id = document.RootElement.GetProperty("data").GetProperty("policyId").GetGuid();
        id.Should().NotBeEmpty();
        return id;
    }

    private async Task<RlsPolicyResponse> GetPolicyAsync(HttpClient adminClient, Guid policyId)
    {
        var response = await adminClient.GetAsync($"/api/v1/admin/rls-policies/{policyId}");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        var parsed = JsonSerializer.Deserialize(
            payload,
            RlsPolicyJsonContext.Default.ApiResponseRlsPolicyResponse);
        parsed!.Data.Should().NotBeNull();
        return parsed.Data!;
    }

    private async Task<IReadOnlyList<RlsPolicyResponse>> ListPoliciesAsync(HttpClient adminClient)
    {
        var response = await adminClient.GetAsync("/api/v1/admin/rls-policies");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        var parsed = JsonSerializer.Deserialize(
            payload,
            RlsPolicyJsonContext.Default.ApiResponseIReadOnlyListRlsPolicyResponse);
        return parsed?.Data ?? Array.Empty<RlsPolicyResponse>();
    }

    private async Task<IReadOnlyList<string?>> QueryCategoriesAsync(string? claimCategory)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "rls-user");
        if (!string.IsNullOrEmpty(claimCategory))
        {
            client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.CategoryHeader, claimCategory);
        }

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json");

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var document = JsonDocument.Parse(payload);
        return document.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("category").GetString())
            .ToList();
    }
}
