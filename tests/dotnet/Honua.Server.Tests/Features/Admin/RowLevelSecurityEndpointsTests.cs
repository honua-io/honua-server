// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
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
public sealed partial class RowLevelSecurityEndpointsTests : IAsyncLifetime
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

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /api/v1/admin/rls-policies")]
    [Endpoint("POST /api/v1/admin/field-mask-policies")]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task RoutedPolicyCollision_AdminOnlyFirstResource_DoesNotOverrideAuthorizedResourcePolicy(bool fieldMask)
    {
        var original = _fixture.GetCurrentV2GraphSnapshot();
        var (baseline, graph, resourceName) = CreateCollisionGraph(original);
        MetadataV2GraphValidator.Validate(baseline).IsValid.Should().BeTrue();
        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
        var provider = _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>();
        provider.SetGraph(graph, schema: _fixture.MetadataGraphSchema);
        using var adminClient = CreateAdminClient();
        Guid? policyId = null;
        var policyPath = fieldMask ? "/api/v1/admin/field-mask-policies" : "/api/v1/admin/rls-policies";
        try
        {
            using var client = _fixture.CreateClient();
            client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "restricted-user");
            client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.CategoryHeader, "test");
            var denied = await client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/77/query?where=1%3D1&outFields=*&returnGeometry=false&f=json");
            denied.StatusCode.Should().Be(HttpStatusCode.OK);
            using var deniedDocument = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
            deniedDocument.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(403);
            var deniedOData = await client.GetAsync("/odata/Features(77)?$top=10");
            deniedOData.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            policyId = fieldMask
                ? await CreateCollisionMaskPolicyAsync(adminClient, resourceName)
                : await CreatePolicyAsync(adminClient, resourceName);
            provider.SetGraph(baseline, schema: _fixture.MetadataGraphSchema);
            await AssertCollisionPolicyResponseAsync(client, fieldMask);
            await AssertODataCollisionPolicyResponseAsync(client, fieldMask);
            provider.SetGraph(graph, schema: _fixture.MetadataGraphSchema);
            using (new AssertionScope())
            {
                await AssertCollisionPolicyResponseAsync(client, fieldMask);
                await AssertODataCollisionPolicyResponseAsync(client, fieldMask);
            }
        }
        finally
        {
            if (policyId.HasValue)
            {
                await adminClient.DeleteAsync($"{policyPath}/{policyId.Value}");
            }
            provider.SetGraph(original.Graph, schema: _fixture.MetadataGraphSchema);
        }
    }

    private static (MetadataV2Graph Baseline, MetadataV2Graph Graph, string ResourceName) CreateCollisionGraph(MetadataV2GraphSnapshot original)
    {
        var publication = original.Graph.Publications.First(candidate =>
            candidate.PublicationType == MetadataV2PublicationType.EsriFeatureLayer && candidate.LayerIndex == WebAppFixture.TestLayerId);
        var resource = original.ResolveResource(publication)!;
        var binding = original.Graph.StorageBindings.First(candidate =>
            candidate.ResourceId == resource.Metadata.Id && candidate.StorageLayerId == WebAppFixture.TestLayerId);
        var odataService = original.Index.ServicesById[publication.ServiceId] with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "collision-odata-service", Name = "collision-odata" },
            Protocols = [ServiceProtocols.OData]
        };
        var odataPublication = publication with
        {
            Metadata = publication.Metadata with { Id = "collision-odata-publication", Name = "collision-odata-publication" },
            ServiceId = odataService.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            PublicationType = MetadataV2PublicationType.ODataEntitySet
        };
        var baseline = original.Graph with
        {
            Revision = original.Graph.Revision + 1,
            Services = [.. original.Graph.Services, odataService],
            Publications = [odataPublication, .. original.Graph.Publications]
        };
        var adminBinding = binding with
        {
            Metadata = binding.Metadata with { Id = "collision-admin-binding", Name = "collision-admin-binding" },
            ResourceId = "collision-admin-resource"
        };
        var adminResource = resource with
        {
            Metadata = resource.Metadata with { Id = "collision-admin-resource", Name = "admin-only-collision" },
            StorageBindingIds = [adminBinding.Metadata.Id],
            AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["admin"] }
        };
        var adminPublication = publication with
        {
            Metadata = publication.Metadata with { Id = "collision-admin-publication", Name = "collision-admin-publication" },
            ResourceId = adminResource.Metadata.Id,
            StorageBindingId = adminBinding.Metadata.Id,
            LayerIndex = 77
        };
        var adminODataPublication = adminPublication with
        {
            Metadata = adminPublication.Metadata with { Id = "collision-admin-odata-publication", Name = "collision-admin-odata-publication" },
            ServiceId = odataService.Metadata.Id,
            PublicationType = MetadataV2PublicationType.ODataEntitySet
        };
        var graph = baseline with
        {
            Revision = baseline.Revision + 1,
            Resources = [adminResource, .. baseline.Resources],
            StorageBindings = [adminBinding, .. baseline.StorageBindings],
            Publications = [adminPublication, adminODataPublication, .. baseline.Publications]
        };
        return (baseline, graph, resource.Metadata.Name);
    }

    private static async Task<Guid> CreateCollisionMaskPolicyAsync(HttpClient adminClient, string resourceName)
    {
        using var content = JsonContent.Create(new CreateFieldMaskPolicyRequest
        {
            Role = "*",
            Service = "*",
            Layer = resourceName,
            Attribute = "description"
        }, FieldMaskPolicyJsonContext.Default.CreateFieldMaskPolicyRequest);
        var response = await adminClient.PostAsync("/api/v1/admin/field-mask-policies", content);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);
        using var body = JsonDocument.Parse(payload);
        return body.RootElement.GetProperty("data").GetProperty("policyId").GetGuid();
    }

    private static async Task AssertCollisionPolicyResponseAsync(HttpClient client, bool fieldMask)
    {
        var response = await client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        var attributes = document.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes")).ToArray();
        AssertCollisionPolicyAttributes(attributes, fieldMask);
    }

    private static async Task AssertODataCollisionPolicyResponseAsync(HttpClient client, bool fieldMask)
    {
        var response = await client.GetAsync($"/odata/Features({WebAppFixture.TestLayerId})?$top=10");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        var attributes = document.RootElement.GetProperty("value").EnumerateArray()
            .Select(ODataTestHelpers.ParseAttributes).ToArray();
        AssertCollisionPolicyAttributes(attributes, fieldMask);
    }

    private static void AssertCollisionPolicyAttributes(JsonElement[] attributes, bool fieldMask)
    {
        if (fieldMask)
        {
            attributes.Should().HaveCount(TestFeatureCount);
            attributes.Should().OnlyContain(value => !value.EnumerateObject().Any(property => property.Name == "description"));
        }
        else
        {
            attributes.Should().HaveCount(CategoryTestCount);
            attributes.Should().OnlyContain(value => value.GetProperty("category").GetString() == "test");
        }
    }

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
            listed.Should().Contain(p => p.PolicyId == policyId!.Value);
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
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /api/v1/admin/rls-policies")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    public async Task RlsPolicy_BlocksEditAndDeleteOfHiddenRow_OnDefaultObjectIdLayer()
    {
        // #2066: on a default-OBJECTID layer, the edit not-found guard was skipped on the fast
        // path, so a caller could delete/update a row RLS hides from them by its OBJECTID. The
        // guard must now reject the hidden objectId ("Feature not found") and leave the row intact.
        var adminClient = CreateAdminClient();
        Guid? policyId = null;

        try
        {
            // Capture a 'test'-category row's OBJECTID BEFORE the policy exists. The query is
            // unrestricted (where=1=1, no policy yet), so it returns every feature; pick a row
            // whose category is 'test' as the row that the policy will later hide from a
            // 'sample' caller.
            var rowsBefore = await QueryObjectIdsByCategoryAsync(claimCategory: "test");
            rowsBefore.Should().HaveCount(TestFeatureCount);
            var testRowsBefore = rowsBefore.Where(r => r.Category == "test").ToList();
            testRowsBefore.Should().HaveCount(CategoryTestCount);
            var hiddenObjectId = testRowsBefore[0].ObjectId;

            policyId = await CreatePolicyAsync(adminClient);

            // Caller's claim is 'sample' — the 'test' row is RLS-hidden from them.
            var visibleToSample = await QueryObjectIdsByCategoryAsync(claimCategory: "sample");
            visibleToSample.Should().OnlyContain(r => r.Category == "sample");
            visibleToSample.Should().NotContain(r => r.ObjectId == hiddenObjectId);

            // deleteFeatures of the hidden objectId must NOT succeed — either rejected outright
            // (write-RBAC) or returned as a per-row failure ("Feature not found" from the RLS guard).
            var deleteSucceeded = await EditHiddenRowSucceededAsync(
                claimCategory: "sample",
                path: $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/deleteFeatures",
                form: [new KeyValuePair<string, string>("objectIds", hiddenObjectId.ToString(CultureInfo.InvariantCulture))],
                resultsProperty: "deleteResults");
            deleteSucceeded.Should().BeFalse("an RLS-hidden row must not be deletable via its OBJECTID");

            // updateFeatures of the hidden objectId must likewise not succeed.
            var updateFeatureJson =
                "[{\"attributes\":{\"objectid\":" + hiddenObjectId.ToString(CultureInfo.InvariantCulture) + ",\"category\":\"pwned\"}}]";
            var updateSucceeded = await EditHiddenRowSucceededAsync(
                claimCategory: "sample",
                path: $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/updateFeatures",
                form: [new KeyValuePair<string, string>("features", updateFeatureJson)],
                resultsProperty: "updateResults");
            updateSucceeded.Should().BeFalse("an RLS-hidden row must not be updatable via its OBJECTID");

            // The row still exists and is unchanged: drop the policy and re-query unrestricted.
            _ = await adminClient.DeleteAsync($"/api/v1/admin/rls-policies/{policyId.Value}");
            policyId = null;

            var rowsAfter = await QueryObjectIdsByCategoryAsync(claimCategory: "test");
            rowsAfter.Should().Contain(r => r.ObjectId == hiddenObjectId && r.Category == "test",
                "the RLS-hidden row must survive the blocked edit/delete intact");
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
        var adminClient = CreateAdminClient();
        var request = new CreateRlsPolicyRequest
        {
            Role = "*",
            Service = "*",
            Layer = "*",
            Attribute = "category",
            ClaimType = "category",
        };

        using var content3 = JsonContent.Create(request, RlsPolicyJsonContext.Default.CreateRlsPolicyRequest);
        var response = await anonymous.PostAsync(
            "/api/v1/admin/rls-policies",
            content3);

        // The admin policy names the ApiKey scheme, so an unauthenticated caller is challenged:
        // exactly 401, never 403 (which would mean authenticated-but-unprivileged).
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Nothing is disclosed: no policy id comes back.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("policyId");

        // Nothing is changed: no RLS policy was created. Read back through the admin
        // principal, which proves the same surface works for an authorised caller.
        var listed = await ListPoliciesAsync(adminClient);
        listed.Should().NotContain(
            p => p.Attribute == "category" && p.ClaimType == "category",
            "an unauthenticated POST must not create an RLS policy");

        // A GET is refused the same way, so the denial is not write-only.
        var anonymousList = await anonymous.GetAsync("/api/v1/admin/rls-policies");
        anonymousList.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The unfiltered query still returns every seeded row, proving no policy took effect.
        var rows = await QueryObjectIdsByCategoryAsync(claimCategory: null);
        rows.Should().HaveCount(TestFeatureCount);
    }

    private async Task<Guid> CreatePolicyAsync(HttpClient adminClient, string layer = "*")
    {
        var request = new CreateRlsPolicyRequest
        {
            Role = "*",
            Service = "*",
            Layer = layer,
            Attribute = "category",
            ClaimType = "category",
            Comparison = "in",
            Description = "RLS test: restrict by category claim",
        };

        using var content2 = JsonContent.Create(request, RlsPolicyJsonContext.Default.CreateRlsPolicyRequest);
        var response = await adminClient.PostAsync(
            "/api/v1/admin/rls-policies",
            content2);

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

    private async Task<IReadOnlyList<(long ObjectId, string? Category)>> QueryObjectIdsByCategoryAsync(string? claimCategory)
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
            .Select(feature =>
            {
                var attributes = feature.GetProperty("attributes");
                var objectId = attributes.GetProperty("objectid").GetInt64();
                var category = attributes.GetProperty("category").GetString();
                return (objectId, category);
            })
            .ToList();
    }

    /// <summary>
    /// Issues an edit/delete for the hidden objectId as the RLS-constrained caller and reports
    /// whether the mutation SUCCEEDED. A successful per-row result on a 200 response is the bypass;
    /// any non-success status (write-RBAC rejection) or a per-row failure is a correct block.
    /// </summary>
    private async Task<bool> EditHiddenRowSucceededAsync(
        string claimCategory,
        string path,
        IEnumerable<KeyValuePair<string, string>> form,
        string resultsProperty)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "rls-user");
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.CategoryHeader, claimCategory);

        var body = new List<KeyValuePair<string, string>>(form) { new("f", "json") };
        using var content = new FormUrlEncodedContent(body);
        var response = await client.PostAsync(path, content);
        var payload = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.OK)
        {
            // Write-RBAC rejected the request entirely; the row was never touched.
            return false;
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty(resultsProperty, out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return false;
        }

        return results[0].TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.True;
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
