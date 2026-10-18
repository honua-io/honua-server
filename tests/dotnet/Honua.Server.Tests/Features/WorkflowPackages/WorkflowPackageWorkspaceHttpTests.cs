// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>Actual HTTP and API-key authentication against the legacy package routes.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class WorkflowPackageWorkspaceHttpTests
{
    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /api/v1/console/workflow-packages")]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    [Endpoint("GET /api/v1/console/workflow-packages/{packageId}")]
    [Endpoint("PUT /api/v1/console/workflow-packages/{packageId}")]
    [Endpoint("GET /api/v1/console/workflow-packages/{packageId}/versions")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions")]
    [Endpoint("GET /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    [Endpoint("GET /api/v1/console/workflow-publications")]
    [Endpoint("DELETE /api/v1/console/workflow-publications/{publicationId}")]
    [Endpoint("POST /api/v1/console/workflow-publications/{publicationId}/status")]
    [Endpoint("POST /api/v1/console/workflow-publications/{publicationId}/runs")]
    public async Task ScopedAdminAgents_CannotUseLegacyRoutesToReadReplaceVersionOrPublishAnotherOwnersPackage()
    {
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();
        try
        {
            using var admin = fixture.CreateAdminClient();
            var keyA = await Key(admin, "workflow-owner");
            var keyB = await Key(admin, "workflow-foreign");
            using var owner = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", keyA.Material));
            using var other = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", keyB.Material));
            var id = "http-workflow-" + Guid.NewGuid().ToString("N");
            var graph = new WorkflowGraph { Nodes = [] };
            var created = await Read(await owner.PostAsJsonAsync("/api/v1/console/workflow-packages", new
            {
                packageId = id, name = "Owned draft", graph,
                metadata = new Dictionary<string, string> { ["ownerActorId"] = "forged-owner", ["tenantId"] = "forged-tenant" },
            }), HttpStatusCode.Created);
            var ownership = created.GetProperty("data").GetProperty("ownership");
            ownership.GetProperty("ownerActorId").GetString().Should().Be($"apikey:api-key:{keyA.Id}");
            ownership.GetProperty("tenantId").GetString().Should().Be("public");
            await using var fixtureScope = fixture.Services.CreateAsyncScope();
            var store = fixtureScope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>();
            var hash = WorkflowPackageGraphHasher.Compute(graph);
            // Trusted fixture setup supplies an immutable snapshot. This does not claim GP validation/execution.
            await store.CreateVersionAsync(id, hash, graph, WorkflowPackageValidationResult.Success(hash), "http-fixture");
            var publication = "http-publication-" + Guid.NewGuid().ToString("N");
            await store.SavePublicationAsync(new WorkflowPublication
            {
                PublicationId = publication, PackageId = id, PackageVersion = 1, PackageHash = hash,
                Target = WorkflowPublicationTarget.Job, Status = WorkflowPublicationStatus.Active,
                Eligibility = WorkflowPackageValidationResult.Success(hash), CreatedAt = DateTimeOffset.UtcNow,
            });
            string root = $"/api/v1/console/workflow-packages/{id}";
            (await owner.GetAsync(root)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await other.GetAsync(root)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.GetAsync(root + "/versions/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var versions = await Read(await other.GetAsync(root + "/versions"), HttpStatusCode.OK);
            versions.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(0);
            var list = await Read(await other.GetAsync("/api/v1/console/workflow-packages"), HttpStatusCode.OK);
            list.GetProperty("data").GetProperty("items").EnumerateArray()
                .Should().NotContain(item => item.GetProperty("packageId").GetString() == id);
            var replacement = new { packageId = id, name = "Foreign replacement", graph };
            (await other.PostAsJsonAsync("/api/v1/console/workflow-packages", replacement)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.PutAsJsonAsync(root, replacement)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.PostAsync(root + "/versions", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.PostAsJsonAsync(root + "/versions/1/publish", new { target = "Job" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.DeleteAsync($"/api/v1/console/workflow-publications/{publication}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.PostAsJsonAsync($"/api/v1/console/workflow-publications/{publication}/status", new { status = "Disabled" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await other.PostAsJsonAsync($"/api/v1/console/workflow-publications/{publication}/runs", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var publications = await Read(await other.GetAsync("/api/v1/console/workflow-publications"), HttpStatusCode.OK);
            publications.GetProperty("data").GetProperty("items").EnumerateArray()
                .Should().NotContain(item => item.GetProperty("publicationId").GetString() == publication);
            // Owning an artifact does not grant the agent a legacy direct-publication exemption.
            (await owner.PostAsJsonAsync(root + "/versions/1/publish", new { target = "Job" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await owner.DeleteAsync($"/api/v1/console/workflow-publications/{publication}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await owner.PostAsJsonAsync($"/api/v1/console/workflow-publications/{publication}/status", new { status = "Disabled" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await store.GetPackageAsync(id))!.Name.Should().Be("Owned draft");
            (await store.GetPublicationAsync(publication))!.Status.Should().Be(WorkflowPublicationStatus.Active);
            var package = (await store.GetPackageAsync(id))!;
            var tenantId = id + "-tenant";
            await store.SavePackageAsync(package with
            {
                PackageId = tenantId, Ownership = package.Ownership! with { TenantId = "another-tenant" },
            });
            (await owner.GetAsync($"/api/v1/console/workflow-packages/{tenantId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await admin.PostAsync($"/api/v1/admin/api-keys/{keyA.Id}/revoke", null)).EnsureSuccessStatusCode();
            (await owner.GetAsync(root)).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<(string Id, string Material)> Key(HttpClient admin, string name)
    {
        var body = await Read(await admin.PostAsJsonAsync("/api/v1/admin/api-keys", new
        {
            name, permissions = new[] { "admin:read", "admin:write", "read:*", "agent:workspace" },
        }), HttpStatusCode.Created);
        return (body.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!,
            body.GetProperty("data").GetProperty("key").GetString()!);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            response.StatusCode.Should().Be(status);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.Clone();
        }
    }
}
