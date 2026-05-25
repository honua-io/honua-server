// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
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

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        return services.BuildServiceProvider();
    }

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
}
