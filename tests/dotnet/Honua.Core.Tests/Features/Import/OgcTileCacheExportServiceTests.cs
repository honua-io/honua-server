// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Tests for the slice-4 XYZ/TMS tile-cache exporter
/// (<see cref="OgcTileCacheExportService"/>).
/// </summary>
public sealed class OgcTileCacheExportServiceTests
{
    private const string ServiceUrl = "https://wmts.example.test/wmts";
    private const string LayerId = "basemap";
    private const string TileMatrixSetId = "WebMercatorQuad";

    [Fact]
    public async Task ExportAsync_AutomatedTileSet_DeterministicallyFetchesAndPersistsTiles()
    {
        var inventory = BuildWmtsInventory(trivial: true);
        var handler = new CountingTileHandler();
        using var httpClient = new HttpClient(handler);
        var sink = new InMemoryTileCacheSink();
        var service = new OgcTileCacheExportService(
            new InMemoryScanner(inventory),
            sink,
            httpClient,
            NullLogger<OgcTileCacheExportService>.Instance);

        var result = await service.ExportAsync(new OgcTileCacheExportRequest
        {
            ServiceUrl = ServiceUrl,
            ApplyMode = true,
            AllowUnsafeLocalUrls = true,
            MinZoom = 0,
            MaxZoom = 1
        });

        result.Success.Should().BeTrue();
        result.WasDryRun.Should().BeFalse();
        result.TileSetsPlanned.Should().Be(1);
        result.TileSetsExported.Should().Be(1);
        result.TileSetsSkipped.Should().Be(0);
        // Zoom 0: 1 tile, Zoom 1: 4 tiles, total 5 tiles.
        result.TilesPersisted.Should().Be(5);
        result.TilesAlreadyPresent.Should().Be(0);
        result.TilesFailed.Should().Be(0);

        var tileSet = result.TileSets.Should().ContainSingle().Subject;
        tileSet.LayerIdentifier.Should().Be(LayerId);
        tileSet.TileMatrixSetIdentifier.Should().Be(TileMatrixSetId);
        tileSet.Classification.Should().Be(MigrationFidelityAutomationStatuses.Automated);
        tileSet.Code.Should().Be(ImportCompatibilityCodes.OgcWmtsTileCacheExported);
        tileSet.TargetTileCacheId.Should().NotBeNullOrWhiteSpace();

        handler.RequestUris.Should().HaveCount(5);
        sink.Records.Should().HaveCount(5);
    }

    [Fact]
    public async Task ExportAsync_DryRun_DoesNotFetchTilesOrWriteSink()
    {
        var inventory = BuildWmtsInventory(trivial: true);
        var handler = new CountingTileHandler();
        using var httpClient = new HttpClient(handler);
        var sink = new InMemoryTileCacheSink();
        var service = new OgcTileCacheExportService(
            new InMemoryScanner(inventory),
            sink,
            httpClient,
            NullLogger<OgcTileCacheExportService>.Instance);

        var result = await service.ExportAsync(new OgcTileCacheExportRequest
        {
            ServiceUrl = ServiceUrl,
            DryRun = true,
            AllowUnsafeLocalUrls = true,
            MinZoom = 0,
            MaxZoom = 1
        });

        result.Success.Should().BeTrue();
        result.WasDryRun.Should().BeTrue();
        result.TilesPersisted.Should().Be(0);
        result.TilesFailed.Should().Be(0);
        handler.RequestUris.Should().BeEmpty();
        sink.Records.Should().BeEmpty();
        sink.EnsureCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_RerunWithSameSink_IsIdempotent()
    {
        var inventory = BuildWmtsInventory(trivial: true);
        var sink = new InMemoryTileCacheSink();

        var firstHandler = new CountingTileHandler();
        using (var firstClient = new HttpClient(firstHandler))
        {
            var first = new OgcTileCacheExportService(
                new InMemoryScanner(inventory),
                sink,
                firstClient,
                NullLogger<OgcTileCacheExportService>.Instance);
            var firstResult = await first.ExportAsync(new OgcTileCacheExportRequest
            {
                ServiceUrl = ServiceUrl,
                ApplyMode = true,
                AllowUnsafeLocalUrls = true,
                MinZoom = 0,
                MaxZoom = 0
            });
            firstResult.TilesPersisted.Should().Be(1);
            firstResult.TilesAlreadyPresent.Should().Be(0);
        }

        var secondHandler = new CountingTileHandler();
        using var secondClient = new HttpClient(secondHandler);
        var second = new OgcTileCacheExportService(
            new InMemoryScanner(inventory),
            sink,
            secondClient,
            NullLogger<OgcTileCacheExportService>.Instance);

        var secondResult = await second.ExportAsync(new OgcTileCacheExportRequest
        {
            ServiceUrl = ServiceUrl,
            ApplyMode = true,
            AllowUnsafeLocalUrls = true,
            MinZoom = 0,
            MaxZoom = 0
        });

        secondResult.Success.Should().BeTrue();
        secondResult.TilesPersisted.Should().Be(0, "the sink already holds the tile");
        secondResult.TilesAlreadyPresent.Should().Be(1);
        secondHandler.RequestUris.Should().HaveCount(1, "rerun still fetches the tile body, but the sink rejects the duplicate write");
        sink.Records.Should().HaveCount(1, "no duplicate row should be created on rerun");
    }

    [Fact]
    public async Task ExportAsync_RequestedZoomBeyondSafetyThreshold_ReclassifiesAsManualReview()
    {
        var inventory = BuildWmtsInventory(trivial: true);
        var handler = new CountingTileHandler();
        using var httpClient = new HttpClient(handler);
        var sink = new InMemoryTileCacheSink();
        var service = new OgcTileCacheExportService(
            new InMemoryScanner(inventory),
            sink,
            httpClient,
            NullLogger<OgcTileCacheExportService>.Instance);

        var result = await service.ExportAsync(new OgcTileCacheExportRequest
        {
            ServiceUrl = ServiceUrl,
            ApplyMode = true,
            AllowUnsafeLocalUrls = true,
            MinZoom = 0,
            MaxZoom = OgcTileCacheExportSafetyLimits.MaxAutomatedZoomLevel + 1
        });

        result.Success.Should().BeTrue();
        result.TileSetsPlanned.Should().Be(1);
        result.TileSetsExported.Should().Be(0);
        result.TileSetsSkipped.Should().Be(1);
        result.TilesPersisted.Should().Be(0);
        handler.RequestUris.Should().BeEmpty();

        var tileSet = result.TileSets.Should().ContainSingle().Subject;
        tileSet.Classification.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        tileSet.Code.Should().Be(ImportCompatibilityCodes.OgcWmtsTileCacheZoomThresholdExceeded);
    }

    [Fact]
    public async Task ExportAsync_NonTrivialTileMatrixSet_IsSkippedAsManualReview()
    {
        var inventory = BuildWmtsInventory(trivial: false);
        var handler = new CountingTileHandler();
        using var httpClient = new HttpClient(handler);
        var sink = new InMemoryTileCacheSink();
        var service = new OgcTileCacheExportService(
            new InMemoryScanner(inventory),
            sink,
            httpClient,
            NullLogger<OgcTileCacheExportService>.Instance);

        var result = await service.ExportAsync(new OgcTileCacheExportRequest
        {
            ServiceUrl = ServiceUrl,
            ApplyMode = true,
            AllowUnsafeLocalUrls = true,
            MinZoom = 0,
            MaxZoom = 1
        });

        result.Success.Should().BeTrue();
        result.TileSetsPlanned.Should().Be(1);
        result.TileSetsSkipped.Should().Be(1);
        result.TilesPersisted.Should().Be(0);
        var tileSet = result.TileSets.Should().ContainSingle().Subject;
        tileSet.Classification.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        tileSet.Code.Should().Be(ImportCompatibilityCodes.OgcWmtsTileCacheSkippedManualReview);
    }

    [Fact]
    public async Task ExportAsync_TileFetchFailure_PropagatesAsTileSetWarning()
    {
        var inventory = BuildWmtsInventory(trivial: true);
        var handler = new FailingTileHandler();
        using var httpClient = new HttpClient(handler);
        var sink = new InMemoryTileCacheSink();
        var service = new OgcTileCacheExportService(
            new InMemoryScanner(inventory),
            sink,
            httpClient,
            NullLogger<OgcTileCacheExportService>.Instance);

        var result = await service.ExportAsync(new OgcTileCacheExportRequest
        {
            ServiceUrl = ServiceUrl,
            ApplyMode = true,
            AllowUnsafeLocalUrls = true,
            MinZoom = 0,
            MaxZoom = 0
        });

        result.Success.Should().BeTrue();
        result.TilesPersisted.Should().Be(0);
        result.TilesFailed.Should().Be(1);
        var tileSet = result.TileSets.Should().ContainSingle().Subject;
        tileSet.Classification.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        tileSet.Code.Should().Be(ImportCompatibilityCodes.OgcWmtsTileCacheFetchFailed);
        tileSet.Warnings.Should().NotBeEmpty();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_ScannerFailure_ReturnsFailureResult()
    {
        var handler = new CountingTileHandler();
        using var httpClient = new HttpClient(handler);
        var sink = new InMemoryTileCacheSink();
        var service = new OgcTileCacheExportService(
            new ThrowingScanner(),
            sink,
            httpClient,
            NullLogger<OgcTileCacheExportService>.Instance);

        var result = await service.ExportAsync(new OgcTileCacheExportRequest
        {
            ServiceUrl = ServiceUrl,
            ApplyMode = true,
            AllowUnsafeLocalUrls = true
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("scan WMTS source");
        handler.RequestUris.Should().BeEmpty();
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_NonWmtsInventory_ReturnsFailureResult()
    {
        var inventory = BuildWmtsInventory(trivial: true) with { SourceKind = "ogc-wfs" };
        var handler = new CountingTileHandler();
        using var httpClient = new HttpClient(handler);
        var sink = new InMemoryTileCacheSink();
        var service = new OgcTileCacheExportService(
            new InMemoryScanner(inventory),
            sink,
            httpClient,
            NullLogger<OgcTileCacheExportService>.Instance);

        var result = await service.ExportAsync(new OgcTileCacheExportRequest
        {
            ServiceUrl = ServiceUrl,
            ApplyMode = true,
            AllowUnsafeLocalUrls = true
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("WMTS endpoint");
    }

    private static MigrationSourceInventoryArtifact BuildWmtsInventory(bool trivial)
    {
        const string containerId = "service:wmts";
        var tileMatrixName = trivial ? TileMatrixSetId : "CustomGrid";
        var tileMatrixIdValue = trivial ? "tile-matrix-set:webmercatorquad" : "tile-matrix-set:custom-grid";

        var resources = new[]
        {
            new MigrationInventoryResource
            {
                Id = $"wmts-layer:{LayerId}",
                ContainerId = containerId,
                Kind = "tile-layer",
                Name = LayerId,
                Title = "Basemap",
                Capabilities = ["wmts:GetCapabilities", "wmts:GetTile"],
                ExternalDependencyIds = [tileMatrixIdValue],
                Compatibility = new MigrationCompatibilityAssessment
                {
                    Level = "incompatible",
                    Code = ImportCompatibilityCodes.OgcWmtsTileOnlySource,
                    Reason = "WMTS exposes pre-rendered tiles."
                }
            }
        };

        var dependencies = new[]
        {
            new MigrationExternalDependency
            {
                Id = tileMatrixIdValue,
                ContainerId = containerId,
                Kind = "tile-matrix-set",
                Name = tileMatrixName,
                DependencyType = "WMTS TileMatrixSet",
                Compatibility = Partial("Tile matrix set captured.", ImportCompatibilityCodes.OgcWmtsTileOnlySource)
            }
        };

        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = containerId,
                Kind = "ogc-service",
                Name = "WMTS",
                Title = "Reference WMTS",
                IsDefault = true,
                Compatibility = Partial("Container captured.", ImportCompatibilityCodes.ManualReview)
            }
        };

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = "ogc-wmts",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Reference WMTS",
                BaseUrl = ServiceUrl,
                Product = "OGC WMTS",
                Version = "1.0.0",
                ServiceType = "WMTS"
            },
            AuthPosture = new MigrationInventoryAuthPosture { Mode = "anonymous" },
            ScanCompleteness = new MigrationInventoryCompleteness { Status = "complete" },
            Summary = new MigrationInventorySummary
            {
                ContainerCount = containers.Length,
                ResourceCount = resources.Length,
                ExternalDependencyCount = dependencies.Length
            },
            OverallCompatibility = Partial("Render-only source captured.", ImportCompatibilityCodes.ManualReview),
            Containers = containers,
            Resources = resources,
            ExternalDependencies = dependencies
        };
    }

    private static MigrationCompatibilityAssessment Partial(string reason, string code) => new()
    {
        Level = "partial",
        Code = code,
        Reason = reason
    };

    private sealed class InMemoryScanner(MigrationSourceInventoryArtifact inventory) : IOgcServiceMigrationScanner
    {
        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            OgcServiceScanRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(inventory);
    }

    private sealed class ThrowingScanner : IOgcServiceMigrationScanner
    {
        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            OgcServiceScanRequest request,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("simulated WMTS capabilities outage");
    }

    private sealed class CountingTileHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var payload = Encoding.UTF8.GetBytes($"tile:{request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") }
                }
            });
        }
    }

    private sealed class FailingTileHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("upstream error", Encoding.UTF8, "text/plain")
            });
    }

    private sealed class InMemoryTileCacheSink : IOgcTileCacheSink
    {
        private readonly ConcurrentDictionary<string, byte> _present = new(StringComparer.Ordinal);

        public List<OgcTileCacheRecord> Records { get; } = [];

        public int EnsureCalls { get; private set; }

        public Task<string> EnsureTileCacheAsync(OgcTileCacheDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            return Task.FromResult($"tilecache:{descriptor.LayerIdentifier}:{descriptor.TileMatrixSetIdentifier}");
        }

        public Task<OgcTileCacheWriteStatus> WriteTileAsync(OgcTileCacheRecord record, CancellationToken cancellationToken = default)
        {
            var key = $"{record.TileCacheId}|{record.Z}|{record.X}|{record.Y}";
            if (_present.TryAdd(key, 0))
            {
                Records.Add(record);
                return Task.FromResult(OgcTileCacheWriteStatus.Inserted);
            }

            return Task.FromResult(OgcTileCacheWriteStatus.AlreadyPresent);
        }
    }
}
