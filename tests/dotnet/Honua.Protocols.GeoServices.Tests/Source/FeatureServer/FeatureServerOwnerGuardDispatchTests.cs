// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// The GeoServices handler must carry each authorized row's stored owner into the actual
/// writer batch. The provider uses that value in its mutation statement to reject a
/// later ownership transfer; a pre-read authorization alone cannot close the race.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerOwnerGuardDispatchTests : IAsyncLifetime
{
    private readonly List<FeatureEditBatch> _written = [];
    private readonly WebAppFixture _fixture;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;
    private string _aliceSubject = null!;

    public FeatureServerOwnerGuardDispatchTests()
    {
        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .DecorateService<IFeatureWriter>(inner => new RecordingWriter(inner, _written));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities("test", ["Query", "Create", "Update", "Delete"]);
        _fixture.UpdateV2ServiceMetadata("test", capabilities: ["Query", "Create", "Update", "Delete"]);
        _fixture.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "owner",
            Type = MetadataV2FieldType.String,
            Nullable = true,
        });

        var provider = _fixture.GetService<TestMetadataV2GraphProvider>();
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resources = snapshot.Graph.Resources.Select(resource =>
            resource.Metadata.Id == "res-layer-0"
                ? resource with
                {
                    OwnerEditPolicy = new MetadataV2OwnerEditPolicy
                    {
                        Enabled = true,
                        OwnerField = "owner",
                        StampOwnerOnInsert = true,
                    },
                }
                : resource).ToArray();
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Resources = resources,
        }, schema: _fixture.CurrentSchema);

        var keyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await keyStore.CreateAsync("owner-guard-alice",
            ["read:test", "write:test"], null, null, CancellationToken.None);
        var bobKey = await keyStore.CreateAsync("owner-guard-bob",
            ["read:test", "write:test"], null, null, CancellationToken.None);
        _aliceSubject = aliceKey.Record.Id.ToString();
        _alice = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        _bob = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));
    }

    public async Task DisposeAsync()
    {
        _alice?.Dispose();
        _bob?.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task PartialUpdateAndDelete_DispatchOnlyAcceptedOwnerGuards()
    {
        var aliceId = await AddFeatureAsync(_alice, "alice-original");
        var bobId = await AddFeatureAsync(_bob, "bob-original");
        _written.Clear();

        var update = await ApplyEditsAsync(_alice, $$$"""
            {"updates":[{"attributes":{"objectid":{{{aliceId}}},"name":"alice-updated"}},
                        {"attributes":{"objectid":{{{bobId}}},"name":"bob-forged"}}],
             "rollbackOnFailure":false}
            """);
        update.UpdateResults.Should().HaveCount(2);
        update.UpdateResults![0].Success.Should().BeTrue();
        update.UpdateResults[1].Error!.Code.Should().Be(GeoServicesEditErrorCodes.NotPermitted);
        var updateBatch = _written.Should().ContainSingle().Subject;
        updateBatch.Updates.Should().ContainSingle(feature => feature.Id == aliceId);
        updateBatch.OwnerGuards.Should().ContainSingle().Which
            .Should().Be(new OwnerEditGuard(aliceId, "owner", _aliceSubject));
        _written.Clear();

        var atomic = await ApplyEditsAsync(_alice, $$$"""
            {"updates":[{"attributes":{"objectid":{{{aliceId}}},"name":"must-not-land"}},
                        {"attributes":{"objectid":{{{bobId}}},"name":"bob-forged"}}],
             "rollbackOnFailure":true}
            """);
        atomic.Success.Should().BeFalse();
        _written.Should().BeEmpty("a denied slot aborts rollbackOnFailure before dispatch");

        var delete = await ApplyEditsAsync(_alice, $$$"""
            {"deletes":[{{{aliceId}}},{{{bobId}}}],"rollbackOnFailure":false}
            """);
        delete.DeleteResults.Should().HaveCount(2);
        delete.DeleteResults![0].Success.Should().BeTrue();
        delete.DeleteResults[1].Error!.Code.Should().Be(GeoServicesEditErrorCodes.NotPermitted);
        var deleteBatch = _written.Should().ContainSingle().Subject;
        deleteBatch.Deletes.Should().ContainSingle().Which.Should().Be(aliceId);
        deleteBatch.OwnerGuards.Should().ContainSingle().Which
            .Should().Be(new OwnerEditGuard(aliceId, "owner", _aliceSubject));
    }

    private async Task<long> AddFeatureAsync(HttpClient client, string name)
    {
        var json = JsonSerializer.Serialize(new
        {
            adds = new[]
            {
                new
                {
                    attributes = new { name },
                    geometry = new { x = -122.4194, y = 37.7749, spatialReference = new { wkid = 4326 } }
                }
            }
        });
        var result = await ApplyEditsAsync(client, json);
        return result.AddResults.Should().ContainSingle(add => add.Success).Subject.ObjectId!.Value;
    }

    private async Task<ApplyEditsResponse> ApplyEditsAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/rest/services/test/FeatureServer/0/applyEdits", content);
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        return JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
            ?? throw new InvalidOperationException($"Expected applyEdits response: {body}");
    }

    private sealed class RecordingWriter(IFeatureWriter inner, List<FeatureEditBatch> written) : IFeatureWriter
    {
        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.CreateAsync(layerId, feature, cancellationToken);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.UpdateAsync(layerId, feature, cancellationToken);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(layerId, featureId, cancellationToken);

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId, FeatureEditBatch batch, CancellationToken cancellationToken = default)
        {
            written.Add(batch);
            return inner.ApplyEditsAsync(layerId, batch, cancellationToken);
        }
    }
}
