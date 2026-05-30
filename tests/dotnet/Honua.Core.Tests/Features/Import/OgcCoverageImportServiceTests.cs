// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Service-level tests for <see cref="OgcCoverageImportService"/> (issue #1030 slice 2).
/// Cover GeoTIFF / COG selection, dry-run vs apply, idempotency, and error propagation.
/// </summary>
public sealed class OgcCoverageImportServiceTests
{
    private const string ServiceUrl = "https://example.com/geoserver/wcs";

    [Fact]
    public async Task ImportAsync_DryRun_BuildsPlannedManifestWithoutDownloadingCoverage()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => throw new InvalidOperationException("Dry-run import must not call the source service."));
        var service = CreateService(handler, rasterImportMock.Object);

        var inventory = BuildInventory(BuildCogResource("dem-cog"), BuildGeoTiffResource("dem-tiff"));
        var request = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = ServiceUrl,
            Inventory = inventory,
            DryRun = true
        };

        var result = await service.ImportAsync(request);

        result.DryRun.Should().BeTrue();
        result.ApplyMode.Should().BeFalse();
        result.Records.Should().HaveCount(2);
        result.Records.Should().OnlyContain(record => record.Action == "planned");

        var cog = result.Records.Single(record => record.SourceCoverageId == "dem-cog");
        cog.OutputFormat.Should().Be("CloudOptimizedGeoTIFF");
        cog.Classification.Should().Be("automated");

        var tiff = result.Records.Single(record => record.SourceCoverageId == "dem-tiff");
        tiff.OutputFormat.Should().Be("GeoTIFF");
        tiff.Classification.Should().Be("automated");

        result.Manifest.Summary.TargetResourceCount.Should().Be(2);
        result.Manifest.TargetResources.Should().OnlyContain(target => target.Action == "planned");
        handler.RequestCount.Should().Be(0);
        rasterImportMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImportAsync_ApplyMode_DownloadsGeoTiffAndRegistersRaster()
    {
        var capturedRequests = new List<RasterImportRequest>();
        var rasterImportMock = new Mock<IRasterImportService>();
        rasterImportMock
            .Setup(static s => s.ImportAsync(It.IsAny<RasterImportRequest>(), null, It.IsAny<CancellationToken>()))
            .Callback<RasterImportRequest, IProgress<RasterImportProgress>?, CancellationToken>((req, _, _) => capturedRequests.Add(req))
            .ReturnsAsync((RasterImportRequest req, IProgress<RasterImportProgress>? _, CancellationToken _) =>
                new RasterImportResult
                {
                    Success = true,
                    RasterId = 4242,
                    LayerId = req.LayerId,
                    Name = req.Name,
                    Format = req.Format
                });

        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = ServiceUrl,
            Version = "2.0.1",
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff")),
            Targets = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal)
            {
                ["dem-tiff"] = new OgcCoverageImportTarget { LayerId = 7, Name = "Elevation" }
            },
            DryRun = false,
            ApplyMode = true
        };

        var result = await service.ImportAsync(request);

        result.ApplyMode.Should().BeTrue();
        result.DryRun.Should().BeFalse();
        var record = result.Records.Should().ContainSingle().Subject;
        record.Action.Should().Be("imported");
        record.RasterId.Should().Be(4242);
        record.TargetLayerId.Should().Be(7);
        record.ByteCount.Should().BeGreaterThan(0);
        handler.RequestCount.Should().Be(1);
        handler.LastRequest!.RequestUri!.Query.Should().Contain("request=GetCoverage");
        handler.LastRequest!.RequestUri!.Query.Should().Contain("coverageId=dem-tiff");
        capturedRequests.Should().ContainSingle();
        capturedRequests[0].Format.Should().Be(SupportedRasterFormat.GeoTiff);
    }

    [Fact]
    public async Task ImportAsync_ApplyMode_CogClassificationRequestsCloudOptimizedFormat()
    {
        var rasterImportMock = new Mock<IRasterImportService>();
        rasterImportMock
            .Setup(static s => s.ImportAsync(It.IsAny<RasterImportRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RasterImportRequest req, IProgress<RasterImportProgress>? _, CancellationToken _) =>
                new RasterImportResult
                {
                    Success = true,
                    RasterId = 99,
                    LayerId = req.LayerId,
                    Name = req.Name,
                    Format = req.Format
                });

        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcCoverageImportRequest
        {
            ServiceType = "OGC API Coverages",
            ServiceUrl = "https://example.com/ogc/collections",
            Inventory = BuildInventory(BuildCogResource("dem-cog")),
            Targets = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal)
            {
                ["dem-cog"] = new OgcCoverageImportTarget { LayerId = 11 }
            },
            DryRun = false,
            ApplyMode = true
        };

        var result = await service.ImportAsync(request);

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputFormat.Should().Be("CloudOptimizedGeoTIFF");
        record.Action.Should().Be("imported");
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("/collections/dem-cog/coverage");
        handler.LastRequest!.RequestUri!.Query.Should().Contain("f=tif");
    }

    [Fact]
    public async Task ImportAsync_ApplyMode_TwiceProducesIdenticalManifests()
    {
        var rasterImportMock = new Mock<IRasterImportService>();
        rasterImportMock
            .Setup(static s => s.ImportAsync(It.IsAny<RasterImportRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RasterImportRequest req, IProgress<RasterImportProgress>? _, CancellationToken _) =>
                new RasterImportResult
                {
                    Success = true,
                    RasterId = 100,
                    LayerId = req.LayerId,
                    Name = req.Name,
                    Format = req.Format
                });

        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var inventory = BuildInventory(BuildGeoTiffResource("dem-tiff"));
        var request = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = ServiceUrl,
            Inventory = inventory,
            Targets = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal)
            {
                ["dem-tiff"] = new OgcCoverageImportTarget { LayerId = 5 }
            },
            DryRun = false,
            ApplyMode = true
        };

        var first = await service.ImportAsync(request);
        var second = await service.ImportAsync(request);

        first.Manifest.Should().BeEquivalentTo(second.Manifest);
        first.Records.Select(static r => r.SourceCoverageId)
            .Should().BeEquivalentTo(second.Records.Select(static r => r.SourceCoverageId));
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task ImportAsync_ApplyMode_MissingLayerIdProducesManualReviewWithoutCallingSource()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => throw new InvalidOperationException("Source must not be called when layerId is missing."));
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = ServiceUrl,
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff")),
            DryRun = false,
            ApplyMode = true
        };

        var result = await service.ImportAsync(request);

        var record = result.Records.Should().ContainSingle().Subject;
        record.Action.Should().Be("skipped");
        record.Warnings.Should().ContainSingle()
            .Which.Should().Contain("no target layer was supplied");
        result.Manifest.ManualReviewItems.Should().ContainSingle()
            .Which.Code.Should().Be("OGC_COVERAGE_TARGET_LAYER_MISSING");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ImportAsync_ApplyMode_HttpFailurePropagatesAsFailedRecord()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = ServiceUrl,
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff")),
            Targets = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal)
            {
                ["dem-tiff"] = new OgcCoverageImportTarget { LayerId = 3 }
            },
            DryRun = false,
            ApplyMode = true
        };

        var result = await service.ImportAsync(request);

        var record = result.Records.Should().ContainSingle().Subject;
        record.Action.Should().Be("failed");
        record.ErrorMessage.Should().Be("Failed to download or import coverage.");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ImportAsync_UnsupportedScientificFormat_ClassifiedAsManualReview()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => throw new InvalidOperationException("Unsupported coverages must not be downloaded."));
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = ServiceUrl,
            Inventory = BuildInventory(BuildResource(
                "dem-netcdf",
                OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported,
                "incompatible")),
            DryRun = true
        };

        var result = await service.ImportAsync(request);

        var record = result.Records.Should().ContainSingle().Subject;
        record.Action.Should().Be("manual-review");
        record.OutputFormat.Should().Be("NetCDF");
        record.Classification.Should().Be("unsupported");
        result.Manifest.UnsupportedItems.Should().ContainSingle()
            .Which.Code.Should().Be(OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported);
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ImportAsync_PopulatesStyleDiagnosticsFromInventoryStyleIds()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => throw new InvalidOperationException("Dry-run import must not call the source service."));
        var service = CreateService(handler, rasterImportMock.Object);

        var resource = BuildGeoTiffResource("ndvi-monthly") with
        {
            StyleIds = ["ndvi-continuous-ramp", "esri:rendering:stretchedRaster"]
        };
        var request = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = ServiceUrl,
            Inventory = BuildInventory(resource),
            DryRun = true
        };

        var result = await service.ImportAsync(request);

        result.StyleDiagnostics.Should().HaveCountGreaterThan(0);
        result.StyleDiagnostics.Should().Contain(d => d.Kind == "colorMap" && d.Classification == "manual-review");
        result.StyleDiagnostics.Should().Contain(d => d.VendorName == "Esri");
    }

    [Fact]
    public async Task ImportAsync_RejectsPlainHttpUrl_UnlessAllowUnsafeIsSet()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = "http://example.com/wcs",
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff")),
            DryRun = true
        };

        var act = async () => await service.ImportAsync(request);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static OgcCoverageImportService CreateService(
        HttpMessageHandler handler,
        IRasterImportService rasterImportService)
    {
        var httpClient = new HttpClient(handler);
        return new OgcCoverageImportService(
            httpClient,
            rasterImportService,
            NullLogger<OgcCoverageImportService>.Instance);
    }

    private static MigrationSourceInventoryArtifact BuildInventory(params MigrationInventoryResource[] resources)
        => new()
        {
            SourceKind = "ogc-wcs",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Example WCS",
                BaseUrl = ServiceUrl,
                ServiceType = "WCS"
            },
            AuthPosture = new MigrationInventoryAuthPosture { Mode = "anonymous" },
            ScanCompleteness = new MigrationInventoryCompleteness { Status = "complete" },
            Summary = new MigrationInventorySummary
            {
                ContainerCount = 1,
                ResourceCount = resources.Length
            },
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "OGC coverage migration scan completed."
            },
            Resources = resources
        };

    private static MigrationInventoryResource BuildGeoTiffResource(string name)
        => BuildResource(name, OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported, "compatible");

    private static MigrationInventoryResource BuildCogResource(string name)
        => BuildResource(name, OgcCoverageMigrationCompatibilityCodes.CogSupported, "compatible");

    private static MigrationInventoryResource BuildResource(string name, string compatibilityCode, string level)
        => new()
        {
            Id = $"coverage:{name}",
            ContainerId = "service:wcs",
            Kind = "coverage",
            Name = name,
            Title = name,
            SpatialReferences = [
                new MigrationSpatialReferenceInfo
                {
                    Role = "native",
                    Srid = 4326,
                    SourceValue = "EPSG:4326"
                }
            ],
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = level,
                Code = compatibilityCode,
                Reason = $"OGC coverage migration compatibility: {compatibilityCode}."
            }
        };

    /// <summary>
    /// Creates a minimal GeoTIFF response payload. Includes the classic GeoTIFF
    /// little-endian header so anything sniffing the file can identify it.
    /// </summary>
    private static HttpResponseMessage CreateGeoTiffResponse()
    {
        // II*\0 = little-endian TIFF magic. Padding bytes simulate a small coverage body.
        var bytes = new byte[]
        {
            0x49, 0x49, 0x2A, 0x00,
            0x08, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0xDE, 0xAD, 0xBE, 0xEF
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;

        public CountingHandler(Func<HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        public int RequestCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return Task.FromResult(_factory());
        }
    }
}
