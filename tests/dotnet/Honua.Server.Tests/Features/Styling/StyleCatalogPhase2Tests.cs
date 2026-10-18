// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Styling;

/// <summary>
/// Integration tests for the ADR-0048 Phase 2 (#1389) independent style catalog: the
/// styleId-keyed store and its many-to-many associations, the <c>Type=Style</c> graph
/// producer lighting up <c>StyleResourceIds</c> with real data, and one-style-many
/// resource reuse.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiStyles)]
public sealed class StyleCatalogPhase2Tests : IAsyncLifetime
{
    private const string MapboxStyleMediaType = "application/vnd.mapbox.style+json";

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task UpdatingLayerStyle_PopulatesStyleResourceIdsWithTypeStyleResource()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);

        using var scope = _fixture.Services.CreateScope();

        // The independent catalog now has the layer's default style.
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var styles = await catalog.GetStylesForLayerAsync(WebAppFixture.TestLayerId);
        styles.Should().NotBeEmpty();
        var styleId = styles[0].StyleId;
        styleId.Should().Be($"style-layer-{WebAppFixture.TestLayerId}");

        // The canonical graph references it via StyleResourceIds → a real Type=Style resource.
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();

        snapshot.Index.ResourcesByStorageLayerId.TryGetValue(WebAppFixture.TestLayerId, out var dataResource)
            .Should().BeTrue();
        dataResource!.StyleResourceIds.Should().NotBeEmpty();

        var styleResourceId = dataResource.StyleResourceIds[0];
        snapshot.Index.ResourcesById.TryGetValue(styleResourceId, out var styleResource).Should().BeTrue();
        styleResource!.Type.Should().Be(MetadataV2ResourceType.Style);
        styleResource.Style.Should().NotBeNull();
        styleResource.Style!.Encodings.Should().Contain(e => e.Encoding == "mapbox-style");

        // The graph stays valid with the populated StyleResourceIds.
        MetadataV2GraphValidator.Validate(snapshot.Graph).IsValid.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task OneCatalogStyle_CanBeReferencedByManyLayers()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);

        using var scope = _fixture.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();

        var styleId = $"shared-{Guid.NewGuid():N}";
        var created = await catalog.CreateStyleAsync(styleId, BuildDefaultStyleJson(), title: "Shared");
        created.Should().NotBeNull();

        // Associate the SAME style with the test layer (one style -> many layers).
        var associated = await catalog.AssociateLayerAsync(WebAppFixture.TestLayerId, styleId, ordinal: 1);
        associated.Should().BeTrue();

        var forLayer = await catalog.GetStylesForLayerAsync(WebAppFixture.TestLayerId);
        forLayer.Select(s => s.StyleId).Should().Contain(styleId);

        // The shared style is one record; the association table records the reuse.
        var associations = await catalog.ListAssociationsAsync();
        associations.Count(a => a.StyleId == styleId).Should().BeGreaterThanOrEqualTo(1);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task UpdateCatalogStyle_UpdatesOnlyAnExistingRecord()
    {
        using var scope = _fixture.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();

        var missing = await catalog.UpdateStyleAsync(
            $"missing-{Guid.NewGuid():N}",
            BuildDefaultStyleJson());
        missing.Should().BeNull();

        var styleId = $"update-{Guid.NewGuid():N}";
        var created = await catalog.CreateStyleAsync(styleId, BuildDefaultStyleJson(), title: "Original");
        created.Should().NotBeNull();

        var updated = await catalog.UpdateStyleAsync(styleId, BuildDefaultStyleJson(), title: "Updated");

        updated.Should().NotBeNull();
        updated!.Title.Should().Be("Updated");
        updated.StyleVersion.Should().Be(created!.StyleVersion + 1);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task ConditionalSnapshot_TwoIndependentScopes_SerializeAndRejectStaleRevision()
    {
        await using var firstScope = _fixture.Services.CreateAsyncScope();
        await using var secondScope = _fixture.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var second = secondScope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        first.Should().NotBeSameAs(second);
        var id = "snapshot-race-" + Guid.NewGuid().ToString("N");
        await first.CreateStyleAsync(id, BuildDefaultStyleJson());
        var snapshot = await first.GetEditSnapshotAsync(id, null);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revalidated = 0;
        var winner = first.ApplyEditIfSnapshotAsync(snapshot, BuildDefaultStyleJson(), "{}", "first", async token =>
        {
            locked.SetResult();
            await release.Task.WaitAsync(token);
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Honua.Core.Features.Styling.Domain.StyleEditStoreSnapshot?> loser;
        try
        {
            loser = second.ApplyEditIfSnapshotAsync(snapshot, BuildDefaultStyleJson(), "{}", "second", _ =>
            { Interlocked.Increment(ref revalidated); return Task.CompletedTask; });
            using var bounded = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            // A separate legacy writer must also wait on the same canonical row lock.
            var legacy = () => second.UpdateStyleAsync(id, BuildDefaultStyleJson(), cancellationToken: bounded.Token);
            await legacy.Should().ThrowAsync<OperationCanceledException>();
            loser.IsCompleted.Should().BeFalse();
        }
        finally { release.TrySetResult(); }
        (await winner)!.CatalogStyle!.StyleVersion.Should().Be(snapshot.CatalogStyle!.StyleVersion + 1);
        (await loser).Should().BeNull();
        revalidated.Should().Be(0, "stale contenders cannot execute a revalidation or mutation callback");
        (await first.GetStyleAsync(id))!.StyleVersion.Should().Be(snapshot.CatalogStyle.StyleVersion + 1);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task ConditionalSnapshot_RevalidationFailureRollsBackBothLayerAndCatalog()
    {
        using var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var id = $"style-layer-{WebAppFixture.TestLayerId}";
        var before = await catalog.GetEditSnapshotAsync(id, WebAppFixture.TestLayerId);
        var mutate = () => catalog.ApplyEditIfSnapshotAsync(before, BuildDefaultStyleJson(), "{}", "revoked-agent",
            _ => throw new InvalidOperationException("authority revoked after lock wait"));
        await mutate.Should().ThrowAsync<InvalidOperationException>();
        var after = await catalog.GetEditSnapshotAsync(id, WebAppFixture.TestLayerId);
        after.CatalogStyle.Should().BeEquivalentTo(before.CatalogStyle);
        after.LayerStyle.Should().BeEquivalentTo(before.LayerStyle);
        after.Associations.Should().BeEquivalentTo(before.Associations);
        // Either independently versioned mirror changing invalidates the coupled snapshot.
        await catalog.UpdateStyleAsync(id, BuildDefaultStyleJson());
        (await catalog.ApplyEditIfSnapshotAsync(before, BuildDefaultStyleJson(), "{}", "stale", _ => Task.CompletedTask)).Should().BeNull();
        var layer = await catalog.GetEditSnapshotAsync(id, WebAppFixture.TestLayerId);
        layer.LayerStyle.Should().BeEquivalentTo(before.LayerStyle);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task MutationReceipt_CommittedResponseFailure_ReconcilesAcrossScopesWithoutAnotherWrite()
    {
        await using var firstScope = _fixture.Services.CreateAsyncScope();
        await using var secondScope = _fixture.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var second = secondScope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var id = "receipt-" + Guid.NewGuid().ToString("N");
        var style = BuildDefaultStyleJson();
        await first.CreateStyleAsync(id, style);
        var snapshot = await first.GetEditSnapshotAsync(id, null);
        using var parsed = JsonDocument.Parse(style);
        var canonical = Honua.Core.Features.Collaboration.Operations.SavedMapOperationIdempotency.CanonicalPayload(parsed.RootElement);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
        var identity = new Honua.Core.Features.Styling.Domain.StyleMutationIdentity(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), new string('a', 64),
            "agent:receipt-test", "public", "honua", "target", id, snapshot.CatalogStyle!.StyleVersion,
            hash, new string('b', 64), hash);
        foreach (var malformed in new[]
        {
            identity with { ExpectedVersion = identity.ExpectedVersion + 1 },
            identity with { ActorId = "misattributed" },
            identity with { SchemaName = "another_schema" },
            identity with { ExpectedContentHash = new string('c', 64) },
        })
        {
            var attempt = () => first.ApplyEditOnceAsync(snapshot, style, "{}", identity.ActorId, malformed,
                _ => throw new InvalidOperationException("identity must fail before callback"), _ => Task.CompletedTask);
            await attempt.Should().ThrowAsync<ArgumentException>();
            (await first.GetMutationReceiptAsync(malformed)).Should().BeNull();
            (await first.GetStyleAsync(id))!.StyleVersion.Should().Be(snapshot.CatalogStyle.StyleVersion);
        }
        var mutationCalls = 0;
        async Task LoseResponseAfterActualCommit()
        {
            var result = await first.ApplyEditOnceAsync(snapshot, style, "{}", identity.ActorId, identity,
                _ => { mutationCalls++; return Task.CompletedTask; }, _ => Task.CompletedTask);
            result.Should().NotBeNull();
            throw new IOException("lost response after SQL commit");
        }
        await ((Func<Task>)LoseResponseAfterActualCommit).Should().ThrowAsync<IOException>();
        var stored = await second.GetMutationReceiptAsync(identity);
        stored.Should().NotBeNull();
        var authorityChecks = 0;
        var retries = Enumerable.Range(0, 2).Select(_ => second.ApplyEditOnceAsync(snapshot, style, "{}", identity.ActorId, identity,
            _ => throw new InvalidOperationException("a retry cannot mutate"),
            _ => { Interlocked.Increment(ref authorityChecks); return Task.CompletedTask; }));
        var outcomes = await Task.WhenAll(retries);
        outcomes.Should().OnlyContain(outcome => outcome != null && outcome.UpdatedSnapshot == null && outcome.Receipt == stored);
        mutationCalls.Should().Be(1);
        authorityChecks.Should().Be(2);
        (await second.GetStyleAsync(id))!.StyleVersion.Should().Be(snapshot.CatalogStyle.StyleVersion + 1);
        var revoked = () => second.ApplyEditOnceAsync(snapshot, style, "{}", identity.ActorId, identity,
            _ => Task.CompletedTask, _ => throw new UnauthorizedAccessException());
        await revoked.Should().ThrowAsync<UnauthorizedAccessException>();
        var conflict = () => second.GetMutationReceiptAsync(identity with { ActorId = "different-actor" });
        await conflict.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task SeedTestLayerStyleAsync(HttpClient adminClient)
    {
        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.Deserialize<JsonElement>(BuildDefaultStyleJson())
        };

        using var content = JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest);
        using var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            content);
        response.Be200Ok();
    }

    private static string BuildDefaultStyleJson()
    {
        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        return JsonSerializer.Serialize(style);
    }
}
