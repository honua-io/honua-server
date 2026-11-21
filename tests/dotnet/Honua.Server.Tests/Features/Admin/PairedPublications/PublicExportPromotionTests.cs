// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicExportPromotionTests
{
    [UnitTest]
    public void CacheControl_PublicImmutable_UsesOneYearImmutableHeader()
    {
        StoredObjectCacheControl.For(StoredObjectCachePolicy.PublicImmutable)
            .Should().Be("public, max-age=31536000, immutable");
    }

    [UnitTest]
    public async Task PromoteAsync_WhenPublicDataDisabled_ReportsNotConfiguredWithoutError()
    {
        var harness = PromotionHarness.Create(destinationConfigured: false);

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.NotConfigured);
        result.Reason.Should().Be(PublicExportPromoter.NotConfiguredMessage);
        harness.PublicWrites.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithTenancyEnabled_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(multiTenancyEnabled: true);

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonMultiTenancyEnabled);
        harness.PublicWrites.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithAnonymousAccessDenied_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(anonymousAllowed: false);

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonAnonymousDenied);
        harness.PublicWrites.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithPermanentRowFilter_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(permanentFilter: "status = 'public'");

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonRowFilter);
        harness.PublicWrites.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithAnonymousRlsFilter_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(rlsFilter: new SqlFragment("region = @p0", ["west"]));

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonRowFilter);
        harness.PublicWrites.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithHiddenSchemaField_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(hasHiddenField: true);

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonHiddenField);
        harness.PublicWrites.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithAnonymousFieldMask_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(maskedFields: ["owner_ssn"]);

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonHiddenField);
        harness.PublicWrites.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WritesArtifactsThenCollectionThenRootCatalogLast_WithCorrectCachePolicies()
    {
        var harness = PromotionHarness.Create();

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.Promoted);
        result.CollectionUrl.Should().Be("https://cdn.example.test/data/publications/parcels/v1/collection.json");
        result.CatalogUrl.Should().Be("https://cdn.example.test/data/catalog.json");

        harness.PublicWrites.Select(w => w.Key).Should().Equal(
            "data/publications/parcels/v1/parcels.pmtiles",
            "data/publications/parcels/v1/parcels.parquet",
            "data/publications/parcels/v1/README.md",
            "data/publications/parcels/v1/collection.json",
            "data/catalog.json");

        harness.PublicWrites.Take(4).Should().OnlyContain(w => w.CachePolicy == StoredObjectCachePolicy.PublicImmutable);
        harness.PublicWrites.Last().CachePolicy.Should().Be(StoredObjectCachePolicy.PublicReplacedInPlace);

        var catalog = JsonNode.Parse(harness.PublicObjects["data/catalog.json"])!.AsObject();
        catalog["type"]!.GetValue<string>().Should().Be("Catalog");
        catalog["stac_version"]!.GetValue<string>().Should().Be("1.1.0");
        catalog["links"]!.AsArray().Select(n => n!["href"]!.GetValue<string>())
            .Should().Contain("./publications/parcels/v1/collection.json");
    }

    [UnitTest]
    public async Task PromoteAsync_RetryAfterCrashBetweenArtifactsAndPointer_IsIdempotentAndSkipsUploadedObjects()
    {
        var harness = PromotionHarness.Create(failOnPublicKeyOnce: "data/publications/parcels/v1/collection.json");

        var firstAttempt = () => harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        harness.PublicObjects.Keys.Should().Contain("data/publications/parcels/v1/parcels.pmtiles");
        harness.PublicObjects.Keys.Should().Contain("data/publications/parcels/v1/parcels.parquet");
        harness.PublicObjects.Keys.Should().NotContain("data/catalog.json");
        var uploadsBeforeRetry = harness.PublicWrites.Count;

        var secondResult = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        secondResult.Status.Should().Be(PublicExportDeliveryStatus.Promoted);
        var retryWrites = harness.PublicWrites.Skip(uploadsBeforeRetry).Select(w => w.Key).ToArray();
        retryWrites.Should().Equal(
            "data/publications/parcels/v1/collection.json",
            "data/catalog.json");
    }

    [UnitTest]
    public async Task PromoteAsync_CanaryFailure_RecordsUnreadableAndPostsOpsFinding()
    {
        var harness = PromotionHarness.Create(canaryResult: PublicExportCanaryResult.Unreadable("Public URL returned HTTP 403."));

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.PromotedUnreadable);
        result.Reason.Should().Contain(OpsNotificationPublicExportNotifier.CanaryFindingTitle);
        result.Reason.Should().Contain("403");

        var saved = await harness.StateStore.GetPromotedAsync("parcels", CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.CanaryState.Should().Be(PublicExportCanaryState.Unreadable);
        harness.CanaryNotices.Should().ContainSingle(n => n.PublicationId == "parcels" && n.Detail.Contains("403"));
    }

    private sealed class PromotionHarness
    {
        public required PublicExportPromoter Promoter { get; init; }
        public required PublicExportPromotionInput Input { get; init; }
        public required InMemoryPublicExportStateStore StateStore { get; init; }
        public required List<(string Key, StoredObjectCachePolicy CachePolicy)> PublicWrites { get; init; }
        public required Dictionary<string, byte[]> PublicObjects { get; init; }
        public required List<(string PublicationId, string Version, Uri Url, string Detail)> CanaryNotices { get; init; }

        public static PromotionHarness Create(
            bool destinationConfigured = true,
            bool multiTenancyEnabled = false,
            bool anonymousAllowed = true,
            string? permanentFilter = null,
            SqlFragment? rlsFilter = null,
            bool hasHiddenField = false,
            string[]? maskedFields = null,
            string? failOnPublicKeyOnce = null,
            PublicExportCanaryResult? canaryResult = null)
        {
            var pmtilesBytes = Encoding.UTF8.GetBytes("pmtiles-bytes");
            var parquetBytes = Encoding.UTF8.GetBytes("parquet-bytes");
            var readmeBytes = Encoding.UTF8.GetBytes("# Parcels");
            var collectionBytes = Encoding.UTF8.GetBytes("{\"type\":\"Collection\"}");
            var pmtilesSha = Convert.ToHexStringLower(SHA256.HashData(pmtilesBytes));
            var parquetSha = Convert.ToHexStringLower(SHA256.HashData(parquetBytes));

            var privateFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["publications/parcels/v1/parcels.pmtiles"] = pmtilesBytes,
                ["publications/parcels/v1/parcels.parquet"] = parquetBytes,
                ["publications/parcels/v1/README.md"] = readmeBytes,
                ["publications/parcels/v1/collection.json"] = collectionBytes
            };
            var privateStorage = CreateReadStorageMock(privateFiles);

            var publicWrites = new List<(string Key, StoredObjectCachePolicy CachePolicy)>();
            var publicObjects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var publicMetadata = new Dictionary<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
            var failedOnce = false;
            var publicStorage = CreatePublicStorageMock(publicWrites, publicObjects, publicMetadata, failOnPublicKeyOnce, () =>
            {
                if (failedOnce)
                {
                    return false;
                }
                failedOnce = true;
                return true;
            });

            var evaluator = new Mock<IAccessPolicyEvaluator>();
            evaluator.Setup(e => e.Evaluate(
                    It.IsAny<ClaimsPrincipal>(),
                    It.IsAny<AccessPolicy?>(),
                    It.IsAny<AccessPolicy?>(),
                    It.IsAny<object?>()))
                .Returns(anonymousAllowed ? AccessDecision.Allowed() : AccessDecision.RequiresAuth("Authentication required."));

            var rlsSource = new Mock<IRowLevelSecurityFilterSource>();
            rlsSource.Setup(s => s.ResolveAsync(It.IsAny<MetadataV2Object>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(rlsFilter);

            var fieldMaskSource = new Mock<IFieldMaskSource>();
            fieldMaskSource.Setup(s => s.ResolveAsync(It.IsAny<MetadataV2Object>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(maskedFields is null ? ImmutableArray<string>.Empty : [.. maskedFields]);

            var services = new ServiceCollection();
            services.AddSingleton(evaluator.Object);
            services.AddSingleton(rlsSource.Object);
            services.AddSingleton(fieldMaskSource.Object);
            var provider = services.BuildServiceProvider();

            var eligibility = new PublicExportEligibility(
                provider,
                Options.Create(new TenantIsolationOptions { Enabled = multiTenancyEnabled }),
                Options.Create(new TenantContextOptions { Enabled = multiTenancyEnabled }));

            var stateStore = new InMemoryPublicExportStateStore();
            var canary = new Mock<IPublicExportCanary>();
            canary.Setup(c => c.CheckAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(canaryResult ?? PublicExportCanaryResult.Readable);

            var canaryNotices = new List<(string, string, Uri, string)>();
            var notifier = new Mock<IPublicExportOpsNotifier>();
            notifier.Setup(n => n.NotifyCanaryFailedAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, Uri, string, CancellationToken>((id, ver, url, detail, _) =>
                    canaryNotices.Add((id, ver, url, detail)))
                .Returns(Task.CompletedTask);

            IPublicPublicationDestination? destination = destinationConfigured
                ? new PublicPublicationDestination(publicStorage.Object, new Uri("https://cdn.example.test/"))
                : null;

            var promoter = new PublicExportPromoter(
                privateStorage.Object, eligibility, stateStore, canary.Object, notifier.Object, destination);

            var resource = new MetadataV2Object
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels", Title = "Parcels" },
                PermanentFilter = permanentFilter is null ? null : new MetadataV2PermanentFilter { Expression = permanentFilter },
                SchemaFields =
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Hidden = false },
                    new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String, Hidden = hasHiddenField }
                ]
            };
            var service = new MetadataV2Service
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = "parcels-svc" }
            };
            var descriptor = new PublicationDescriptor
            {
                PublicationId = "parcels",
                Version = "v1",
                Title = "Parcels",
                PublishedAt = DateTimeOffset.UtcNow,
                SourceLayer = new PublicationSourceLayer("svc-parcels", "res-parcels"),
                Capture = new PublicationCapture("cap-1", DateTimeOffset.UtcNow, "rev-1", 10),
                Bounds = [-122, 37, -121, 38],
                Crs = new PublicationCrs("EPSG:4326", "OGC:CRS84"),
                Fields = [new PublicationField("objectid", "integer", true)],
                Identity = new PublicationIdentity("objectid", "objectid", "objectid"),
                Geometry = new PublicationGeometry("geom", "WKB", ["Polygon"]),
                Artifacts = new PublicationArtifacts(
                    new PmtilesPublicationArtifact
                    {
                        Href = "parcels.pmtiles", Bytes = pmtilesBytes.Length, Sha256 = pmtilesSha,
                        CaptureId = "cap-1", Fields = ["objectid"], VectorLayer = "layer", Minzoom = 0, Maxzoom = 10
                    },
                    new GeoparquetPublicationArtifact
                    {
                        Href = "parcels.parquet", Bytes = parquetBytes.Length, Sha256 = parquetSha,
                        CaptureId = "cap-1", Fields = ["objectid"]
                    }),
                Validation = new PublicationValidation(PublicationValidation.Passed, DateTimeOffset.UtcNow, [])
            };

            var store = PairedPublicationStore.Create(
                privateStorage.Object,
                new CloudStorageOptions { PMTilesPublish = new PMTilesPublishOptions { KeyPrefix = string.Empty } },
                "parcels");
            var input = new PublicExportPromotionInput(descriptor, store, resource, service, StorageLayerId: 7, LayerIndex: 0, MetadataRevision: 12);

            return new PromotionHarness
            {
                Promoter = promoter,
                Input = input,
                StateStore = stateStore,
                PublicWrites = publicWrites,
                PublicObjects = publicObjects,
                CanaryNotices = canaryNotices
            };
        }

        private static Mock<ICloudFileStorage> CreateReadStorageMock(Dictionary<string, byte[]> files)
        {
            var storage = new Mock<ICloudFileStorage>();
            storage.Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => files.TryGetValue(key, out var bytes)
                    ? new CloudFile
                    {
                        FileId = key, FileName = Path.GetFileName(key), StoragePath = key,
                        ContentType = "application/octet-stream", SizeBytes = bytes.Length,
                        UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.AwsS3
                    }
                    : null);
            storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => files.TryGetValue(key, out var bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null);
            return storage;
        }

        private static Mock<ICloudFileStorage> CreatePublicStorageMock(
            List<(string Key, StoredObjectCachePolicy CachePolicy)> writes,
            Dictionary<string, byte[]> objects,
            Dictionary<string, ImmutableDictionary<string, string>> metadataMap,
            string? failOnKeyOnce,
            Func<bool> consumeFailure)
        {
            var storage = new Mock<ICloudFileStorage>();
            storage.Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => objects.TryGetValue(key, out var bytes)
                    ? new CloudFile
                    {
                        FileId = key, FileName = Path.GetFileName(key), StoragePath = key,
                        ContentType = "application/octet-stream", SizeBytes = bytes.Length,
                        UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.AwsS3,
                        Metadata = metadataMap.TryGetValue(key, out var meta) ? meta : ImmutableDictionary<string, string>.Empty
                    }
                    : null);
            storage.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FileUploadRequest request, CancellationToken _) =>
                {
                    var key = request.ObjectKeyOverride!;
                    if (key == failOnKeyOnce && consumeFailure())
                    {
                        return UploadResult.CreateFailure("Simulated crash before pointer.");
                    }

                    using var copy = new MemoryStream();
                    request.Content.CopyTo(copy);
                    objects[key] = copy.ToArray();
                    metadataMap[key] = request.Metadata;
                    writes.Add((key, request.CachePolicy));
                    return UploadResult.CreateSuccess(new CloudFile
                    {
                        FileId = key, FileName = request.FileName, StoragePath = key,
                        ContentType = request.ContentType, SizeBytes = objects[key].Length,
                        UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.AwsS3,
                        Metadata = request.Metadata
                    }, TimeSpan.Zero);
                });
            return storage;
        }
    }

    internal sealed class InMemoryPublicExportStateStore : IPublicExportStateStore
    {
        public Dictionary<string, PromotedExportRecord> Promoted { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PublicExportRevocationRecord> Revocations { get; } = new(StringComparer.Ordinal);

        public Task SavePromotedAsync(PromotedExportRecord record, CancellationToken cancellationToken)
        {
            Promoted[record.PublicationId] = record;
            return Task.CompletedTask;
        }

        public Task<PromotedExportRecord?> GetPromotedAsync(string publicationId, CancellationToken cancellationToken)
            => Task.FromResult(Promoted.GetValueOrDefault(publicationId));

        public Task DeletePromotedAsync(string publicationId, CancellationToken cancellationToken)
        {
            Promoted.Remove(publicationId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PromotedExportRecord>> ListPromotedAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PromotedExportRecord>>([.. Promoted.Values]);

        public Task SaveRevocationAsync(PublicExportRevocationRecord record, CancellationToken cancellationToken)
        {
            Revocations[record.RevocationId] = record;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PublicExportRevocationRecord>> ListPendingRevocationsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PublicExportRevocationRecord>>(
                [.. Revocations.Values.Where(r => r.State != PublicExportRevocationState.Completed)]);
    }
}
