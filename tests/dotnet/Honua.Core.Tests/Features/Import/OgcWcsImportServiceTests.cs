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
/// Service-level tests for <see cref="OgcWcsImportService"/> (issue #1030 slice 3).
/// Exercises the WCS happy-path against captured GetCoverage responses, the
/// non-GeoTIFF format downgrade, and error propagation from the underlying
/// coverage import pipeline.
/// </summary>
public sealed class OgcWcsImportServiceTests
{
    private const string ServiceUrl = "https://example.com/geoserver/wcs";

    [Fact]
    public async Task ImportAsync_DryRun_BuildsPlannedManifestWithoutDownloadingCoverage()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => throw new InvalidOperationException("Dry-run import must not call the WCS source."));
        var service = CreateService(handler, rasterImportMock.Object);

        var inventory = BuildInventory(BuildGeoTiffResource("dem-tiff"));
        var request = new OgcWcsImportRequest
        {
            ServiceUrl = ServiceUrl,
            Inventory = inventory,
            DryRun = true
        };

        var result = await service.ImportAsync(request);

        result.DryRun.Should().BeTrue();
        result.ApplyMode.Should().BeFalse();
        result.RequestedOutputFormat.Should().Be("image/tiff");
        result.ResolvedVersion.Should().Be("2.0.1");
        result.Records.Should().ContainSingle().Which.Action.Should().Be("planned");
        result.Manifest.TargetResources.Should().OnlyContain(target => target.Action == "planned");
        handler.RequestCount.Should().Be(0);
        rasterImportMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImportAsync_ApplyMode_DownloadsGeoTiffAndIssuesWcsGetCoverageRequest()
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
                    RasterId = 7777,
                    LayerId = req.LayerId,
                    Name = req.Name,
                    Format = req.Format
                });

        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcWcsImportRequest
        {
            ServiceUrl = ServiceUrl,
            Version = "2.0.1",
            OutputFormat = "image/tiff",
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff")),
            Targets = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal)
            {
                ["dem-tiff"] = new OgcCoverageImportTarget { LayerId = 9, Name = "Elevation" }
            },
            DryRun = false,
            ApplyMode = true
        };

        var result = await service.ImportAsync(request);

        result.ApplyMode.Should().BeTrue();
        result.DryRun.Should().BeFalse();
        result.ResolvedVersion.Should().Be("2.0.1");
        var record = result.Records.Should().ContainSingle().Subject;
        record.Action.Should().Be("imported");
        record.RasterId.Should().Be(7777);
        record.TargetLayerId.Should().Be(9);
        record.OutputFormat.Should().Be("GeoTIFF");
        handler.RequestCount.Should().Be(1);
        var query = handler.LastRequest!.RequestUri!.Query;
        query.Should().Contain("service=WCS");
        query.Should().Contain("request=GetCoverage");
        query.Should().Contain("coverageId=dem-tiff");
        query.Should().Contain("version=2.0.1");
        capturedRequests.Should().ContainSingle();
        capturedRequests[0].Format.Should().Be(SupportedRasterFormat.GeoTiff);
    }

    [Fact]
    public async Task ImportAsync_NonGeoTiffFormat_DowngradesEveryCoverageToManualReview()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => throw new InvalidOperationException("Non-GeoTIFF formats must not call the WCS source."));
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcWcsImportRequest
        {
            ServiceUrl = ServiceUrl,
            OutputFormat = "application/x-netcdf",
            Inventory = BuildInventory(
                BuildGeoTiffResource("dem-tiff"),
                BuildGeoTiffResource("ortho-tiff")),
            Targets = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal)
            {
                ["dem-tiff"] = new OgcCoverageImportTarget { LayerId = 1 },
                ["ortho-tiff"] = new OgcCoverageImportTarget { LayerId = 2 }
            },
            DryRun = false,
            ApplyMode = true
        };

        var result = await service.ImportAsync(request);

        result.RequestedOutputFormat.Should().Be("application/x-netcdf");
        result.Records.Should().HaveCount(2);
        result.Records.Should().OnlyContain(record => record.Action == "manual-review");
        result.Manifest.UnsupportedItems.Should().HaveCount(2);
        result.Manifest.UnsupportedItems.Should().OnlyContain(item =>
            item.Code == OgcCoverageMigrationCompatibilityCodes.ScientificFormatUnsupported);
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ImportAsync_GeoTiffMimeVariant_IsTreatedAsAutomatedFormat()
    {
        var rasterImportMock = new Mock<IRasterImportService>();
        rasterImportMock
            .Setup(static s => s.ImportAsync(It.IsAny<RasterImportRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RasterImportRequest req, IProgress<RasterImportProgress>? _, CancellationToken _) =>
                new RasterImportResult
                {
                    Success = true,
                    RasterId = 333,
                    LayerId = req.LayerId,
                    Name = req.Name,
                    Format = req.Format
                });

        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcWcsImportRequest
        {
            ServiceUrl = ServiceUrl,
            OutputFormat = "image/tiff;application=geotiff",
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff")),
            Targets = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal)
            {
                ["dem-tiff"] = new OgcCoverageImportTarget { LayerId = 4 }
            },
            DryRun = false,
            ApplyMode = true
        };

        var result = await service.ImportAsync(request);

        result.RequestedOutputFormat.Should().Be("image/tiff;application=geotiff");
        result.Records.Should().ContainSingle().Which.Action.Should().Be("imported");
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ImportAsync_Wcs1xVersion_PropagatesLegacyCoverageParameter()
    {
        var rasterImportMock = new Mock<IRasterImportService>();
        rasterImportMock
            .Setup(static s => s.ImportAsync(It.IsAny<RasterImportRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RasterImportRequest req, IProgress<RasterImportProgress>? _, CancellationToken _) =>
                new RasterImportResult
                {
                    Success = true,
                    RasterId = 1,
                    LayerId = req.LayerId,
                    Name = req.Name,
                    Format = req.Format
                });

        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcWcsImportRequest
        {
            ServiceUrl = ServiceUrl,
            Version = "1.1.1",
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff")),
            Targets = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal)
            {
                ["dem-tiff"] = new OgcCoverageImportTarget { LayerId = 6 }
            },
            DryRun = false,
            ApplyMode = true
        };

        var result = await service.ImportAsync(request);

        result.ResolvedVersion.Should().Be("1.1.1");
        var query = handler.LastRequest!.RequestUri!.Query;
        query.Should().Contain("version=1.1.1");
        // WCS 1.x uses "coverage" rather than "coverageId" for the layer identifier.
        query.Should().Contain("coverage=dem-tiff");
        query.Should().NotContain("coverageId=");
    }

    [Fact]
    public async Task ImportAsync_HttpFailurePropagatesAsFailedRecord()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcWcsImportRequest
        {
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
    public async Task ImportAsync_RejectsUnknownVersion()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcWcsImportRequest
        {
            ServiceUrl = ServiceUrl,
            Version = "9.9.9",
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff")),
            DryRun = true
        };

        var act = async () => await service.ImportAsync(request);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(static ex => ex.Message.Contains("Unsupported WCS version"));
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ImportAsync_PropagatesStyleDiagnosticsFromUnderlyingCoverageService()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => throw new InvalidOperationException("Dry-run must not call the WCS source."));
        var service = CreateService(handler, rasterImportMock.Object);

        var resource = BuildGeoTiffResource("ndvi") with
        {
            StyleIds = ["esri:colorizer"]
        };
        var request = new OgcWcsImportRequest
        {
            ServiceUrl = ServiceUrl,
            Inventory = BuildInventory(resource),
            DryRun = true
        };

        var result = await service.ImportAsync(request);

        result.StyleDiagnostics.Should().ContainSingle();
        result.StyleDiagnostics[0].VendorName.Should().Be("Esri");
        result.StyleDiagnostics[0].Classification.Should().Be("manual-review");
    }

    [Fact]
    public async Task ImportAsync_MissingServiceUrl_ThrowsArgumentException()
    {
        var rasterImportMock = new Mock<IRasterImportService>(MockBehavior.Strict);
        var handler = new CountingHandler(() => CreateGeoTiffResponse());
        var service = CreateService(handler, rasterImportMock.Object);

        var request = new OgcWcsImportRequest
        {
            ServiceUrl = "   ",
            Inventory = BuildInventory(BuildGeoTiffResource("dem-tiff"))
        };

        var act = async () => await service.ImportAsync(request);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(static ex => ex.Message.Contains("ServiceUrl is required"));
    }

    private static OgcWcsImportService CreateService(
        HttpMessageHandler handler,
        IRasterImportService rasterImportService)
    {
        var httpClient = new HttpClient(handler);
        var coverageService = new OgcCoverageImportService(
            httpClient,
            rasterImportService,
            NullLogger<OgcCoverageImportService>.Instance);
        return new OgcWcsImportService(
            coverageService,
            NullLogger<OgcWcsImportService>.Instance);
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
                Reason = "WCS coverage migration scan completed."
            },
            Resources = resources
        };

    private static MigrationInventoryResource BuildGeoTiffResource(string name)
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
                Level = "compatible",
                Code = OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported,
                Reason = "WCS GeoTIFF coverage."
            }
        };

    /// <summary>
    /// Builds a minimal GeoTIFF response body. WCS 2.x servers return image/tiff
    /// directly from GetCoverage; we mimic that shape with the canonical TIFF magic.
    /// </summary>
    private static HttpResponseMessage CreateGeoTiffResponse()
    {
        // II*\0 = little-endian TIFF magic. Padding bytes simulate the coverage body.
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
