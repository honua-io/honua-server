// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class FolderEndpointsTests : IAsyncLifetime
{
    private readonly FacetAuthorization _authorization = new();
    private readonly WebAppFixture _fixture;

    public FolderEndpointsTests()
    {
        _fixture = new WebAppFixture();
        _fixture.ConfigureWebHost(b =>
            {
                b.UseEnvironment("Test");
                b.UseSetting("HONUA_DEV_AUTH", "false");
                b.UseSetting("HONUA_ADMIN_PASSWORD", "folder-test-key");
            })
            .ConfigureServices(services =>
            {
                FolderPostgresTestHost.Configure(services, () => _fixture.CurrentSchema);
                services.AddSingleton<IOperatorAuthorizationEvaluator>(_authorization);
            });
    }

    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await FolderPostgresTestHost.InstallSchemaAsync(_fixture);
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "folder-test-key"));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/folders")]
    [Endpoint("POST /api/v1/admin/folders")]
    [Endpoint("PATCH /api/v1/admin/folders/{id}")]
    [Endpoint("DELETE /api/v1/admin/folders/{id}")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/folder")]
    public async Task FolderLifecycle_WithCurrentEtag_MovesAllFacetsAndPreservesAliases()
    {
        var listing = await _client.GetAsync("/api/v1/admin/folders");
        listing.StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = listing.Headers.ETag!.ToString();
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", stale);
        var created = await _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "Survey" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = payload.RootElement.GetProperty("data").GetProperty("metadata").GetProperty("id").GetString();
        var staleMove = await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = id });
        staleMove.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        SetEtag(created);
        var moved = await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = id });
        moved.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertMembership(id);
        SetEtag(moved);
        var nonempty = await _client.DeleteAsync($"/api/v1/admin/folders/{id}");
        nonempty.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var renamed = await _client.PatchAsJsonAsync($"/api/v1/admin/folders/{id}", new { name = "Survey2", title = "Field survey" });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        using var renamedBody = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync());
        renamedBody.RootElement.GetProperty("data").GetProperty("previousNames")[0].GetString().Should().Be("Survey");
        SetEtag(renamed);
        var root = await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = (string?)null });
        root.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertMembership(null);
        SetEtag(root);
        (await _client.DeleteAsync($"/api/v1/admin/folders/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/folders")]
    public async Task CreateFolder_WithoutPrecondition_Returns428()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "Survey" });
        ((int)response.StatusCode).Should().Be(428);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/folder")]
    public async Task MoveService_WhenLogicalServiceDenied_DoesNotSaveEvenForCurrentMembership()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var original = await store.GetCurrentAsync();
        var facets = original.Graph.Services.Where(s => s.Metadata.Name == "test").ToArray();
        facets.Length.Should().BeGreaterThan(1);
        _authorization.DeniedService = facets[^1].Metadata.Name;
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", original.Etag);
        var response = await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await store.GetCurrentAsync()).Etag.Should().Be(original.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/folders")]
    public async Task CreateFolder_WithConcurrentSnapshotEtag_OnlyOneWriteCommits()
    {
        var listing = await _client.GetAsync("/api/v1/admin/folders");
        SetEtag(listing);
        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "SurveyA" }),
            _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "SurveyB" }));
        responses.Select(r => r.StatusCode).Should().BeEquivalentTo(
            new[] { HttpStatusCode.Created, HttpStatusCode.PreconditionFailed });
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/folders")]
    [Endpoint("POST /api/v1/admin/folders")]
    public async Task FolderScope_OmittedOrSuppliedNamespace_UsesPublicationOwnership()
    {
        SetEtag(await _client.GetAsync("/api/v1/admin/folders"));
        var unscoped = await _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "Shared" });
        unscoped.StatusCode.Should().Be(HttpStatusCode.Created);
        using var unscopedBody = JsonDocument.Parse(await unscoped.Content.ReadAsStringAsync());
        unscopedBody.RootElement.GetProperty("data").GetProperty("metadata").GetProperty("tenant")
            .ValueKind.Should().Be(JsonValueKind.Null);
        SetEtag(unscoped);
        var scoped = await _client.PostAsJsonAsync("/api/v1/admin/folders?namespace=workspace", new { name = "Owned" });
        scoped.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scopedBody = JsonDocument.Parse(await scoped.Content.ReadAsStringAsync());
        scopedBody.RootElement.GetProperty("data").GetProperty("metadata").GetProperty("tenant")
            .GetString().Should().Be("public");
        var listing = await _client.GetStringAsync("/api/v1/admin/folders");
        listing.Should().Contain("Shared").And.NotContain("Owned");
        var scopedListing = await _client.GetStringAsync("/api/v1/admin/folders?namespace=workspace");
        scopedListing.Should().Contain("Owned").And.NotContain("Shared");
    }

    private sealed class FacetAuthorization : IOperatorAuthorizationEvaluator
    {
        public string? DeniedService { get; set; }

        public Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(request.ResourceType == OperatorResourceType.PublishedService && request.ResourceId == DeniedService
                ? AccessDecision.Forbidden("Facet is not authorized.") : AccessDecision.Allowed());
    }

    private async Task AssertMembership(string? folderId)
    {
        using var scope = _fixture.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().GetCurrentAsync();
        var facets = snapshot.Graph.Services.Where(service => service.Metadata.Name == "test").ToArray();
        facets.Length.Should().BeGreaterThan(1);
        facets.Should().OnlyContain(service => service.FolderId == folderId);
    }

    private void SetEtag(HttpResponseMessage response)
    {
        _client.DefaultRequestHeaders.Remove("If-Match");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", response.Headers.ETag!.ToString());
    }
}
