// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Admin.Exports;

[Collection("Unit")]
public sealed class PublicExportRevocationTests
{
    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenLayerRestrictedByServiceName_DeletesObjectsRewritesCatalogInvalidatesAndCompletes()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = false });

        // ServiceSettingsEndpoints passes service.Metadata.Name and layerIndex, not Metadata.Id / StorageLayerId.
        await harness.Service.OnAdministrativeAccessChangeAsync("parcels-svc", [0], CancellationToken.None);

        harness.PublicStorage.Objects.Keys.Where(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal))
            .Should().BeEmpty();
        var catalog = JsonNode.Parse(harness.PublicStorage.Objects["data/catalog.json"])!.AsObject();
        catalog["links"]!.AsArray().Select(n => n!["href"]!.GetValue<string>())
            .Should().NotContain(href => href.Contains("parcels", StringComparison.Ordinal));
        catalog["links"]!.AsArray().Select(n => n!["href"]!.GetValue<string>())
            .Should().Contain("./publications/roads/v1/collection.json");

        harness.InvalidationRequests.Should().ContainSingle();
        harness.InvalidationRequests[0].Should().BeEquivalentTo(["/data/publications/parcels/*", "/data/catalog.json"]);
        harness.PollReceipts.Should().ContainSingle(r => r.ResourceId == "E123DIST" && r.InvalidationId == "INV-1");
        harness.StateStore.Revocations.Values.Should().ContainSingle(r => r.State == PublicExportRevocationState.Completed);
        harness.FailureNotices.Should().BeEmpty();
    }

    [UnitTest]
    public async Task CrashBetweenDeleteAndInvalidation_RestartResumesAndCreatesExactlyOneInvalidation()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = false });
        var crashed = new PublicExportRevocationRecord
        {
            RevocationId = "rev-crash-1",
            PublicationId = "parcels",
            Reason = PublicExportRevocationService.ReasonAccessRestricted,
            ChangeId = "chg-101",
            State = PublicExportRevocationState.ObjectsDeleted,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await harness.StateStore.SaveRevocationAsync(crashed, CancellationToken.None);
        await harness.StateStore.DeletePromotedAsync("parcels", CancellationToken.None);

        await harness.Service.ResumePendingAsync(CancellationToken.None);

        harness.InvalidationRequests.Should().ContainSingle();
        harness.PollReceipts.Should().ContainSingle(r => r.InvalidationId == "INV-1");
        harness.StateStore.Revocations["rev-crash-1"].State.Should().Be(PublicExportRevocationState.Completed);
    }

    [UnitTest]
    public async Task CrashAfterInvalidationCreated_RestartPollsSameIdWithoutSecondInvalidation()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = false });
        var crashed = new PublicExportRevocationRecord
        {
            RevocationId = "rev-crash-2",
            PublicationId = "parcels",
            Reason = PublicExportRevocationService.ReasonAccessRestricted,
            ChangeId = "chg-102",
            State = PublicExportRevocationState.InvalidationCreated,
            InvalidationId = "INV-EXISTING",
            DistributionId = "E123DIST",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await harness.StateStore.SaveRevocationAsync(crashed, CancellationToken.None);

        await harness.Service.ResumePendingAsync(CancellationToken.None);

        harness.InvalidationRequests.Should().BeEmpty();
        harness.PollReceipts.Should().ContainSingle(r => r.ResourceId == "E123DIST" && r.InvalidationId == "INV-EXISTING");
        harness.StateStore.Revocations["rev-crash-2"].State.Should().Be(PublicExportRevocationState.Completed);
    }

    [UnitTest]
    public async Task InvalidationFailure_PostsOpsNoticeAndRetriesToCompletion()
    {
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = false,
            InvalidationOutcomes =
            [
                new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "Throttled"),
                new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "INV-RETRY")
            ]
        });

        var first = await harness.Service.RevokeUnpublishedAsync("parcels", "chg-unpub", CancellationToken.None);

        first.State.Should().Be(PublicExportRevocationState.CatalogRewritten);
        first.AttemptCount.Should().Be(1);
        first.NextAttemptAt.Should().NotBeNull();
        harness.FailureNotices.Should().ContainSingle(n => n.Detail.Contains("Failed"));

        await harness.StateStore.SaveRevocationAsync(first with { NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, CancellationToken.None);
        await harness.Service.ResumePendingAsync(CancellationToken.None);

        harness.StateStore.Revocations[first.RevocationId].State.Should().Be(PublicExportRevocationState.Completed);
        harness.PollReceipts.Should().ContainSingle(r => r.InvalidationId == "INV-RETRY");
    }

    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenLayerUnpublishedFromService_RevokesExport()
    {
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = true,
            IncludeParcelsPublication = false
        });

        await harness.Service.OnAdministrativeAccessChangeAsync(serviceId: null, layerIds: null, CancellationToken.None);

        harness.PublicStorage.Objects.Keys.Where(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal))
            .Should().BeEmpty();
        harness.StateStore.Revocations.Values.Should().ContainSingle(r =>
            r.PublicationId == "parcels" && r.State == PublicExportRevocationState.Completed);
        harness.StateStore.Promoted.Keys.Should().Equal("roads");
    }

    [UnitTest]
    public async Task UnpublishPath_DeletesPublicObjectsRewritesCatalogAndCompletes()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = true });

        var result = await harness.Service.RevokeUnpublishedAsync("parcels", "chg-del", CancellationToken.None);

        result.Reason.Should().Be(PublicExportRevocationService.ReasonUnpublished);
        result.State.Should().Be(PublicExportRevocationState.Completed);
        harness.PublicStorage.Objects.Keys.Should().NotContain(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal));
        harness.InvalidationRequests.Should().ContainSingle();
    }

    [UnitTest]
    public async Task HostedService_StartupSweepWithTenancyEnabled_RevokesPromotedExports()
    {
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = true,
            MultiTenancyEnabled = true
        });
        using var hosted = harness.CreateHostedService();

        var keepRunning = await hosted.RunCycleAsync(CancellationToken.None);

        keepRunning.Should().BeTrue();
        harness.StateStore.Promoted.Should().BeEmpty();
        harness.PublicStorage.Objects.Keys.Where(k => k.StartsWith("data/publications/", StringComparison.Ordinal))
            .Should().BeEmpty();
        harness.StateStore.Revocations.Values.Should().HaveCount(2)
            .And.OnlyContain(r => r.State == PublicExportRevocationState.Completed);
    }

    [UnitTest]
    public async Task HostedService_WhenFirstEligibilitySweepThrows_NextTickRetriesAndRevokesExport()
    {
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = false,
            GraphFailuresBeforeSuccess = 1
        });
        using var hosted = harness.CreateHostedService();

        // First tick hits transient metadata error during eligibility sweep and does not mark sweep complete.
        (await hosted.RunCycleAsync(CancellationToken.None)).Should().BeTrue();
        harness.StateStore.Promoted.Keys.Should().Contain("parcels");

        // Next tick retries immediately without waiting for the 5-minute interval and revokes the restricted export.
        (await hosted.RunCycleAsync(CancellationToken.None)).Should().BeTrue();
        harness.StateStore.Promoted.Keys.Should().Equal("roads");
        harness.PublicStorage.Objects.Keys.Where(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal))
            .Should().BeEmpty();
        harness.StateStore.Revocations.Values.Should().ContainSingle(r =>
            r.PublicationId == "parcels" && r.State == PublicExportRevocationState.Completed);
    }

    [UnitTest]
    public async Task CloudStorageStateStore_WhenListingReachesPageLimit_FailsClosed()
    {
        var fullPage = Enumerable.Range(0, CloudStoragePublicExportStateStore.MaxStateRecords)
            .Select(i => new CloudFile
            {
                FileId = $"exports/_public/promoted/pub-{i}.json",
                FileName = $"pub-{i}.json",
                StoragePath = $"exports/_public/promoted/pub-{i}.json",
                ContentType = "application/json",
                SizeBytes = 10,
                UploadedAt = DateTimeOffset.UtcNow,
                Provider = CloudStorageProvider.AwsS3
            })
            .ToArray();
        var storage = new Mock<ICloudFileStorage>();
        storage.Setup(s => s.ListFilesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullPage);
        var store = new CloudStoragePublicExportStateStore(storage.Object, Options.Create(new CloudStorageOptions()));

        var listPromoted = () => store.ListPromotedAsync(CancellationToken.None);
        var listRevocations = () => store.ListPendingRevocationsAsync(CancellationToken.None);

        await listPromoted.Should().ThrowAsync<InvalidOperationException>().WithMessage("*listing limit*");
        await listRevocations.Should().ThrowAsync<InvalidOperationException>().WithMessage("*listing limit*");
    }

    [UnitTest]
    public async Task ExportCatalogUnpublish_WhenPromoted_CreatesRevocationRecordAndDeletesPublicObjects()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = true });
        var privateStorage = new Mock<ICloudFileStorage>();
        privateStorage.Setup(s => s.ListFilesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        privateStorage.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadResult { Success = true });
        privateStorage.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var jobs = new Mock<ITileCacheJobService>();
        var catalog = new ExportCatalog(new ExportCatalogDependencies
        {
            Storage = privateStorage.Object,
            Options = Options.Create(new CloudStorageOptions()),
            Jobs = jobs.Object,
            PublicRevocation = harness.Service
        });

        var deleted = await catalog.UnpublishAsync("parcels", CancellationToken.None);

        deleted.Should().BeTrue();
        harness.StateStore.Revocations.Values.Should().ContainSingle(r =>
            r.PublicationId == "parcels" &&
            r.Reason == PublicExportRevocationService.ReasonUnpublished &&
            r.State == PublicExportRevocationState.Completed);
        harness.PublicStorage.Objects.Keys.Should().NotContain(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal));
        harness.InvalidationRequests.Should().ContainSingle();
    }

    [UnitTest]
    public async Task ExportCatalogUnpublish_WhenRevocationStateStoreFails_PreservesPointerForRetry()
    {
        var failingStateStore = new Mock<IPublicExportStateStore>();
        failingStateStore.Setup(s => s.ListPendingRevocationsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("state store unavailable"));
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = true,
            StateStoreOverride = failingStateStore.Object
        });
        var privateStorage = new Mock<ICloudFileStorage>();
        privateStorage.Setup(s => s.ListFilesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        privateStorage.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploadResult { Success = true });
        var catalog = new ExportCatalog(new ExportCatalogDependencies
        {
            Storage = privateStorage.Object,
            Options = Options.Create(new CloudStorageOptions()),
            Jobs = new Mock<ITileCacheJobService>().Object,
            PublicRevocation = harness.Service
        });

        var act = () => catalog.UnpublishAsync("parcels", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*state store unavailable*");
        privateStorage.Verify(
            s => s.UploadAsync(
                It.Is<FileUploadRequest>(r => r.ObjectKeyOverride != null && r.ObjectKeyOverride.EndsWith("unpublished.json", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        privateStorage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenExportHasNoActivePointer_RevokesExportAsUnpublished()
    {
        var now = DateTimeOffset.UtcNow;
        var privateStorage = CreatePrivateExportStorage(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["exports/roads/current.json"] = JsonSerializer.SerializeToUtf8Bytes(
                new ExportPointer("roads", "v1", "v1/collection.json", now),
                ExportVersionJsonContext.Default.ExportPointer)
        });
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = true,
            PrivateStorage = privateStorage
        });

        await harness.Service.OnAdministrativeAccessChangeAsync(serviceId: null, layerIds: null, CancellationToken.None);

        harness.StateStore.Revocations.Values.Should().ContainSingle(r =>
            r.PublicationId == "parcels" &&
            r.Reason == PublicExportRevocationService.ReasonUnpublished &&
            r.State == PublicExportRevocationState.Completed);
        harness.StateStore.Promoted.Keys.Should().Equal("roads");
    }

    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenUnpublishedMarkerIsNewerThanPointer_RevokesExportAsUnpublished()
    {
        var now = DateTimeOffset.UtcNow;
        var privateStorage = CreatePrivateExportStorage(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["exports/parcels/current.json"] = JsonSerializer.SerializeToUtf8Bytes(
                new ExportPointer("parcels", "v1", "v1/collection.json", now.AddMinutes(-2)),
                ExportVersionJsonContext.Default.ExportPointer),
            ["exports/parcels/unpublished.json"] = JsonSerializer.SerializeToUtf8Bytes(
                new ExportUnpublishedMarker
                {
                    ExportId = "parcels",
                    ServiceId = "svc-parcels",
                    LayerId = "7",
                    UnpublishedAt = now.AddMinutes(-1)
                },
                ExportVersionJsonContext.Default.ExportUnpublishedMarker),
            ["exports/roads/current.json"] = JsonSerializer.SerializeToUtf8Bytes(
                new ExportPointer("roads", "v1", "v1/collection.json", now),
                ExportVersionJsonContext.Default.ExportPointer)
        });
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = true,
            PrivateStorage = privateStorage
        });

        await harness.Service.OnAdministrativeAccessChangeAsync(serviceId: null, layerIds: null, CancellationToken.None);

        harness.StateStore.Revocations.Values.Should().ContainSingle(r =>
            r.PublicationId == "parcels" &&
            r.Reason == PublicExportRevocationService.ReasonUnpublished &&
            r.State == PublicExportRevocationState.Completed);
        harness.StateStore.Promoted.Keys.Should().Equal("roads");
    }

    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenRepublishedAfterStaleUnpublishedMarker_KeepsPromotedExport()
    {
        var now = DateTimeOffset.UtcNow;
        var privateStorage = CreatePrivateExportStorage(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["exports/parcels/current.json"] = JsonSerializer.SerializeToUtf8Bytes(
                new ExportPointer("parcels", "v2", "v2/collection.json", now),
                ExportVersionJsonContext.Default.ExportPointer),
            ["exports/parcels/unpublished.json"] = JsonSerializer.SerializeToUtf8Bytes(
                new ExportUnpublishedMarker
                {
                    ExportId = "parcels",
                    ServiceId = "svc-parcels",
                    LayerId = "7",
                    UnpublishedAt = now.AddMinutes(-5)
                },
                ExportVersionJsonContext.Default.ExportUnpublishedMarker),
            ["exports/roads/current.json"] = JsonSerializer.SerializeToUtf8Bytes(
                new ExportPointer("roads", "v1", "v1/collection.json", now),
                ExportVersionJsonContext.Default.ExportPointer)
        });
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = true,
            PrivateStorage = privateStorage
        });

        await harness.Service.OnAdministrativeAccessChangeAsync(serviceId: null, layerIds: null, CancellationToken.None);

        harness.StateStore.Revocations.Should().BeEmpty();
        harness.StateStore.Promoted.Keys.Should().BeEquivalentTo(["parcels", "roads"]);
    }

    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenPrivateStorageThrowsForOneExport_StillRevokesIneligibleSiblingAndRethrows()
    {
        var storage = new Mock<ICloudFileStorage>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("transient storage timeout"));
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = false,
            PrivateStorage = storage.Object
        });

        var act = () => harness.Service.OnAdministrativeAccessChangeAsync(serviceId: null, layerIds: null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*transient storage timeout*");
        harness.StateStore.Revocations.Values.Should().ContainSingle(r =>
            r.PublicationId == "parcels" &&
            r.Reason == PublicExportRevocationService.ReasonAccessRestricted &&
            r.State == PublicExportRevocationState.Completed);
        harness.StateStore.Promoted.Keys.Should().Equal("roads");
    }

    private static ICloudFileStorage CreatePrivateExportStorage(Dictionary<string, byte[]> objects)
    {
        var storage = new Mock<ICloudFileStorage>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
        return storage.Object;
    }
}
