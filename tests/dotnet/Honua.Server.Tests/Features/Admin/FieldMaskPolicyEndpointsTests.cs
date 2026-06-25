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
/// Integration tests for field-level security (column masking) — issue #1940, the
/// field-level companion to RLS (#502). Proves the masking core end-to-end against real
/// PostGIS + the canonical FeatureServer query pipeline: a field-mask policy keyed by role
/// removes a column from query output for a restricted role across the shared projection
/// seam, even when the caller explicitly requests the column via <c>outFields</c>, while an
/// unrestricted caller still receives it.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class FieldMaskPolicyEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "field-mask-admin-key";

    // The default seed for layer 0 has five features, each carrying a 'description'
    // attribute (tests/seed/server.yaml). The mask policy hides 'description'.
    private const int TestFeatureCount = 5;
    private const string MaskedAttribute = "description";
    private const string RestrictedRole = "field-restricted";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        })
        .ConfigureServices(services =>
        {
            // Reuse the header-driven RLS test auth scheme: it projects X-Test-Roles into
            // role claims, which the field-mask resolver keys on. Make it the default
            // scheme so the (anonymous-allowed) FeatureServer query authenticates with the
            // roles when the headers are present.
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
    [Endpoint("POST /api/v1/admin/field-mask-policies")]
    [Endpoint("GET /api/v1/admin/field-mask-policies")]
    [Endpoint("GET /api/v1/admin/field-mask-policies/{id}")]
    [Endpoint("DELETE /api/v1/admin/field-mask-policies/{id}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FieldMaskPolicy_MasksColumn_ForRestrictedRoleOnly()
    {
        var adminClient = CreateAdminClient();
        Guid? policyId = null;

        try
        {
            // Sanity: with no policy, every feature exposes the 'description' attribute,
            // for both the restricted role and an unrestricted caller.
            var unmaskedRestricted = await QueryFeatureAttributesAsync(role: RestrictedRole);
            unmaskedRestricted.Should().HaveCount(TestFeatureCount);
            unmaskedRestricted.Should().OnlyContain(attrs => attrs.ContainsKey(MaskedAttribute));

            // Create a field-mask policy: restricted role, any service/layer, mask
            // 'description'.
            policyId = await CreatePolicyAsync(adminClient);

            // The policy is retrievable by id via the admin API.
            var fetched = await GetPolicyAsync(adminClient, policyId.Value);
            fetched.Attribute.Should().Be(MaskedAttribute);
            fetched.Role.Should().Be(RestrictedRole);

            // The restricted role's query no longer receives the 'description' column —
            // even though outFields=* explicitly requests every field (fail-secure).
            var maskedRows = await QueryFeatureAttributesAsync(role: RestrictedRole);
            maskedRows.Should().HaveCount(TestFeatureCount);
            maskedRows.Should().OnlyContain(
                attrs => !attrs.ContainsKey(MaskedAttribute),
                "the masked column must be dropped for the restricted role even when explicitly requested");
            // Non-masked attributes are still present (only the masked column is removed).
            maskedRows.Should().OnlyContain(attrs => attrs.ContainsKey("name"));

            // An unrestricted caller (different role) still receives 'description' — the
            // same layer yields different columns purely from the caller's role.
            var unrestricted = await QueryFeatureAttributesAsync(role: "analyst");
            unrestricted.Should().HaveCount(TestFeatureCount);
            unrestricted.Should().OnlyContain(attrs => attrs.ContainsKey(MaskedAttribute));

            // The policy is listed via the admin API.
            var listed = await ListPoliciesAsync(adminClient);
            listed.Should().Contain(p => p.PolicyId == policyId.Value);
        }
        finally
        {
            if (policyId.HasValue)
            {
                _ = await adminClient.DeleteAsync($"/api/v1/admin/field-mask-policies/{policyId.Value}");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/field-mask-policies")]
    public async Task FieldMaskPolicyEndpoints_RejectAnonymousCallers()
    {
        var anonymous = _fixture.CreateClient();
        var request = new CreateFieldMaskPolicyRequest
        {
            Role = RestrictedRole,
            Service = "*",
            Layer = "*",
            Attribute = MaskedAttribute,
        };

        var response = await anonymous.PostAsync(
            "/api/v1/admin/field-mask-policies",
            JsonContent.Create(request, FieldMaskPolicyJsonContext.Default.CreateFieldMaskPolicyRequest));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private async Task<Guid> CreatePolicyAsync(HttpClient adminClient)
    {
        var request = new CreateFieldMaskPolicyRequest
        {
            Role = RestrictedRole,
            Service = "*",
            Layer = "*",
            Attribute = MaskedAttribute,
            Description = "Field-mask test: hide description from the restricted role",
        };

        var response = await adminClient.PostAsync(
            "/api/v1/admin/field-mask-policies",
            JsonContent.Create(request, FieldMaskPolicyJsonContext.Default.CreateFieldMaskPolicyRequest));

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);

        using var document = JsonDocument.Parse(payload);
        var id = document.RootElement.GetProperty("data").GetProperty("policyId").GetGuid();
        id.Should().NotBeEmpty();
        return id;
    }

    private async Task<FieldMaskPolicyResponse> GetPolicyAsync(HttpClient adminClient, Guid policyId)
    {
        var response = await adminClient.GetAsync($"/api/v1/admin/field-mask-policies/{policyId}");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        var parsed = JsonSerializer.Deserialize(
            payload,
            FieldMaskPolicyJsonContext.Default.ApiResponseFieldMaskPolicyResponse);
        parsed!.Data.Should().NotBeNull();
        return parsed.Data!;
    }

    private async Task<IReadOnlyList<FieldMaskPolicyResponse>> ListPoliciesAsync(HttpClient adminClient)
    {
        var response = await adminClient.GetAsync("/api/v1/admin/field-mask-policies");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        var parsed = JsonSerializer.Deserialize(
            payload,
            FieldMaskPolicyJsonContext.Default.ApiResponseIReadOnlyListFieldMaskPolicyResponse);
        return parsed?.Data ?? Array.Empty<FieldMaskPolicyResponse>();
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> QueryFeatureAttributesAsync(string role)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "field-mask-user");
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, role);

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json");

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var document = JsonDocument.Parse(payload);
        return document.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .Select(feature => (IReadOnlyDictionary<string, JsonElement>)feature
                .GetProperty("attributes")
                .EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
