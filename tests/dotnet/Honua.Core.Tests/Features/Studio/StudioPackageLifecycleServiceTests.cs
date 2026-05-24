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
                    Ref = dependencyRef,
                    Crs = "EPSG:4326",
                    Srid = 4326,
                    RequiredPermissions = ["metadata.read"],
                },
            ],
            Dependencies =
            [
                new StudioPackageDependency
                {
                    Kind = "content-item",
                    Ref = dependencyRef,
                    VersionId = "v1",
                },
            ],
            Provenance =
            [
                new StudioProvenanceRef
                {
                    Kind = "prompt",
                    Ref = "prompt-1",
                    Rel = "generated-by",
                },
            ],
            PublicationIntent = new StudioPublicationIntent { Route = "/studio/parcels", Visibility = "organization" },
            Body = body.RootElement.Clone(),
        };
    }
}
