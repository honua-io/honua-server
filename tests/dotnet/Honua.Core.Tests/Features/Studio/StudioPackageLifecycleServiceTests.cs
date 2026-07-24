// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Studio;

public sealed class StudioPackageLifecycleServiceTests
{
    [UnitTest]
    public async Task SaveDraftAsVersion_ReopenAndRollback_PreservesImmutableVersions()
    {
        var service = BuildServiceProvider().GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "tester",
        });

        var version = await service.SaveDraftAsVersionAsync(draft.DraftId, "first save", "tester");
        Assert.NotNull(version);
        Assert.Equal(1, version!.VersionNumber);
        Assert.Equal(StudioPackageValidationStatus.Valid, version.Validation.Status);

        var reopened = await service.ReopenVersionAsync(version.ItemId, version.VersionId, "tester");
        Assert.NotNull(reopened);
        Assert.Equal(version.VersionId, reopened!.BaseVersionId);

        var updated = await service.UpdateDraftAsync(reopened.DraftId, new UpdateStudioPackageDraftCommand
        {
            PackageKey = reopened.PackageKey,
            WorkspaceId = reopened.WorkspaceId,
            OwnerId = reopened.OwnerId,
            Envelope = BuildEnvelope("POPULATION > 1000", "content.parcels"),
            Generation = reopened.Generation,
            ActorId = "tester",
        });
        Assert.NotNull(updated);

        var secondVersion = await service.SaveDraftAsVersionAsync(updated!.DraftId, "edited query", "tester");
        Assert.NotNull(secondVersion);
        Assert.Equal(2, secondVersion!.VersionNumber);
        Assert.NotEqual(version.ContentHash, secondVersion.ContentHash);

        var comparison = await service.CompareVersionsAsync(version.ItemId, version.VersionId, secondVersion.VersionId);
        Assert.NotNull(comparison);
        Assert.False(comparison!.ContentEqual);
        Assert.Contains("content", comparison.Changes);

        var publication = await service.CreatePublicationRequestAsync(
            version.ItemId,
            secondVersion.VersionId,
            intent: null,
            warningAcknowledgement: null,
            actorId: "tester");
        Assert.NotNull(publication);
        Assert.Equal(StudioPublicationRequestStatus.Accepted, publication!.Status);

        var rollback = await service.RollbackAsync(
            version.ItemId,
            version.VersionId,
            StudioRollbackPointer.Both,
            "tester",
            "restore first version");
        Assert.NotNull(rollback);
        Assert.Equal(version.VersionId, rollback!.Pointers.CurrentVersionId);
        Assert.Equal(version.VersionId, rollback.Pointers.PublishedVersionId);

        var original = await service.GetVersionAsync(version.ItemId, version.VersionId);
        Assert.NotNull(original);
        Assert.Equal(version.ContentHash, original!.ContentHash);
        Assert.Equal("1=1", original.Envelope.Body!.Value.GetProperty("where").GetString());
    }

    [UnitTest]
    public async Task SaveDraftAsVersion_WithOnlyValidationTimestampChange_KeepsContentHashStable()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));
        var service = BuildServiceProvider(timeProvider: timeProvider).GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "tester",
        });

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var first = await service.SaveDraftAsVersionAsync(draft.DraftId, "first save", "tester");
        Assert.NotNull(first);

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var second = await service.SaveDraftAsVersionAsync(draft.DraftId, "second save", "tester");
        Assert.NotNull(second);

        Assert.NotEqual(first!.Validation.GeneratedAt, second!.Validation.GeneratedAt);
        Assert.Equal(first.ContentHash, second.ContentHash);

        var comparison = await service.CompareVersionsAsync(draft.ItemId, first.VersionId, second.VersionId);

        Assert.NotNull(comparison);
        Assert.True(comparison!.ContentEqual);
        Assert.True(comparison.ValidationEqual);
        Assert.DoesNotContain("content", comparison.Changes);
        Assert.DoesNotContain("validation", comparison.Changes);
    }

    [UnitTest]
    public async Task CreatePublicationRequest_WithInvalidIntentOverride_ThrowsWithoutPublishingPointer()
    {
        var provider = BuildServiceProvider();
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var store = provider.GetRequiredService<IStudioPackageStore>();
        var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "tester",
        });
        var version = await service.SaveDraftAsVersionAsync(draft.DraftId, "first save", "tester");
        Assert.NotNull(version);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePublicationRequestAsync(
            version!.ItemId,
            version.VersionId,
            new StudioPublicationIntent { Route = "relative", Visibility = "world" },
            warningAcknowledgement: null,
            actorId: "tester"));

        Assert.Contains("Publication intent is invalid", exception.Message);
        Assert.Contains("route must start with '/'", exception.Message);
        Assert.Contains("visibility must be personal, team, organization, or public", exception.Message);
        var pointers = await store.GetPointersAsync(version!.ItemId);
        Assert.NotNull(pointers);
        Assert.Null(pointers!.PublishedVersionId);
    }

    [UnitTest]
    public async Task CreateDraft_WithNullCollections_CoalescesToEmptyWithoutError()
    {
        // Regression: honua-server#2351 — a client sending an explicit JSON null for
        // bindings/dependencies/provenance must be treated identically to omitting them
        // (empty), not persisted as a null collection that later triggers a spurious
        // ArgumentNullException ("source") on the otherwise-successful path.
        var provider = BuildServiceProvider();
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();

        const string envelopeJson = """
        {
          "family": 0,
          "schemaVersion": "1.0",
          "format": "studio_query_package.v1",
          "bindings": null,
          "dependencies": null,
          "provenance": null,
          "body": {"where":"1=1"}
        }
        """;
        var envelope = JsonSerializer.Deserialize(envelopeJson, StudioJsonContext.Default.StudioPackageEnvelope)!;
        Assert.Null(envelope.Bindings);
        Assert.Null(envelope.Dependencies);
        Assert.Null(envelope.Provenance);

        var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = envelope,
            ActorId = "tester",
        });

        // Null collections are coalesced to empty and never persisted as null.
        Assert.NotNull(draft.Envelope.Bindings);
        Assert.NotNull(draft.Envelope.Dependencies);
        Assert.NotNull(draft.Envelope.Provenance);
        Assert.Empty(draft.Envelope.Bindings);
        Assert.Empty(draft.Envelope.Dependencies);
        Assert.Empty(draft.Envelope.Provenance);

        // Behaves like omitted collections: no spurious "must be an array" diagnostics.
        Assert.DoesNotContain(draft.Validation.Diagnostics, d => d.Code == "studio.bindings.array");
        Assert.DoesNotContain(draft.Validation.Diagnostics, d => d.Code == "studio.dependencies.array");
        Assert.DoesNotContain(draft.Validation.Diagnostics, d => d.Code == "studio.provenance.array");
        Assert.Equal(StudioPackageValidationStatus.Valid, draft.Validation.Status);

        // The immutable version sidecar path enumerates these collections; with the null
        // coalesced away it completes without throwing.
        var version = await service.SaveDraftAsVersionAsync(draft.DraftId, "first save", "tester");
        Assert.NotNull(version);
        Assert.Empty(version!.Dependencies);
        Assert.Empty(version.Provenance);
    }

    [UnitTest]
    public async Task UpdateDraft_WithNullCollections_CoalescesToEmptyWithoutError()
    {
        var provider = BuildServiceProvider();
        var service = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "tester",
        });

        const string envelopeJson = """
        {
          "family": 0,
          "schemaVersion": "1.0",
          "format": "studio_query_package.v1",
          "bindings": null,
          "dependencies": null,
          "provenance": null,
          "body": {"where":"POPULATION > 1000"}
        }
        """;
        var envelope = JsonSerializer.Deserialize(envelopeJson, StudioJsonContext.Default.StudioPackageEnvelope)!;

        var updated = await service.UpdateDraftAsync(draft.DraftId, new UpdateStudioPackageDraftCommand
        {
            PackageKey = draft.PackageKey,
            WorkspaceId = draft.WorkspaceId,
            OwnerId = draft.OwnerId,
            Envelope = envelope,
            Generation = draft.Generation,
            ActorId = "tester",
        });

        Assert.NotNull(updated);
        Assert.NotNull(updated!.Envelope.Bindings);
        Assert.NotNull(updated.Envelope.Dependencies);
        Assert.NotNull(updated.Envelope.Provenance);
        Assert.Empty(updated.Envelope.Bindings);
        Assert.Empty(updated.Envelope.Dependencies);
        Assert.Empty(updated.Envelope.Provenance);
    }

    [UnitTest]
    public void Validate_WithDuplicateBindingsAndInvalidCrs_ReturnsInvalidDiagnostics()
    {
        var provider = BuildServiceProvider();
        var validator = provider.GetRequiredService<IStudioPackageValidator>();
        var envelope = BuildEnvelope("1=1", "content.parcels") with
        {
            Bindings =
            [
                new StudioPackageBinding { Key = "source", Kind = "content", Ref = "content.parcels", Crs = "EPSG:4326" },
                new StudioPackageBinding { Key = "source", Kind = "content", Ref = "content.buildings", Crs = "EPSG:abc" },
            ],
        };

        var validation = validator.Validate(envelope);

        Assert.Equal(StudioPackageValidationStatus.Invalid, validation.Status);
        Assert.Contains(validation.Diagnostics, d => d.Code == "studio.binding.key.duplicate");
        Assert.Contains(validation.Diagnostics, d => d.Code == "studio.binding.crs.invalid");
    }

    [UnitTest]
    public void Validate_WithMissingOrMismatchedFormat_ReturnsInvalidDiagnostics()
    {
        var provider = BuildServiceProvider();
        var validator = provider.GetRequiredService<IStudioPackageValidator>();

        var missing = validator.Validate(BuildEnvelope("1=1", "content.parcels") with { Format = null });
        var mismatched = validator.Validate(BuildEnvelope("1=1", "content.parcels") with { Format = "wrong" });

        Assert.Equal(StudioPackageValidationStatus.Invalid, missing.Status);
        Assert.Contains(missing.Diagnostics, d => d.Code == "studio.format.required");
        Assert.Equal(StudioPackageValidationStatus.Invalid, mismatched.Status);
        Assert.Contains(mismatched.Diagnostics, d => d.Code == "studio.format.unsupported");
    }

    [UnitTest]
    public async Task UpdateDraft_WithConflictingPackageKey_ThrowsConflict()
    {
        var service = BuildServiceProvider().GetRequiredService<IStudioPackageLifecycleService>();
        var first = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "tester",
        });
        var second = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "buildings-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.buildings"),
            ActorId = "tester",
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateDraftAsync(
            second.DraftId,
            new UpdateStudioPackageDraftCommand
            {
                PackageKey = first.PackageKey,
                WorkspaceId = second.WorkspaceId,
                OwnerId = second.OwnerId,
                Envelope = BuildEnvelope("1=1", "content.buildings"),
                Generation = second.Generation,
                ActorId = "tester",
            }));

        Assert.Equal("Studio package key conflicts with an existing content item.", exception.Message);
    }

    [UnitTest]
    public async Task UpdateDraft_WithStaleGeneration_ThrowsConflict()
    {
        var service = BuildServiceProvider().GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "tester",
        });

        var updated = await service.UpdateDraftAsync(
            draft.DraftId,
            new UpdateStudioPackageDraftCommand
            {
                PackageKey = draft.PackageKey,
                WorkspaceId = draft.WorkspaceId,
                OwnerId = draft.OwnerId,
                Envelope = BuildEnvelope("POPULATION > 1000", "content.parcels"),
                Generation = draft.Generation,
                ActorId = "tester",
            });
        Assert.NotNull(updated);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateDraftAsync(
            draft.DraftId,
            new UpdateStudioPackageDraftCommand
            {
                PackageKey = draft.PackageKey,
                WorkspaceId = draft.WorkspaceId,
                OwnerId = draft.OwnerId,
                Envelope = BuildEnvelope("POPULATION > 5000", "content.parcels"),
                Generation = draft.Generation,
                ActorId = "tester",
            }));

        Assert.Equal("Stale draft generation; refresh and retry.", exception.Message);
    }

    [UnitTest]
    public async Task CompareVersions_WithReorderedDependenciesAndProvenance_DoesNotReportSetChanges()
    {
        var service = BuildServiceProvider().GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope(
                "1=1",
                [
                    new StudioPackageDependency { Kind = "content-item", Ref = "content.parcels", VersionId = "v1" },
                    new StudioPackageDependency { Kind = "content-item", Ref = "content.zoning", VersionId = "v3" },
                ],
                [
                    new StudioProvenanceRef { Kind = "prompt", Ref = "prompt-1", Rel = "generated-by" },
                    new StudioProvenanceRef { Kind = "tool", Ref = "builder", Rel = "assisted-by", ActorId = "tester" },
                ]),
            ActorId = "tester",
        });
        var first = await service.SaveDraftAsVersionAsync(draft.DraftId, "first save", "tester");
        Assert.NotNull(first);

        var currentDraft = await service.GetDraftAsync(draft.DraftId);
        Assert.NotNull(currentDraft);
        var updated = await service.UpdateDraftAsync(
            draft.DraftId,
            new UpdateStudioPackageDraftCommand
            {
                PackageKey = draft.PackageKey,
                WorkspaceId = draft.WorkspaceId,
                OwnerId = draft.OwnerId,
                Envelope = BuildEnvelope(
                    "1=1",
                    [
                        new StudioPackageDependency { Kind = "content-item", Ref = "content.zoning", VersionId = "v3" },
                        new StudioPackageDependency { Kind = "content-item", Ref = "content.parcels", VersionId = "v1" },
                    ],
                    [
                        new StudioProvenanceRef { Kind = "tool", Ref = "builder", Rel = "assisted-by", ActorId = "tester" },
                        new StudioProvenanceRef { Kind = "prompt", Ref = "prompt-1", Rel = "generated-by" },
                    ]),
                Generation = currentDraft!.Generation,
                ActorId = "tester",
            });
        Assert.NotNull(updated);
        var second = await service.SaveDraftAsVersionAsync(draft.DraftId, "reordered refs", "tester");
        Assert.NotNull(second);

        var comparison = await service.CompareVersionsAsync(draft.ItemId, first!.VersionId, second!.VersionId);

        Assert.NotNull(comparison);
        Assert.True(comparison!.DependenciesEqual);
        Assert.True(comparison.ProvenanceEqual);
        Assert.DoesNotContain("dependencies", comparison.Changes);
        Assert.DoesNotContain("provenance", comparison.Changes);
    }

    [UnitTest]
    public async Task ListContentItems_FiltersByFamilyOwnerAndState_OrdersByUpdatedAtDescWithIdTiebreak()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var service = BuildServiceProvider(timeProvider: timeProvider).GetRequiredService<IStudioPackageLifecycleService>();

        // Dashboard is envelope-validated only (no family-specific body shape), so the generic
        // `{"where":"1=1"}` body from BuildEnvelope stays valid and the publication request
        // below is accepted rather than rejected.
        var dashboardDraft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-dashboard",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.parcels") with { Family = StudioPackageFamily.Dashboard, Format = "studio_dashboard_package.v1" },
            ActorId = "alice",
        });
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var dashboardVersion = await service.SaveDraftAsVersionAsync(dashboardDraft.DraftId, "first save", "alice");
        Assert.NotNull(dashboardVersion);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var publication = await service.CreatePublicationRequestAsync(dashboardVersion!.ItemId, dashboardVersion.VersionId, intent: null, warningAcknowledgement: null, actorId: "alice");
        Assert.NotNull(publication);
        Assert.Equal(StudioPublicationRequestStatus.Accepted, publication!.Status);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var queryDraft = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            WorkspaceId = "studio",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "bob",
        });

        var familyFiltered = await service.ListContentItemsAsync(new StudioContentItemQuery
        {
            Families = [StudioPackageFamily.Dashboard],
        });
        Assert.Single(familyFiltered.Items);
        Assert.Equal(dashboardVersion.ItemId, familyFiltered.Items[0].ItemId);
        Assert.Equal(StudioContentItemState.Published, familyFiltered.Items[0].State);

        var ownerFiltered = await service.ListContentItemsAsync(new StudioContentItemQuery { OwnerId = "bob" });
        Assert.Single(ownerFiltered.Items);
        Assert.Equal(queryDraft.ItemId, ownerFiltered.Items[0].ItemId);
        Assert.Equal(StudioContentItemState.Draft, ownerFiltered.Items[0].State);

        var stateFiltered = await service.ListContentItemsAsync(new StudioContentItemQuery
        {
            States = [StudioContentItemState.Draft],
        });
        Assert.Single(stateFiltered.Items);
        Assert.Equal(queryDraft.ItemId, stateFiltered.Items[0].ItemId);

        var all = await service.ListContentItemsAsync(new StudioContentItemQuery { Limit = 25 });
        Assert.Equal(2, all.Total);
        Assert.Equal(2, all.Items.Count);
        // Most recently updated (bob's query item) first.
        Assert.Equal(queryDraft.ItemId, all.Items[0].ItemId);
        Assert.Equal(dashboardVersion.ItemId, all.Items[1].ItemId);
        Assert.Null(all.NextCursor);
    }

    [UnitTest]
    public async Task ListContentItems_WithPageSizeOne_PagesThroughAllItemsViaNextCursor()
    {
        var service = BuildServiceProvider().GetRequiredService<IStudioPackageLifecycleService>();
        var first = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "item-a",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "tester",
        });
        var second = await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "item-b",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            ActorId = "tester",
        });

        var page1 = await service.ListContentItemsAsync(new StudioContentItemQuery { Limit = 1 });
        Assert.Single(page1.Items);
        Assert.Equal(2, page1.Total);
        Assert.NotNull(page1.NextCursor);

        var page2 = await service.ListContentItemsAsync(new StudioContentItemQuery { Limit = 1, Cursor = page1.NextCursor });
        Assert.Single(page2.Items);
        Assert.Null(page2.NextCursor);

        var seen = new[] { page1.Items[0].ItemId, page2.Items[0].ItemId };
        Assert.Contains(first.ItemId, seen);
        Assert.Contains(second.ItemId, seen);
        Assert.NotEqual(page1.Items[0].ItemId, page2.Items[0].ItemId);
    }

    [UnitTest]
    public async Task ListDrafts_FiltersByOwnerAndSearchTerm()
    {
        var service = BuildServiceProvider().GetRequiredService<IStudioPackageLifecycleService>();
        await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "parcels-query",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            OwnerId = "alice",
            ActorId = "alice",
        });
        await service.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = "buildings-query",
            Envelope = BuildEnvelope("1=1", "content.parcels"),
            OwnerId = "bob",
            ActorId = "bob",
        });

        var ownerFiltered = await service.ListDraftsAsync(new StudioPackageDraftQuery { OwnerId = "alice" });
        Assert.Single(ownerFiltered.Items);
        Assert.Equal("parcels-query", ownerFiltered.Items[0].PackageKey);

        var searchFiltered = await service.ListDraftsAsync(new StudioPackageDraftQuery { SearchTerm = "building" });
        Assert.Single(searchFiltered.Items);
        Assert.Equal("buildings-query", searchFiltered.Items[0].PackageKey);

        var all = await service.ListDraftsAsync(new StudioPackageDraftQuery());
        Assert.Equal(2, all.Total);
    }

    [UnitTest]
    public void PackageFamilyCapabilities_AdvertisesAllFamiliesAndLifecycleOperations()
    {
        var service = BuildServiceProvider().GetRequiredService<IStudioPackageLifecycleService>();

        var capabilities = service.GetCapabilities();

        Assert.False(capabilities.Durable);
        Assert.Equal(10, capabilities.Families.Count);
        Assert.Contains(capabilities.Families, f =>
            f.Family == StudioPackageFamily.Map &&
            f.Format == "honua_map_package.v1" &&
            f.SupportedOperations.Contains(StudioPackageOperation.ContentVersionCreate));
        Assert.Contains(capabilities.Families, f =>
            f.Family == StudioPackageFamily.Etl &&
            f.Limitations.Contains("family-specific deep validation is deferred; envelope validation is active"));
    }

    [UnitTest]
    public async Task ReadAndDeleteLifecycleActivities_CoverStoreWork()
    {
        var store = new BlockingStudioPackageStore();
        var service = BuildServiceProvider(store).GetRequiredService<IStudioPackageLifecycleService>();
        var draftId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var stoppedActivities = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StudioPackageLifecycleService.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stoppedActivities.Enqueue(activity.OperationName),
        };
        ActivitySource.AddActivityListener(listener);

        _ = await AssertActivityCoversBlockedStoreCallAsync(
            store,
            stoppedActivities,
            nameof(IStudioPackageStore.GetDraftAsync),
            "studio.package.draft.get",
            () => service.GetDraftAsync(draftId));
        _ = await AssertActivityCoversBlockedStoreCallAsync(
            store,
            stoppedActivities,
            nameof(IStudioPackageStore.DeleteDraftAsync),
            "studio.package.draft.delete",
            () => service.DeleteDraftAsync(draftId));
        _ = await AssertActivityCoversBlockedStoreCallAsync(
            store,
            stoppedActivities,
            nameof(IStudioPackageStore.ListVersionsAsync),
            "studio.package.version.list",
            () => service.ListVersionsAsync(itemId));
        _ = await AssertActivityCoversBlockedStoreCallAsync(
            store,
            stoppedActivities,
            nameof(IStudioPackageStore.GetVersionAsync),
            "studio.package.version.get",
            () => service.GetVersionAsync(itemId, versionId));
    }

    private static ServiceProvider BuildServiceProvider(
        IStudioPackageStore? store = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        if (store is not null)
        {
            services.AddSingleton(store);
        }

        services.AddStudioPackageLifecycle();
        return services.BuildServiceProvider();
    }

    private static async Task<T> AssertActivityCoversBlockedStoreCallAsync<T>(
        BlockingStudioPackageStore store,
        ConcurrentQueue<string> stoppedActivities,
        string storeOperation,
        string activityName,
        Func<Task<T>> action)
    {
        var stoppedBefore = CountStopped(stoppedActivities, activityName);
        store.BlockNext(storeOperation);
        var operation = action();

        await store.WaitForBlockedAsync().ConfigureAwait(false);
        Assert.Equal(stoppedBefore, CountStopped(stoppedActivities, activityName));

        store.ReleaseBlocked();
        var result = await operation.ConfigureAwait(false);
        Assert.True(CountStopped(stoppedActivities, activityName) > stoppedBefore);
        return result;
    }

    private static int CountStopped(ConcurrentQueue<string> stoppedActivities, string activityName)
        => stoppedActivities.Count(name => string.Equals(name, activityName, StringComparison.Ordinal));

    private static StudioPackageEnvelope BuildEnvelope(string where, string dependencyRef)
        => BuildEnvelope(
            where,
            [
                new StudioPackageDependency
                {
                    Kind = "content-item",
                    Ref = dependencyRef,
                    VersionId = "v1",
                },
            ],
            [
                new StudioProvenanceRef
                {
                    Kind = "prompt",
                    Ref = "prompt-1",
                    Rel = "generated-by",
                },
            ],
            dependencyRef);

    private static StudioPackageEnvelope BuildEnvelope(
        string where,
        IReadOnlyList<StudioPackageDependency> dependencies,
        IReadOnlyList<StudioProvenanceRef> provenance,
        string bindingRef = "content.parcels")
    {
        using var body = JsonDocument.Parse($$"""{"where":"{{where}}"}""");
        return new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Query,
            SchemaVersion = "1.0",
            Format = "studio_query_package.v1",
            Bindings =
            [
                new StudioPackageBinding
                {
                    Key = "source",
                    Kind = "content",
                    Ref = bindingRef,
                    Crs = "EPSG:4326",
                    Srid = 4326,
                    RequiredPermissions = ["metadata.read"],
                },
            ],
            Dependencies = dependencies,
            Provenance = provenance,
            PublicationIntent = new StudioPublicationIntent { Route = "/studio/parcels", Visibility = "organization" },
            Body = body.RootElement.Clone(),
        };
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
            => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
            => _utcNow;

        public void Advance(TimeSpan value)
            => _utcNow = _utcNow.Add(value);
    }

    private sealed class BlockingStudioPackageStore : IStudioPackageStore
    {
        private readonly object _gate = new();
        private string? _blockedOperation;
        private TaskCompletionSource<bool> _blocked = CreateCompletionSource();
        private TaskCompletionSource<bool> _release = CreateCompletionSource();

        public StudioPackagePersistenceMode PersistenceMode => StudioPackagePersistenceMode.InMemory;

        public void BlockNext(string operation)
        {
            lock (_gate)
            {
                _blockedOperation = operation;
                _blocked = CreateCompletionSource();
                _release = CreateCompletionSource();
            }
        }

        public Task<bool> WaitForBlockedAsync()
            => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseBlocked()
            => _release.TrySetResult(true);

        public Task<StudioPackageDraft> CreateDraftAsync(
            StudioPackageDraft draft,
            CancellationToken cancellationToken = default)
            => Task.FromException<StudioPackageDraft>(new NotSupportedException());

        public async Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        {
            await MaybeBlockAsync(nameof(GetDraftAsync)).ConfigureAwait(false);
            return null;
        }

        public Task<StudioPackageDraft?> UpdateDraftAsync(
            StudioPackageDraft draft,
            CancellationToken cancellationToken = default)
            => Task.FromException<StudioPackageDraft?>(new NotSupportedException());

        public async Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
        {
            await MaybeBlockAsync(nameof(DeleteDraftAsync)).ConfigureAwait(false);
            return true;
        }

        public Task<StudioContentVersion> CreateVersionAsync(
            StudioPackageDraft draft,
            string? changeNote,
            string? actorId,
            CancellationToken cancellationToken = default)
            => Task.FromException<StudioContentVersion>(new NotSupportedException());

        public async Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(
            Guid itemId,
            CancellationToken cancellationToken = default)
        {
            await MaybeBlockAsync(nameof(ListVersionsAsync)).ConfigureAwait(false);
            return [];
        }

        public async Task<StudioContentVersion?> GetVersionAsync(
            Guid itemId,
            Guid versionId,
            CancellationToken cancellationToken = default)
        {
            await MaybeBlockAsync(nameof(GetVersionAsync)).ConfigureAwait(false);
            return null;
        }

        public Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default)
            => Task.FromException<StudioContentItemPointers?>(new NotSupportedException());

        public Task<StudioContentItemListResult> ListContentItemsAsync(
            StudioContentItemQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromException<StudioContentItemListResult>(new NotSupportedException());

        public Task<StudioPackageDraftListResult> ListDraftsAsync(
            StudioPackageDraftQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromException<StudioPackageDraftListResult>(new NotSupportedException());

        public Task<StudioPublicationRequest> CreatePublicationRequestAsync(
            StudioPublicationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<StudioPublicationRequest>(new NotSupportedException());

        public Task<StudioRollbackRequest> RollbackAsync(
            Guid itemId,
            Guid targetVersionId,
            StudioRollbackPointer target,
            string? actorId,
            string? reason,
            CancellationToken cancellationToken = default)
            => Task.FromException<StudioRollbackRequest>(new NotSupportedException());

        private static TaskCompletionSource<bool> CreateCompletionSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task MaybeBlockAsync(string operation)
        {
            TaskCompletionSource<bool>? blocked;
            TaskCompletionSource<bool>? release;
            lock (_gate)
            {
                if (!string.Equals(_blockedOperation, operation, StringComparison.Ordinal))
                {
                    return;
                }

                _blockedOperation = null;
                blocked = _blocked;
                release = _release;
            }

            blocked.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
        }
    }
}
