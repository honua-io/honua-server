// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Postgres.Features.Import;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Tests for RefactoredStreamingFileImportService - critical file import business logic
/// </summary>
public sealed class RefactoredStreamingFileImportServiceTests
{
    private readonly IFileFormatDetectionService _mockFormatDetectionService;
    private readonly IFilePreviewService _mockPreviewService;
    private readonly IStreamingImportProcessor _mockImportProcessor;
    private readonly ILogger<RefactoredStreamingFileImportService> _mockLogger;
    private readonly RefactoredStreamingFileImportService _importService;

    public RefactoredStreamingFileImportServiceTests()
    {
        _mockFormatDetectionService = Substitute.For<IFileFormatDetectionService>();
        _mockPreviewService = Substitute.For<IFilePreviewService>();
        _mockImportProcessor = Substitute.For<IStreamingImportProcessor>();
        _mockLogger = Substitute.For<ILogger<RefactoredStreamingFileImportService>>();

        _importService = new RefactoredStreamingFileImportService(
            _mockFormatDetectionService,
            _mockPreviewService,
            _mockImportProcessor,
            _mockLogger);
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullFormatDetectionService_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new RefactoredStreamingFileImportService(
            null!,
            _mockPreviewService,
            _mockImportProcessor,
            _mockLogger);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("formatDetectionService");
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullPreviewService_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new RefactoredStreamingFileImportService(
            _mockFormatDetectionService,
            null!,
            _mockImportProcessor,
            _mockLogger);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("previewService");
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullImportProcessor_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new RefactoredStreamingFileImportService(
            _mockFormatDetectionService,
            _mockPreviewService,
            null!,
            _mockLogger);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("importProcessor");
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new RefactoredStreamingFileImportService(
            _mockFormatDetectionService,
            _mockPreviewService,
            _mockImportProcessor,
            null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    [UnitTest]
    public async Task DetectFormatAsync_ValidStream_CallsFormatDetectionService()
    {
        // Arrange
        using var stream = new MemoryStream([1, 2, 3, 4]);
        const string fileName = "test.shp";
        var expectedFormat = new FileFormat { Name = "Shapefile", Extension = ".shp" };

        _mockFormatDetectionService
            .DetectFormatAsync(stream, fileName, Arg.Any<CancellationToken>())
            .Returns(expectedFormat);

        // Act
        var result = await _importService.DetectFormatAsync(stream, fileName);

        // Assert
        result.Should().Be(expectedFormat);
        await _mockFormatDetectionService.Received(1)
            .DetectFormatAsync(stream, fileName, Arg.Any<CancellationToken>());
    }

    [Fact]
    [UnitTest]
    public async Task DetectFormatAsync_NullStream_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = async () => await _importService.DetectFormatAsync(null!, "test.shp");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    [UnitTest]
    public async Task DetectFormatAsync_NullFileName_ThrowsArgumentNullException()
    {
        // Arrange
        using var stream = new MemoryStream([1, 2, 3]);

        // Act & Assert
        var act = async () => await _importService.DetectFormatAsync(stream, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    [UnitTest]
    public async Task PreviewAsync_ValidImportRequest_CallsPreviewService()
    {
        // Arrange
        var importRequest = new ImportRequest
        {
            LayerName = "TestLayer",
            SourceStream = new MemoryStream([1, 2, 3]),
            Format = new FileFormat { Name = "GeoJSON", Extension = ".geojson" }
        };

        var expectedPreview = new ImportPreview
        {
            FeatureCount = 10,
            SampleFeatures = new List<Feature>(),
            FieldSchema = new List<FieldDefinition>()
        };

        _mockPreviewService
            .GeneratePreviewAsync(importRequest, Arg.Any<CancellationToken>())
            .Returns(expectedPreview);

        // Act
        var result = await _importService.PreviewAsync(importRequest);

        // Assert
        result.Should().Be(expectedPreview);
        await _mockPreviewService.Received(1)
            .GeneratePreviewAsync(importRequest, Arg.Any<CancellationToken>());
    }

    [Fact]
    [UnitTest]
    public async Task PreviewAsync_NullImportRequest_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = async () => await _importService.PreviewAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    [UnitTest]
    public async Task ImportAsync_ValidImportRequest_CallsImportProcessor()
    {
        // Arrange
        var importRequest = new ImportRequest
        {
            LayerName = "TestLayer",
            SourceStream = new MemoryStream([1, 2, 3]),
            Format = new FileFormat { Name = "Shapefile", Extension = ".shp" }
        };

        var expectedResult = new ImportResult
        {
            Success = true,
            FeaturesImported = 100,
            LayerId = Guid.NewGuid()
        };

        _mockImportProcessor
            .ProcessImportAsync(importRequest, Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        // Act
        var result = await _importService.ImportAsync(importRequest);

        // Assert
        result.Should().Be(expectedResult);
        await _mockImportProcessor.Received(1)
            .ProcessImportAsync(importRequest, Arg.Any<CancellationToken>());
    }

    [Fact]
    [UnitTest]
    public async Task ImportAsync_NullImportRequest_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = async () => await _importService.ImportAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    [UnitTest]
    public async Task ImportAsync_ProcessorThrowsException_LogsErrorAndRethrows()
    {
        // Arrange
        var importRequest = new ImportRequest
        {
            LayerName = "TestLayer",
            SourceStream = new MemoryStream([1, 2, 3]),
            Format = new FileFormat { Name = "CSV", Extension = ".csv" }
        };

        var expectedException = new InvalidOperationException("Import processing failed");

        _mockImportProcessor
            .ProcessImportAsync(importRequest, Arg.Any<CancellationToken>())
            .ThrowsAsync(expectedException);

        // Act & Assert
        var act = async () => await _importService.ImportAsync(importRequest);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Import processing failed");

        // Verify error was logged
        _mockLogger.Received().LogError(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());
    }

    [Fact]
    [UnitTest]
    public async Task ValidateAsync_ValidImportRequest_CallsAllValidationServices()
    {
        // Arrange
        using var stream = new MemoryStream([1, 2, 3]);
        const string fileName = "test.geojson";
        var format = new FileFormat { Name = "GeoJSON", Extension = ".geojson" };

        var importRequest = new ImportRequest
        {
            LayerName = "TestLayer",
            SourceStream = stream,
            Format = format
        };

        _mockFormatDetectionService
            .DetectFormatAsync(stream, fileName, Arg.Any<CancellationToken>())
            .Returns(format);

        _mockPreviewService
            .ValidateAsync(importRequest, Arg.Any<CancellationToken>())
            .Returns(new ValidationResult { IsValid = true });

        // Act
        var result = await _importService.ValidateAsync(importRequest);

        // Assert
        result.IsValid.Should().BeTrue();
        await _mockPreviewService.Received(1)
            .ValidateAsync(importRequest, Arg.Any<CancellationToken>());
    }

    [Fact]
    [UnitTest]
    public async Task ValidateAsync_InvalidFormat_ReturnsValidationFailure()
    {
        // Arrange
        var importRequest = new ImportRequest
        {
            LayerName = "TestLayer",
            SourceStream = new MemoryStream([1, 2, 3]),
            Format = new FileFormat { Name = "Unknown", Extension = ".xyz" }
        };

        var validationResult = new ValidationResult
        {
            IsValid = false,
            Errors = new[] { "Unsupported file format" }
        };

        _mockPreviewService
            .ValidateAsync(importRequest, Arg.Any<CancellationToken>())
            .Returns(validationResult);

        // Act
        var result = await _importService.ValidateAsync(importRequest);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("Unsupported file format");
    }

    [Fact]
    [UnitTest]
    public async Task GetSupportedFormatsAsync_CallsFormatDetectionService()
    {
        // Arrange
        var supportedFormats = new[]
        {
            new FileFormat { Name = "Shapefile", Extension = ".shp" },
            new FileFormat { Name = "GeoJSON", Extension = ".geojson" },
            new FileFormat { Name = "KML", Extension = ".kml" }
        };

        _mockFormatDetectionService
            .GetSupportedFormatsAsync(Arg.Any<CancellationToken>())
            .Returns(supportedFormats);

        // Act
        var result = await _importService.GetSupportedFormatsAsync();

        // Assert
        result.Should().BeEquivalentTo(supportedFormats);
        await _mockFormatDetectionService.Received(1)
            .GetSupportedFormatsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [UnitTest]
    public async Task ProcessStreamAsync_ValidParameters_ChainsAllServices()
    {
        // Arrange
        using var stream = new MemoryStream([1, 2, 3]);
        const string fileName = "integration_test.shp";
        var format = new FileFormat { Name = "Shapefile", Extension = ".shp" };

        var preview = new ImportPreview
        {
            FeatureCount = 5,
            SampleFeatures = new List<Feature>(),
            FieldSchema = new List<FieldDefinition>()
        };

        var importResult = new ImportResult
        {
            Success = true,
            FeaturesImported = 5,
            LayerId = Guid.NewGuid()
        };

        _mockFormatDetectionService
            .DetectFormatAsync(stream, fileName, Arg.Any<CancellationToken>())
            .Returns(format);

        _mockPreviewService
            .GeneratePreviewAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(preview);

        _mockPreviewService
            .ValidateAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult { IsValid = true });

        _mockImportProcessor
            .ProcessImportAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(importResult);

        // Act - Simulating full workflow
        var detectedFormat = await _importService.DetectFormatAsync(stream, fileName);
        stream.Position = 0; // Reset stream position

        var importRequest = new ImportRequest
        {
            LayerName = "TestLayer",
            SourceStream = stream,
            Format = detectedFormat
        };

        var validation = await _importService.ValidateAsync(importRequest);
        validation.IsValid.Should().BeTrue();

        var previewResult = await _importService.PreviewAsync(importRequest);
        var finalResult = await _importService.ImportAsync(importRequest);

        // Assert
        detectedFormat.Should().Be(format);
        previewResult.Should().Be(preview);
        finalResult.Should().Be(importResult);
        finalResult.Success.Should().BeTrue();
        finalResult.FeaturesImported.Should().Be(5);
    }
}