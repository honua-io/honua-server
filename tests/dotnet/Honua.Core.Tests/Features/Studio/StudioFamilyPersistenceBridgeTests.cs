// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Studio.Services.Bridging;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Unit tests for the ADR-0069 family persistence bridge composition (honua-server#3004):
/// deterministic id derivation, family-registry descriptor overrides, and
/// <see cref="BridgedStudioPackageStore"/> routing/merging semantics.
/// </summary>
public sealed class StudioFamilyPersistenceBridgeTests
{
    [UnitTest]
    public void BridgeIds_DeriveIsDeterministicAndNamespaced()
    {
        var first = StudioBridgeIds.Derive("form", "inspection-form");
        var second = StudioBridgeIds.Derive("form", "inspection-form");
        var otherNamespace = StudioBridgeIds.Derive("analysis", "inspection-form");
        var otherId = StudioBridgeIds.Derive("form", "inspection-form-2");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherNamespace);
        Assert.NotEqual(first, otherId);
        Assert.NotEqual(Guid.Empty, first);
    }

    [UnitTest]
    public void BridgeIds_NativeGuidIdsMapDirectly()
    {
        var native = Guid.NewGuid();
        Assert.Equal(native, StudioBridgeIds.ForNativeId("form", native.ToString("D")));
        Assert.NotEqual(native, StudioBridgeIds.ForNativeId("form", $"prefix-{native:N}"));
    }

    [UnitTest]
    public void FamilyRegistry_BridgedFamilyAdvertisesNativeFormatOperationsAndLimitations()
    {
        var bridge = new StubBridge(StudioPackageFamily.Form)
        {
            FormatValue = "honua.form-package.v1",
            PublishSupportedValue = true,
            OperationsValue = [StudioPackageOperation.DraftCreate, StudioPackageOperation.ContentVersionCreate],
            LimitationsValue = ["bridged to the native forms package store"],
        };
        var registry = new StudioPackageFamilyRegistry(
            new InMemoryStudioPackageStore(),
            new StudioFamilyPersistenceBridgeCatalog([bridge]));

        var capabilities = registry.GetCapabilities();
        Assert.Equal(10, capabilities.Families.Count);

        var form = Assert.Single(capabilities.Families, descriptor => descriptor.Family == StudioPackageFamily.Form);
        Assert.Equal("honua.form-package.v1", form.Format);
        Assert.True(form.PublishSupported);
        Assert.Equal(bridge.OperationsValue, form.SupportedOperations);
        Assert.Contains("bridged to the native forms package store", form.Limitations);

        // Non-bridged families are untouched.
        var map = Assert.Single(capabilities.Families, descriptor => descriptor.Family == StudioPackageFamily.Map);
        Assert.Equal("honua_map_package.v1", map.Format);
    }

    [UnitTest]
    public async Task BridgedStore_RoutesVersionSaveAndPointersToBridge()
    {
        var inner = new InMemoryStudioPackageStore();
        var bridge = new StubBridge(StudioPackageFamily.Form);
        var store = new BridgedStudioPackageStore(inner, [bridge]);

        var draft = BuildDraft(StudioPackageFamily.Form);
        await store.CreateDraftAsync(draft);

        var version = await store.CreateVersionAsync(draft, "note", "tester");
        Assert.Equal(1, version.VersionNumber);
        Assert.Single(bridge.SavedDrafts);

        var pointers = await store.GetPointersAsync(draft.ItemId);
        Assert.NotNull(pointers);
        Assert.Equal(version.VersionId, pointers!.CurrentVersionId);

        var versions = await store.ListVersionsAsync(draft.ItemId);
        Assert.Single(versions);

        var fetched = await store.GetVersionAsync(draft.ItemId, version.VersionId);
        Assert.NotNull(fetched);

        // Non-bridged families keep flowing to the inner store.
        var queryDraft = BuildDraft(StudioPackageFamily.Query);
        await store.CreateDraftAsync(queryDraft);
        var queryVersion = await store.CreateVersionAsync(queryDraft, null, "tester");
        Assert.DoesNotContain(bridge.SavedDrafts, saved => saved.DraftId == queryDraft.DraftId);
        Assert.NotNull(await inner.GetVersionAsync(queryDraft.ItemId, queryVersion.VersionId));
    }

    [UnitTest]
    public async Task BridgedStore_MergesBridgedItemsIntoEnumerationAndScopesByOwner()
    {
        var inner = new InMemoryStudioPackageStore();
        var bridge = new StubBridge(StudioPackageFamily.Form);
        var store = new BridgedStudioPackageStore(inner, [bridge]);

        // One native (inner) query item.
        var queryDraft = BuildDraft(StudioPackageFamily.Query);
        await store.CreateDraftAsync(queryDraft);
        await store.CreateVersionAsync(queryDraft, null, "tester");

        // One bridged form item (owner-less, mirroring the native forms surface).
        var formDraft = BuildDraft(StudioPackageFamily.Form);
        await store.CreateDraftAsync(formDraft);
        await store.CreateVersionAsync(formDraft, null, "tester");

        var all = await store.ListContentItemsAsync(new StudioContentItemQuery { Limit = 10 });
        Assert.Equal(2, all.Total);
        Assert.Contains(all.Items, item => item.Family == StudioPackageFamily.Form);
        Assert.Contains(all.Items, item => item.Family == StudioPackageFamily.Query);

        var formsOnly = await store.ListContentItemsAsync(new StudioContentItemQuery
        {
            Families = [StudioPackageFamily.Form],
            Limit = 10,
        });
        Assert.Equal(1, formsOnly.Total);
        Assert.Single(formsOnly.Items);

        // Owner scoping fails closed for owner-less bridged items (#3018 model).
        var scoped = await store.ListContentItemsAsync(new StudioContentItemQuery
        {
            OwnerId = "somebody",
            Families = [StudioPackageFamily.Form],
            Limit = 10,
        });
        Assert.Empty(scoped.Items);
        Assert.Equal(0, scoped.Total);
    }

    [UnitTest]
    public async Task BridgedStore_RollbackAndUnsupportedPublishSurfaceInvalidOperation()
    {
        var inner = new InMemoryStudioPackageStore();
        var bridge = new StubBridge(StudioPackageFamily.Analysis) { PublishSupportedValue = false };
        var store = new BridgedStudioPackageStore(inner, [bridge]);

        var draft = BuildDraft(StudioPackageFamily.Analysis);
        await store.CreateDraftAsync(draft);
        var version = await store.CreateVersionAsync(draft, null, "tester");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RollbackAsync(
            draft.ItemId,
            version.VersionId,
            StudioRollbackPointer.Current,
            "tester",
            reason: null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreatePublicationRequestAsync(new StudioPublicationRequest
        {
            RequestId = Guid.NewGuid(),
            ItemId = draft.ItemId,
            VersionId = version.VersionId,
            Status = StudioPublicationRequestStatus.Accepted,
            CreatedAt = DateTimeOffset.UtcNow,
        }));
    }

    private static StudioPackageDraft BuildDraft(StudioPackageFamily family)
    {
        using var body = JsonDocument.Parse("""{"title":"t"}""");
        var now = DateTimeOffset.UtcNow;
        return new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            PackageKey = $"pkg-{Guid.NewGuid():N}",
            Family = family,
            Envelope = new StudioPackageEnvelope
            {
                Family = family,
                SchemaVersion = "1.0",
                Format = "test-format",
                Body = body.RootElement.Clone(),
            },
            Generation = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// In-memory <see cref="IStudioFamilyPersistenceBridge"/> stub emulating a native store:
    /// versions appended through <see cref="SaveVersionAsync"/> resolve through pointers,
    /// listing, and item summaries; publish honors <see cref="PublishSupportedValue"/>.
    /// </summary>
    private sealed class StubBridge : IStudioFamilyPersistenceBridge
    {
        private readonly Dictionary<Guid, List<StudioContentVersion>> _versionsByItem = new();

        public StubBridge(StudioPackageFamily family) => Family = family;

        public List<StudioPackageDraft> SavedDrafts { get; } = [];

        public string FormatValue { get; init; } = "native-format.v1";

        public bool PublishSupportedValue { get; init; } = true;

        public IReadOnlyList<StudioPackageOperation> OperationsValue { get; init; } =
            [StudioPackageOperation.ContentVersionCreate, StudioPackageOperation.ContentVersionRead];

        public IReadOnlyList<string> LimitationsValue { get; init; } = [];

        public StudioPackageFamily Family { get; }

        public string Format => FormatValue;

        public bool PublishSupported => PublishSupportedValue;

        public IReadOnlyList<StudioPackageOperation> SupportedOperations => OperationsValue;

        public IReadOnlyList<string> Limitations => LimitationsValue;

        public Task<IReadOnlyList<StudioContentItemSummary>> ListItemSummariesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<StudioContentItemSummary> summaries = _versionsByItem
                .Select(pair => new StudioContentItemSummary
                {
                    ItemId = pair.Key,
                    PackageKey = pair.Value[^1].PackageKey,
                    Family = Family,
                    State = StudioContentItemState.Current,
                    CurrentVersionId = pair.Value[^1].VersionId,
                    OwnerId = null,
                    CreatedAt = pair.Value[0].CreatedAt,
                    UpdatedAt = pair.Value[^1].CreatedAt,
                })
                .ToArray();
            return Task.FromResult(summaries);
        }

        public Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default)
            => Task.FromResult(_versionsByItem.TryGetValue(itemId, out var versions)
                ? new StudioContentItemPointers
                {
                    ItemId = itemId,
                    CurrentVersionId = versions[^1].VersionId,
                    PublishedVersionId = null,
                    OwnerId = null,
                }
                : null);

        public Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StudioContentVersion>>(
                _versionsByItem.TryGetValue(itemId, out var versions) ? versions.ToArray() : Array.Empty<StudioContentVersion>());

        public Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_versionsByItem.TryGetValue(itemId, out var versions)
                ? versions.FirstOrDefault(version => version.VersionId == versionId)
                : null);

        public Task<StudioContentVersion> SaveVersionAsync(
            StudioPackageDraft draft,
            string? changeNote,
            string? actorId,
            CancellationToken cancellationToken = default)
        {
            SavedDrafts.Add(draft);
            if (!_versionsByItem.TryGetValue(draft.ItemId, out var versions))
            {
                versions = [];
                _versionsByItem[draft.ItemId] = versions;
            }

            var version = new StudioContentVersion
            {
                ItemId = draft.ItemId,
                PackageKey = draft.PackageKey,
                VersionId = Guid.NewGuid(),
                VersionNumber = versions.Count + 1,
                ContentHash = Guid.NewGuid().ToString("N"),
                Envelope = draft.Envelope,
                Validation = StudioValidationSummary.NotValidated,
                ChangeNote = changeNote,
                CreatedBy = actorId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            versions.Add(version);
            return Task.FromResult(version);
        }

        public Task<StudioPublicationRequest> PublishAsync(StudioPublicationRequest request, CancellationToken cancellationToken = default)
            => PublishSupported
                ? Task.FromResult(request with { Status = StudioPublicationRequestStatus.Accepted })
                : throw new InvalidOperationException("Publish-request is not supported for this bridged family.");
    }
}
