// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicExportRevocationTests
{
    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenLayerRestricted_DeletesObjectsRewritesCatalogInvalidatesAndCompletes()
    {
        var harness = RevocationHarness.Create(anonymousAllowed: false);

        await harness.Service.OnAdministrativeAccessChangeAsync("svc-parcels", [7], CancellationToken.None);

        harness.PublicObjects.Keys.Where(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal))
            .Should().BeEmpty();
        var catalog = JsonNode.Parse(harness.PublicObjects["data/catalog.json"])!.AsObject();
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
        var harness = RevocationHarness.Create(anonymousAllowed: false);
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
        var harness = RevocationHarness.Create(anonymousAllowed: false);
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
        var harness = RevocationHarness.Create(
            anonymousAllowed: false,
            invalidationOutcomes:
            [
                new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "Throttled"),
                new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "INV-RETRY")
            ]);

        var first = await harness.Service.RevokeUnpublishedAsync("parcels", "chg-unpub", CancellationToken.None);

        first.State.Should().Be(PublicExportRevocationState.CatalogRewritten);
        first.AttemptCount.Should().Be(1);
        first.NextAttemptAt.Should().NotBeNull();
        harness.FailureNotices.Should().ContainSingle(n => n.Detail.Contains("Failed"));

        // Advance past backoff by clearing NextAttemptAt in persisted state to simulate timer wake.
        await harness.StateStore.SaveRevocationAsync(first with { NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, CancellationToken.None);
        await harness.Service.ResumePendingAsync(CancellationToken.None);

        harness.StateStore.Revocations[first.RevocationId].State.Should().Be(PublicExportRevocationState.Completed);
        harness.PollReceipts.Should().ContainSingle(r => r.InvalidationId == "INV-RETRY");
    }

    [UnitTest]
    public async Task UnpublishPath_DeletesPublicObjectsRewritesCatalogAndCompletes()
    {
        var harness = RevocationHarness.Create(anonymousAllowed: true);

        var result = await harness.Service.RevokeUnpublishedAsync("parcels", "chg-del", CancellationToken.None);

        result.Reason.Should().Be(PublicExportRevocationService.ReasonUnpublished);
        result.State.Should().Be(PublicExportRevocationState.Completed);
        harness.PublicObjects.Keys.Should().NotContain(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal));
        harness.InvalidationRequests.Should().ContainSingle();
    }

    private sealed class RevocationHarness
    {
        public required PublicExportRevocationService Service { get; init; }
        public required PublicExportPromotionTests.InMemoryPublicExportStateStore StateStore { get; init; }
        public required Dictionary<string, byte[]> PublicObjects { get; init; }
        public required List<string[]> InvalidationRequests { get; init; }
        public required List<CdnInvalidationReceipt> PollReceipts { get; init; }
        public required List<(PublicExportRevocationRecord Record, string Detail)> FailureNotices { get; init; }

        public static RevocationHarness Create(
            bool anonymousAllowed,
            CdnInvalidationResult[]? invalidationOutcomes = null)
        {
            var publicObjects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["data/publications/parcels/v1/parcels.pmtiles"] = [1],
                ["data/publications/parcels/v1/parcels.parquet"] = [2],
                ["data/publications/parcels/v1/collection.json"] = [3],
                ["data/publications/roads/v1/collection.json"] = [4],
                ["data/catalog.json"] = "{}"u8.ToArray()
            };

            var publicStorage = new Mock<ICloudFileStorage>();
            publicStorage.Setup(s => s.ListFilesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string? prefix, int _, bool _, CancellationToken _) =>
                    publicObjects.Keys
                        .Where(k => prefix is null || k.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(k => new CloudFile
                        {
                            FileId = k, FileName = Path.GetFileName(k), StoragePath = k,
                            ContentType = "application/octet-stream", SizeBytes = publicObjects[k].Length,
                            UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.AwsS3
                        }).ToArray());
            publicStorage.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => publicObjects.Remove(key));
            publicStorage.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => publicObjects.ContainsKey(key));
            publicStorage.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FileUploadRequest request, CancellationToken _) =>
                {
                    using var copy = new MemoryStream();
                    request.Content.CopyTo(copy);
                    var key = request.ObjectKeyOverride!;
                    publicObjects[key] = copy.ToArray();
                    return UploadResult.CreateSuccess(new CloudFile
                    {
                        FileId = key, FileName = request.FileName, StoragePath = key,
                        ContentType = request.ContentType, SizeBytes = publicObjects[key].Length,
                        UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.AwsS3
                    }, TimeSpan.Zero);
                });

            var destination = new PublicPublicationDestination(publicStorage.Object, new Uri("https://cdn.example.test/"));

            var stateStore = new PublicExportPromotionTests.InMemoryPublicExportStateStore();
            stateStore.Promoted["parcels"] = new PromotedExportRecord
            {
                PublicationId = "parcels", Version = "v1", ServiceId = "svc-parcels", ResourceId = "res-parcels",
                StorageLayerId = 7, Title = "Parcels",
                CollectionUrl = "https://cdn.example.test/data/publications/parcels/v1/collection.json",
                CatalogUrl = "https://cdn.example.test/data/catalog.json",
                PromotedAt = DateTimeOffset.UtcNow, CanaryState = PublicExportCanaryState.Readable
            };
            stateStore.Promoted["roads"] = new PromotedExportRecord
            {
                PublicationId = "roads", Version = "v1", ServiceId = "svc-roads", ResourceId = "res-roads",
                StorageLayerId = 8, Title = "Roads",
                CollectionUrl = "https://cdn.example.test/data/publications/roads/v1/collection.json",
                CatalogUrl = "https://cdn.example.test/data/catalog.json",
                PromotedAt = DateTimeOffset.UtcNow, CanaryState = PublicExportCanaryState.Readable
            };

            var evaluator = new Mock<IAccessPolicyEvaluator>();
            evaluator.Setup(e => e.Evaluate(
                    It.IsAny<ClaimsPrincipal>(),
                    It.IsAny<AccessPolicy?>(),
                    It.IsAny<AccessPolicy?>(),
                    It.IsAny<object?>()))
                .Returns(anonymousAllowed ? AccessDecision.Allowed() : AccessDecision.RequiresAuth("Restricted"));

            var services = new ServiceCollection();
            services.AddSingleton(evaluator.Object);
            var eligibility = new PublicExportEligibility(services.BuildServiceProvider());

            var graph = new MetadataV2Graph
            {
                Revision = 2,
                Services =
                [
                    new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = "parcels-svc" } },
                    new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "svc-roads", Name = "roads-svc" } }
                ],
                Resources =
                [
                    new MetadataV2Object { Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels" } },
                    new MetadataV2Object { Metadata = new MetadataV2ObjectMetadata { Id = "res-roads", Name = "roads" } }
                ]
            };
            var graphProvider = new Mock<IMetadataV2GraphProvider>();
            graphProvider.Setup(g => g.GetCurrentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataV2GraphSnapshot(graph, "etag-2", DateTimeOffset.UtcNow));

            var invalidationRequests = new List<string[]>();
            var outcomeQueue = new Queue<CdnInvalidationResult>(
                invalidationOutcomes ?? [new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "INV-1")]);
            var invalidator = new Mock<ICdnInvalidator>();
            invalidator.Setup(i => i.InvalidateAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<string> paths, CancellationToken _) =>
                {
                    invalidationRequests.Add([.. paths]);
                    return outcomeQueue.Count > 0
                        ? outcomeQueue.Dequeue()
                        : new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "INV-NEXT");
                });
            var cdnService = new CdnInvalidationService(invalidator.Object, NullLogger<CdnInvalidationService>.Instance);

            var pollReceipts = new List<CdnInvalidationReceipt>();
            var statusReader = new Mock<ICdnInvalidationStatusReader>();
            statusReader.Setup(r => r.ReadAsync(It.IsAny<CdnInvalidationReceipt>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((CdnInvalidationReceipt receipt, CancellationToken _) =>
                {
                    pollReceipts.Add(receipt);
                    return new CdnInvalidationStatusResult(CdnInvalidationStatus.Completed);
                });

            var failureNotices = new List<(PublicExportRevocationRecord, string)>();
            var notifier = new Mock<IPublicExportOpsNotifier>();
            notifier.Setup(n => n.NotifyRevocationFailedAsync(
                    It.IsAny<PublicExportRevocationRecord>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<PublicExportRevocationRecord, string, CancellationToken>((rec, detail, _) =>
                    failureNotices.Add((rec, detail)))
                .Returns(Task.CompletedTask);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [PublicExportRevocationService.DistributionIdSetting] = "E123DIST"
                })
                .Build();

            var service = new PublicExportRevocationService(
                stateStore,
                eligibility,
                graphProvider.Object,
                cdnService,
                notifier.Object,
                config,
                NullLogger<PublicExportRevocationService>.Instance,
                destination,
                statusReader.Object);

            return new RevocationHarness
            {
                Service = service,
                StateStore = stateStore,
                PublicObjects = publicObjects,
                InvalidationRequests = invalidationRequests,
                PollReceipts = pollReceipts,
                FailureNotices = failureNotices
            };
        }
    }
}
