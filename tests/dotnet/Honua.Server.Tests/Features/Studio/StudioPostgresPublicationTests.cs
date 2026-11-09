// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Server.Features.Operations;
using Honua.Server.Features.Studio.Models;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Studio;

[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioPostgresPublicationTests : IAsyncLifetime
{
    private const string ServiceId = "postgres-publication-parcels";
    private readonly AdminAuthEndpointsTests.StubHttpClientFactory _identityProvider = AdminAuthEndpointsTests.CreateStubFactory();
    private readonly WebAppFixture _fixture;

    public StudioPostgresPublicationTests()
    {
        _fixture = HumanApprovalHttpClient.Configure(new WebAppFixture(), _identityProvider)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("ConnectionStrings:redis", "");
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.UseSetting("Operations:Policy:Enabled", "true");
                builder.UseSetting("Operations:Policy:DefaultDecision", "Allow");
                builder.UseSetting("Operations:Policy:Rules:0:OperationId", StudioDraftOperations.CreatePublicationRequest);
                builder.UseSetting("Operations:Policy:Rules:0:Decision", "RequireApproval");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOperationEnvelopeFactory>();
                services.AddSingleton<IOperationEnvelopeFactory>(provider =>
                    new ScopedOperationEnvelopeFactory(provider.GetRequiredService<IServiceScopeFactory>(), useVolatileAudit: false));
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public async Task DisposeAsync()
    {
        try { await _fixture.DisposeAsync(); }
        finally { _identityProvider.Dispose(); }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("GET /api/v1/studio/published/{*route}")]
    public async Task Publish_WithoutRedis_RequiresHumanApprovalAndSurvivesHostRestart()
    {
        _fixture.GetService<IAuditLog>().IsPersisted.Should().BeTrue();
        SetSourceGraph();
        var packageKey = $"postgres-publication-{Guid.NewGuid():N}";
        var route = "/maps/" + packageKey;
        var createRequest = new CreateStudioPackageDraftRequest
        {
            PackageKey = packageKey,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "1.0",
                Format = "honua_map_package.v1",
                Body = JsonSerializer.SerializeToElement(new
                {
                    mapPackageId = packageKey,
                    format = "honua_map_package.v1",
                    status = "Ready",
                    createdAt = "2026-10-20T00:00:00Z",
                    layers = new[] { new { id = "parcels", sourceId = StudioCatalogSourceIdentity.Create(ServiceId, 7), type = "fill", visible = true } }
                })
            }
        };
        var createBody = JsonSerializer.Serialize(createRequest, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        string proposalId;
        using (var author = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider, "postgres-publisher", "public"))
        {
            using var create = await author.Client.PostAsync("/api/v1/studio/package-drafts", JsonContent(createBody));
            var draft = await ReadDataAsync(create, HttpStatusCode.Created);
            using var save = await author.Client.PostAsync(
                $"/api/v1/studio/package-drafts/{draft.GetProperty("draftId").GetGuid():D}/content-versions", JsonContent("{}"));
            var version = await ReadDataAsync(save, HttpStatusCode.Created);
            using var publish = await author.Client.PostAsync(
                $"/api/v1/studio/content-items/{version.GetProperty("itemId").GetGuid():D}/versions/{version.GetProperty("versionId").GetGuid():D}/publish-requests",
                JsonContent($$"""{"intent":{"route":"{{route}}","visibility":"public"},"warningAcknowledgement":"reviewed"}"""));
            var pending = await ReadDataAsync(publish, HttpStatusCode.Accepted);
            pending.GetProperty("status").GetString().Should().Be("RequiresApproval");
            proposalId = pending.GetProperty("proposalId").GetString()!;
        }

        var proposals = _fixture.GetService<IOperationProposalStore>();
        var pendingProposal = await proposals.GetAsync(proposalId);
        pendingProposal.Should().NotBeNull();
        pendingProposal!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        using (var anonymous = _fixture.CreateClient())
        {
            using var beforeApproval = await anonymous.GetAsync(StudioPublishedRoutes.BuildActiveUrl(route));
            beforeApproval.IsSuccessStatusCode.Should().BeFalse();
            using var approve = await anonymous.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);
            approve.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
            using var mutate = await anonymous.PostAsync("/api/v1/studio/package-drafts", JsonContent(createBody));
            mutate.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }

        await _fixture.RestartHostAsync();
        SetSourceGraph();
        var restored = await _fixture.GetService<IOperationProposalStore>().GetAsync(proposalId);
        restored.Should().BeEquivalentTo(pendingProposal);
        using (var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider, "postgres-approver", "public"))
        {
            using var approve = await human.ApproveAsync(proposalId);
            approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        }
        var approved = await _fixture.GetService<IOperationProposalStore>().GetAsync(proposalId);
        approved!.Status.Should().Be(OperationProposalStatus.Succeeded);
        approved.HumanApproval.Should().NotBeNull();

        await _fixture.RestartHostAsync();
        SetSourceGraph();
        using var reader = _fixture.CreateClient();
        using var published = await reader.GetAsync(StudioPublishedRoutes.BuildActiveUrl(route) + "?includeSources=true");
        var data = await ReadDataAsync(published, HttpStatusCode.OK);
        data.GetProperty("envelope").GetProperty("body").GetProperty("mapPackageId").GetString().Should().Be(packageKey);
        var source = data.GetProperty("sourceCatalog").EnumerateArray().Should().ContainSingle().Subject;
        source.GetProperty("id").GetString().Should().Be(StudioCatalogSourceIdentity.Create(ServiceId, 7));
        using var metadata = await reader.GetAsync(source.GetProperty("locator").GetProperty("url").GetString());
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fixture.GetService<IOperationProposalStore>().GetAsync(proposalId)).Should().BeEquivalentTo(approved);
    }

    private void SetSourceGraph()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService(ServiceId, "PostgresPublicationParcels", protocols: [ServiceProtocols.FeatureServer])
            .AddResource("postgres-publication-resource", "Parcels",
                accessPolicy: new AccessPolicy { AllowAnonymous = true, AllowedRoles = ["admin"] },
                spatial: new MetadataV2ResourceSpatial { GeometryType = MetadataV2GeometryType.GeometryCollection })
            .AddPublication("postgres-publication", ServiceId, "postgres-publication-resource", layerIndex: 7)
            .Build();
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>()).SetGraph(graph, schema: _fixture.CurrentSchema);
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }
}
