// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Admin.Exports;

public sealed class ExportCatalogPaginationTests
{
    [UnitTest]
    public async Task ListAsync_CountsOnlyExportsThatCanBeDescribed()
    {
        var options = new CloudStorageOptions();
        var storage = new FakePublicStorage();
        const string missingExportId = "archived-parcels";
        const string missingVersion = "20261125T012207Z";
        var pointerKey = ExportStore.Create(storage.Storage.Object, options, missingExportId).PointerKey;
        var pointer = new ExportPointer(missingExportId, missingVersion, $"{missingVersion}/collection.json", DateTimeOffset.UtcNow);
        storage.Objects[pointerKey] = JsonSerializer.SerializeToUtf8Bytes(pointer, ExportVersionJsonContext.Default.ExportPointer);
        const string historicalExportId = "maui-county-parcels";
        var historical = ExportStore.Create(storage.Storage.Object, options, historicalExportId);
        storage.Objects[historical.VersionKey("20261113T062701Z", ExportCollectionDocument.FileName)] =
            await File.ReadAllBytesAsync(RepositoryPaths.Resolve(
                "docs", "gis", "schemas", "export-collection.v1", "maui-county-parcels.json"));
        storage.Storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                storage.Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
        var jobs = new Mock<ITileCacheJobService>();
        jobs.Setup(service => service.ListAsync(TileOperationListScope.All, It.IsAny<CancellationToken>()))
            .ReturnsAsync([QueuedExport("roads")]);
        var catalog = new ExportCatalog(new ExportCatalogDependencies
        {
            Storage = storage.Storage.Object,
            Options = Options.Create(options),
            Jobs = jobs.Object
        });

        var firstPage = await catalog.ListAsync("http://honua", new ExportListPage(Limit: 1, Offset: 0), CancellationToken.None);
        var secondPage = await catalog.ListAsync("http://honua", new ExportListPage(Limit: 1, Offset: 1), CancellationToken.None);
        var lastPage = await catalog.ListAsync("http://honua", new ExportListPage(Limit: 1, Offset: 2), CancellationToken.None);

        firstPage.Select(item => item.Id).Should().Equal(historicalExportId);
        secondPage.Select(item => item.Id).Should().Equal("roads");
        lastPage.Should().BeEmpty();
        storage.Storage.Verify(s => s.DownloadAsync(historical.PointerKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static TileOperationProgress QueuedExport(string exportId)
        => TileOperationProgress.CreateInitial(
            $"job-{exportId}", TileCacheExecutionSpecBuilder.ExportOperation, "service", 1, "WebMercatorQuad") with
        {
            ExportId = exportId
        };
}
