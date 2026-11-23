// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Admin.Exports;

// A revocation deletes the promoted record but leaves the pointer and the job that promoted the
// Version, so the admin view must not keep advertising the deleted public copy.
public sealed class ExportCatalogPublicUrlTests
{
    private const string ExportId = "maui-county-parcels";
    private const string CurrentVersion = "20261113T062701Z";
    private const string PublicCollectionUrl =
        "https://public.example/data/publications/maui-county-parcels/20261113T062701Z/collection.json";

    [UnitTheory]
    [InlineData(null, null)]
    [InlineData("20261101T000000Z", null)]
    [InlineData(CurrentVersion, PublicCollectionUrl)]
    public async Task GetAsync_AdvertisesPublicUrlOnlyWhilePromotedRecordNamesCurrentVersion(
        string? promotedVersion, string? expectedPublicUrl)
    {
        var catalog = CreateCatalog(CreateStateStore(promotedVersion));

        var export = await catalog.GetAsync(ExportId, "http://honua", CancellationToken.None);

        export.Should().NotBeNull();
        export!.Status.Should().Be(ExportResponse.Ready);
        export.PublicUrl.Should().Be(expectedPublicUrl);
        export.NotPublicReason.Should().Be(expectedPublicUrl is null ? ExportCatalog.PublicCopyWithdrawnReason : null);
    }

    [UnitTest]
    public async Task ListAsync_WhenPromotedRecordWasRevoked_DoesNotAdvertisePublicUrl()
    {
        var catalog = CreateCatalog(CreateStateStore(promotedVersion: null));

        var items = await catalog.ListAsync("http://honua", new ExportListPage(), CancellationToken.None);

        items.Should().ContainSingle(item => item.Id == ExportId)
            .Which.PublicUrl.Should().BeNull();
    }

    [UnitTest]
    public async Task GetAsync_WithoutPublicStateStore_KeepsJobDelivery()
    {
        var catalog = CreateCatalog(publicState: null);

        var export = await catalog.GetAsync(ExportId, "http://honua", CancellationToken.None);

        export!.PublicUrl.Should().Be(PublicCollectionUrl);
    }

    private static InMemoryPublicExportStateStore CreateStateStore(string? promotedVersion)
    {
        var store = new InMemoryPublicExportStateStore();
        if (promotedVersion is not null)
        {
            store.Promoted[ExportId] = new PromotedExportRecord
            {
                PublicationId = ExportId,
                Version = promotedVersion,
                ServiceId = "svc-parcels",
                ResourceId = "res-parcels",
                CollectionUrl = PublicCollectionUrl,
                CatalogUrl = "https://public.example/data/catalog.json"
            };
        }

        return store;
    }

    private static ExportCatalog CreateCatalog(IPublicExportStateStore? publicState)
    {
        var jobs = new Mock<ITileCacheJobService>();
        jobs.Setup(j => j.ListAsync(It.IsAny<TileOperationListScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([PromotingJob()]);
        return new ExportCatalog(new ExportCatalogDependencies
        {
            Storage = CreateStorage(),
            Options = Options.Create(new CloudStorageOptions()),
            Jobs = jobs.Object,
            PublicState = publicState
        });
    }

    private static TileOperationProgress PromotingJob() => new()
    {
        JobId = "tile-job-1",
        Operation = TileCacheExecutionSpecBuilder.ExportOperation,
        Status = OperationStatus.Completed,
        ExportId = ExportId,
        PublishedExportVersion = new PublishedExportVersion(ExportId, CurrentVersion, "capture", "collection", "pointer")
        {
            Public = new PublicExportDeliveryStatus(PublicExportDeliveryStatus.Promoted) { CollectionUrl = PublicCollectionUrl }
        }
    };

    private static ICloudFileStorage CreateStorage()
    {
        var root = PMTilesPublishKeyRoot.Resolve(new CloudStorageOptions());
        var prefix = string.IsNullOrEmpty(root) ? $"exports/{ExportId}" : $"{root}/exports/{ExportId}";
        var pointer = JsonSerializer.SerializeToUtf8Bytes(
            new ExportPointer(ExportId, CurrentVersion, $"{CurrentVersion}/collection.json", DateTimeOffset.UtcNow),
            ExportVersionJsonContext.Default.ExportPointer);
        var collection = Encoding.UTF8.GetBytes(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "gis", "schemas", "export-collection.v1", "maui-county-parcels.json")));
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{prefix}/current.json"] = pointer,
            [$"{prefix}/{CurrentVersion}/collection.json"] = collection
        };
        var storage = new Mock<ICloudFileStorage>();
        storage.Setup(s => s.ListFilesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
        return storage.Object;
    }
}
