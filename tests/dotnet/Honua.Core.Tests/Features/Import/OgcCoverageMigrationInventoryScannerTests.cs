// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Moq;

namespace Honua.Core.Tests.Features.Import;

public sealed class OgcCoverageMigrationInventoryScannerTests
{
    [Fact]
    public async Task ScanAsync_WcsCoverageWithCogAndTemporalMetadata_ProducesCoverageInventory()
    {
        var scanner = CreateScanner();

        var artifact = await scanner.ScanAsync(new OgcCoverageServiceScanRequest
        {
            ServiceType = "WCS",
            ServiceUrl = "https://example.com/geoserver/wcs",
            Version = "2.0.1",
            ServiceMetadata = new OgcCoverageServiceMetadata
            {
                Title = "Reference WCS",
                Product = "GeoServer",
                ProviderName = "Example GIS",
                Fees = ["none"],
                AccessConstraints = ["none"]
            },
            Coverages =
            [
                new OgcCoverageDescription
                {
                    CoverageId = "nurc:temperature",
                    Title = "Temperature",
                    CoverageType = "RectifiedGridCoverage",
                    NativeFormat = "image/tiff",
                    Crs =
                    [
                        new OgcCoverageCrsMetadata
                        {
                            Role = "native",
                            Crs = "http://www.opengis.net/def/crs/EPSG/0/4326",
                            AxisOrder = "lat,lon",
                            AxisLabels = ["Lat", "Long"]
                        }
                    ],
                    Axes =
                    [
                        new OgcCoverageAxisMetadata
                        {
                            Name = "Long",
                            AxisType = "x",
                            Unit = "deg",
                            LowerBound = "-180",
                            UpperBound = "180",
                            Resolution = "0.1",
                            Subsettable = true
                        },
                        new OgcCoverageAxisMetadata
                        {
                            Name = "Lat",
                            AxisType = "y",
                            Unit = "deg",
                            LowerBound = "-90",
                            UpperBound = "90",
                            Resolution = "0.1",
                            Subsettable = true
                        }
                    ],
                    Ranges =
                    [
                        new OgcCoverageRangeMetadata
                        {
                            Name = "temperature",
                            Label = "Air temperature",
                            DataType = "Float32",
                            Unit = "K",
                            NoDataValue = "-9999",
                            Interpretation = "temperature",
                            MinimumValue = "200",
                            MaximumValue = "330"
                        }
                    ],
                    OutputFormats =
                    [
                        new OgcCoverageFormatMetadata
                        {
                            Format = "image/tiff",
                            MediaType = "image/tiff; application=geotiff",
                            Profile = "cloud-optimized-geotiff",
                            IsNative = true
                        }
                    ],
                    TemporalDimensions =
                    [
                        new OgcCoverageTemporalDimensionMetadata
                        {
                            Name = "time",
                            Start = "2020-01-01T00:00:00Z",
                            End = "2020-01-03T00:00:00Z",
                            DefaultValue = "2020-01-01T00:00:00Z",
                            Interval = "P1D",
                            Subsettable = true
                        }
                    ]
                }
            ]
        });

        artifact.SourceKind.Should().Be("ogc-wcs");
        artifact.Source.ServiceType.Should().Be("WCS");
        artifact.Source.Product.Should().Be("GeoServer");
        artifact.Source.Version.Should().Be("2.0.1");
        artifact.AuthPosture.Mode.Should().Be("anonymous");
        artifact.ScanCompleteness.Status.Should().Be("complete");
        artifact.Resources.Should().ContainSingle();
        var resource = artifact.Resources.Single();
        resource.Kind.Should().Be("coverage");
        resource.Capabilities.Should().Contain(["wcs:DescribeCoverage", "wcs:GetCoverage"]);
        resource.SpatialReferences.Should().ContainSingle().Which.Should().Match<MigrationSpatialReferenceInfo>(reference =>
            reference.Role == "native" &&
            reference.Srid == 4326 &&
            reference.AxisOrder == "lat,lon");
        resource.Fields.Should().ContainSingle(field =>
            field.Name == "temperature" &&
            field.FieldType == "Float32" &&
            field.DomainName == "K");
        resource.Compatibility.Level.Should().Be("partial");
        resource.Compatibility.Code.Should().Be(OgcCoverageMigrationCompatibilityCodes.CogSupported);
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Kind == "coverage-output-format" &&
            dependency.DependencyType == "cog" &&
            dependency.Compatibility.Code == OgcCoverageMigrationCompatibilityCodes.CogSupported);
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Kind == "coverage-axis" &&
            dependency.Name == "Lat" &&
            dependency.Metadata["subsettable"] == "true");
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Kind == "coverage-range" &&
            dependency.Name == "temperature" &&
            dependency.Metadata["noDataValue"] == "-9999");
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Kind == "coverage-temporal-dimension" &&
            dependency.Name == "time" &&
            dependency.Metadata["interval"] == "P1D");
    }

    [Fact]
    public async Task ScanAsync_OgcApiCoveragesScientificFormats_ClassifiesUnsupportedFormatsSeparately()
    {
        var scanner = CreateScanner();

        var artifact = await scanner.ScanAsync(new OgcCoverageServiceScanRequest
        {
            ServiceType = "OGC API Coverages",
            ServiceUrl = "https://coverages.example.com/collections",
            ServiceMetadata = new OgcCoverageServiceMetadata
            {
                Title = "Scientific coverages",
                AccessConstraints = ["license review required"]
            },
            Coverages =
            [
                new OgcCoverageDescription
                {
                    CoverageId = "ocean-forecast",
                    CoverageType = "Coverage",
                    Crs =
                    [
                        new OgcCoverageCrsMetadata
                        {
                            Role = "native",
                            Crs = "EPSG:4326"
                        }
                    ],
                    Axes =
                    [
                        new OgcCoverageAxisMetadata
                        {
                            Name = "time",
                            AxisType = "time",
                            LowerBound = "2026-01-01T00:00:00Z",
                            UpperBound = "2026-01-02T00:00:00Z",
                            Subsettable = true
                        }
                    ],
                    Ranges =
                    [
                        new OgcCoverageRangeMetadata
                        {
                            Name = "salinity",
                            DataType = "Float32",
                            Unit = "PSU"
                        }
                    ],
                    OutputFormats =
                    [
                        new OgcCoverageFormatMetadata
                        {
                            Format = "application/x-netcdf"
                        },
                        new OgcCoverageFormatMetadata
                        {
                            Format = "application/x-hdf5"
                        },
                        new OgcCoverageFormatMetadata
                        {
                            Format = "application/vnd+zarr"
                        }
                    ]
                }
            ]
        });

        artifact.SourceKind.Should().Be("ogc-api-coverages");
        artifact.AuthPosture.Mode.Should().Be("access-constrained");
        artifact.AuthPosture.Notes.Should().ContainSingle().Which.Should().Be("license review required");
        var resource = artifact.Resources.Should().ContainSingle().Subject;
        resource.Compatibility.Level.Should().Be("incompatible");
        resource.Compatibility.Code.Should().Be(OgcCoverageMigrationCompatibilityCodes.ScientificFormatUnsupported);
        artifact.ExternalDependencies.Where(static dependency => dependency.Kind == "coverage-output-format")
            .Select(static dependency => dependency.Compatibility.Code)
            .Should()
            .BeEquivalentTo(
                [
                    OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported,
                    OgcCoverageMigrationCompatibilityCodes.HdfUnsupported,
                    OgcCoverageMigrationCompatibilityCodes.ZarrUnsupported
                ]);
    }

    [Fact]
    public async Task ScanAsync_CoverageLevelAccessConstraints_MarkServiceMetadataForManualReview()
    {
        var scanner = CreateScanner();

        var artifact = await scanner.ScanAsync(new OgcCoverageServiceScanRequest
        {
            ServiceType = "WCS",
            ServiceUrl = "https://example.com/wcs",
            ServiceMetadata = new OgcCoverageServiceMetadata
            {
                Title = "Reference WCS",
                AccessConstraints = ["none"]
            },
            Coverages =
            [
                BuildGeoTiffCoverage("restricted") with
                {
                    AccessConstraints = ["internal use only"]
                }
            ]
        });

        artifact.AuthPosture.Mode.Should().Be("access-constrained");
        artifact.AuthPosture.Notes.Should().ContainSingle().Which.Should().Be("internal use only");

        var serviceMetadata = artifact.ExternalDependencies.Should()
            .ContainSingle(dependency => dependency.Id == "metadata:coverage-service")
            .Subject;
        serviceMetadata.Metadata["accessConstraints"].Should().Be("internal use only");
        serviceMetadata.Compatibility.Should().Match<MigrationCompatibilityAssessment>(compatibility =>
            compatibility.Level == "partial" &&
            compatibility.Code == OgcCoverageMigrationCompatibilityCodes.ManualReview &&
            compatibility.Warnings.Contains("internal use only"));
    }

    [Fact]
    public async Task ScanAsync_MissingOutputFormats_EmitsManualReviewCompletenessSignal()
    {
        var scanner = CreateScanner();

        var artifact = await scanner.ScanAsync(new OgcCoverageServiceScanRequest
        {
            ServiceType = "WCS",
            ServiceUrl = "https://example.com/wcs",
            Coverages =
            [
                new OgcCoverageDescription
                {
                    CoverageId = "dem"
                }
            ]
        });

        artifact.ScanCompleteness.Status.Should().Be("partial");
        artifact.ScanCompleteness.MissingArtifacts.Should().ContainSingle().Which.Should().Be("output-format:dem");
        artifact.Resources.Should().ContainSingle().Which.Compatibility.Should().Match<MigrationCompatibilityAssessment>(compatibility =>
            compatibility.Level == "partial" &&
            compatibility.Code == OgcCoverageMigrationCompatibilityCodes.OutputFormatMissing);
    }

    [Fact]
    public async Task ScanAsync_ReordersCoverageInventoryDeterministically()
    {
        var scanner = CreateScanner();

        var artifact = await scanner.ScanAsync(new OgcCoverageServiceScanRequest
        {
            ServiceType = "WCS",
            ServiceUrl = "https://example.com/wcs",
            Coverages =
            [
                BuildGeoTiffCoverage("z-last"),
                BuildGeoTiffCoverage("a-first")
            ]
        });

        artifact.Resources.Select(static resource => resource.Name).Should().Equal("a-first", "z-last");
        artifact.ExternalDependencies.Select(static dependency => dependency.Id).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    private static OgcCoverageDescription BuildGeoTiffCoverage(string coverageId)
        => new()
        {
            CoverageId = coverageId,
            OutputFormats =
            [
                new OgcCoverageFormatMetadata
                {
                    Format = "GeoTIFF"
                }
            ]
        };

    private static OgcCoverageMigrationInventoryScanner CreateScanner()
    {
        var registry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        registry.Setup(item => item.ResolveBySridAsync(4326, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<CrsDefinition?>((CrsDefinition?)null));
        return new OgcCoverageMigrationInventoryScanner(registry.Object);
    }
}
