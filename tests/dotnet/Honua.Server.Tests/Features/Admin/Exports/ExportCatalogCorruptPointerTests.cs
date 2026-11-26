// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Admin.Exports;

public sealed class ExportCatalogCorruptPointerTests
{
    private const string BrokenExportId = "maui-county-parcels";
    private const string HealthyExportId = "healthy-parcels";
    private const string StoredVersion = "20261113T062701Z";
    private static readonly byte[] CorruptJson = Encoding.UTF8.GetBytes("{corrupt");

    [UnitTest]
    public async Task GetAsync_WhenCurrentPointerIsCorrupt_ReturnsStoredVersionsNotPublicAndLogsWarning()
    {
        var storage = CreateStorageWithCorruptPointer(includeHealthySibling: false);
        var logger = new RecordingLogger<ExportCatalog>();
        var catalog = CreateCatalog(storage, logger, [CompletedExportJob(BrokenExportId)]);

        var export = await catalog.GetAsync(BrokenExportId, "http://honua", CancellationToken.None);

        export.Should().NotBeNull();
        export!.Id.Should().Be(BrokenExportId);
        export.Status.Should().Be(ExportResponse.Unpublished);
        export.CurrentVersion.Should().BeNull();
        export.CollectionUrl.Should().BeNull();
        export.PublicUrl.Should().BeNull();
        export.JobId.Should().Be("job-corrupt");
        export.Versions.Select(v => v.Version).Should().Equal(StoredVersion);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.EventId.Id == 10345);
    }

    [UnitTest]
    public async Task ListAsync_WhenOneExportHasCorruptPointer_ReturnsAllExportsAndLogsWarning()
    {
        var storage = CreateStorageWithCorruptPointer(includeHealthySibling: true);
        var logger = new RecordingLogger<ExportCatalog>();
        var catalog = CreateCatalog(storage, logger, [CompletedExportJob(BrokenExportId)]);

        var items = await catalog.ListAsync("http://honua", new ExportListPage(), CancellationToken.None);

        items.Select(item => item.Id).Should().Equal(HealthyExportId, BrokenExportId);
        var healthy = items.Single(item => item.Id == HealthyExportId);
        healthy.Status.Should().Be(ExportResponse.Ready);
        healthy.CurrentVersion.Should().Be(StoredVersion);

        var broken = items.Single(item => item.Id == BrokenExportId);
        broken.Status.Should().Be(ExportResponse.Unpublished);
        broken.CurrentVersion.Should().BeNull();
        broken.PublicUrl.Should().BeNull();
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.EventId.Id == 10345);
    }

    [UnitTest]
    public async Task ListAsync_WhenPointerOnlyExportIsCorrupt_SkipsItWithoutFailingList()
    {
        var options = new CloudStorageOptions();
        var storage = new FakePublicStorage();
        var brokenStore = ExportStore.Create(storage.Storage.Object, options, "corrupt-only");
        storage.Objects[brokenStore.PointerKey] = CorruptJson;
        SeedHealthyExport(storage, options, HealthyExportId);
        WireDownloads(storage);
        var logger = new RecordingLogger<ExportCatalog>();
        var catalog = CreateCatalog(storage, logger, []);

        var items = await catalog.ListAsync("http://honua", new ExportListPage(), CancellationToken.None);

        items.Select(item => item.Id).Should().Equal(HealthyExportId);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.EventId.Id == 10345);
    }

    [UnitTest]
    public async Task UnpublishAsync_WhenCurrentPointerIsCorrupt_PreservesSourceIdentityAndDeletesPointer()
    {
        var options = new CloudStorageOptions();
        var storage = CreateStorageWithCorruptPointer(includeHealthySibling: false);
        var store = ExportStore.Create(storage.Storage.Object, options, BrokenExportId);
        var logger = new RecordingLogger<ExportCatalog>();
        var catalog = CreateCatalog(storage, logger, []);

        var deleted = await catalog.UnpublishAsync(BrokenExportId, CancellationToken.None);

        deleted.Should().BeTrue();
        storage.Objects.Should().NotContainKey(store.PointerKey);
        var marker = await store.ReadUnpublishedAsync(CancellationToken.None);
        marker.Should().NotBeNull();
        marker!.ExportId.Should().Be(BrokenExportId);
        marker.ServiceId.Should().NotBeNullOrWhiteSpace();
        marker.LayerId.Should().NotBeNullOrWhiteSpace();
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.EventId.Id == 10345);
    }

    private static FakePublicStorage CreateStorageWithCorruptPointer(bool includeHealthySibling)
    {
        var options = new CloudStorageOptions();
        var storage = new FakePublicStorage();
        var brokenStore = ExportStore.Create(storage.Storage.Object, options, BrokenExportId);
        storage.Objects[brokenStore.PointerKey] = CorruptJson;
        storage.Objects[brokenStore.VersionKey(StoredVersion, ExportCollectionDocument.FileName)] = ReadSampleCollection();
        if (includeHealthySibling)
        {
            SeedHealthyExport(storage, options, HealthyExportId);
        }

        WireDownloads(storage);
        return storage;
    }

    private static void SeedHealthyExport(FakePublicStorage storage, CloudStorageOptions options, string exportId)
    {
        var store = ExportStore.Create(storage.Storage.Object, options, exportId);
        var pointer = new ExportPointer(exportId, StoredVersion, $"{StoredVersion}/collection.json", DateTimeOffset.UtcNow);
        storage.Objects[store.PointerKey] = JsonSerializer.SerializeToUtf8Bytes(pointer, ExportVersionJsonContext.Default.ExportPointer);
        var collectionText = Encoding.UTF8.GetString(ReadSampleCollection()).Replace(BrokenExportId, exportId, StringComparison.Ordinal);
        storage.Objects[store.VersionKey(StoredVersion, ExportCollectionDocument.FileName)] = Encoding.UTF8.GetBytes(collectionText);
    }

    private static void WireDownloads(FakePublicStorage storage)
        => storage.Storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                storage.Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

    private static byte[] ReadSampleCollection()
        => File.ReadAllBytes(RepositoryPaths.Resolve(
            "docs", "gis", "schemas", "export-collection.v1", "maui-county-parcels.json"));

    private static ExportCatalog CreateCatalog(
        FakePublicStorage storage,
        RecordingLogger<ExportCatalog> logger,
        IReadOnlyList<TileOperationProgress> exportJobs)
    {
        var jobs = new Mock<ITileCacheJobService>();
        jobs.Setup(service => service.ListAsync(TileOperationListScope.All, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exportJobs);
        return new ExportCatalog(new ExportCatalogDependencies
        {
            Storage = storage.Storage.Object,
            Options = Options.Create(new CloudStorageOptions()),
            Jobs = jobs.Object,
            Logger = logger
        });
    }

    private static TileOperationProgress CompletedExportJob(string exportId) => new()
    {
        JobId = "job-corrupt",
        Operation = TileCacheExecutionSpecBuilder.ExportOperation,
        Status = OperationStatus.Completed,
        ExportId = exportId,
        ServiceId = "svc-parcels",
        LayerId = 10,
        PublishedExportVersion = new PublishedExportVersion(exportId, StoredVersion, "capture", "collection", "pointer")
        {
            Public = new PublicExportDeliveryStatus(PublicExportDeliveryStatus.Promoted)
            {
                CollectionUrl = $"https://public.example/data/publications/{exportId}/{StoredVersion}/collection.json"
            }
        }
    };

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
