// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Disposition tests for the SLD conversion path inside <see cref="GeoServerImportService"/>.
/// Covers the regression where <see cref="UnsupportedResourceBehavior.Skip"/> on a converter
/// error incremented <c>SkippedCount</c> but still imported the same style downstream
/// (issue #375 review finding).
/// </summary>
public sealed class GeoServerImportServiceSldDispositionTests
{
    [Fact]
    public void TryConvertSldStyle_ConverterErrorWithSkip_SignalsSkipAndDoesNotDoubleCount()
    {
        var converter = new StubSldConverter(
            warnings: Array.Empty<string>(),
            errors: new[] { "no convertible symbolizers" });
        var service = CreateService(converter);

        var style = new GeoServerStyleInfo
        {
            Name = "broken-style",
            Format = "sld",
            SldContent = "<StyledLayerDescriptor xmlns=\"http://www.opengis.net/sld\"/>"
        };
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            TargetHonuaUrl = "https://example.com/honua",
            ImportOptions = new GeoServerImportOptions
            {
                UnsupportedStyleBehavior = UnsupportedResourceBehavior.Skip
            }
        };
        var result = new GeoServerImportService.ImportStepResult();

        var warnings = service.TryConvertSldStyle(style, request, result, out var converterAvailable, out var shouldSkip);

        converterAvailable.Should().BeTrue();
        shouldSkip.Should().BeTrue("converter errors with Skip behavior must abort the per-style import");
        result.SkippedCount.Should().Be(1);
        result.SuccessCount.Should().Be(0);
        result.ImportedResources.Should().BeEmpty();
        warnings.Should().NotContain(w => w.Contains("could not be converted"),
            "Skip behavior should not also append a LogWarning-style message");
    }

    [Fact]
    public void TryConvertSldStyle_ConverterErrorWithLogWarning_DoesNotSignalSkip()
    {
        var converter = new StubSldConverter(
            warnings: Array.Empty<string>(),
            errors: new[] { "no convertible symbolizers" });
        var service = CreateService(converter);

        var style = new GeoServerStyleInfo
        {
            Name = "lossy-style",
            Format = "sld",
            SldContent = "<StyledLayerDescriptor xmlns=\"http://www.opengis.net/sld\"/>"
        };
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            TargetHonuaUrl = "https://example.com/honua",
            ImportOptions = new GeoServerImportOptions
            {
                UnsupportedStyleBehavior = UnsupportedResourceBehavior.LogWarning
            }
        };
        var result = new GeoServerImportService.ImportStepResult();

        var warnings = service.TryConvertSldStyle(style, request, result, out var converterAvailable, out var shouldSkip);

        converterAvailable.Should().BeTrue();
        shouldSkip.Should().BeFalse("LogWarning behavior must let the caller continue with the import");
        result.SkippedCount.Should().Be(0);
        warnings.Should().Contain(w => w.Contains("could not be converted"));
    }

    [Fact]
    public void TryConvertSldStyle_ConversionSucceedsWithWarnings_PropagatesWithoutSkipping()
    {
        var converter = new StubSldConverter(
            warnings: new[] { "[VendorOption] ignored x-foo" },
            errors: Array.Empty<string>(),
            mapLibreLayersJson: "[{\"id\":\"r0-0\",\"type\":\"fill\"}]");
        var service = CreateService(converter);

        var style = new GeoServerStyleInfo
        {
            Name = "ok-style",
            Format = "sld",
            SldContent = "<StyledLayerDescriptor xmlns=\"http://www.opengis.net/sld\"/>"
        };
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            TargetHonuaUrl = "https://example.com/honua",
            ImportOptions = new GeoServerImportOptions
            {
                UnsupportedStyleBehavior = UnsupportedResourceBehavior.Skip
            }
        };
        var result = new GeoServerImportService.ImportStepResult();

        var warnings = service.TryConvertSldStyle(style, request, result, out var converterAvailable, out var shouldSkip);

        converterAvailable.Should().BeTrue();
        shouldSkip.Should().BeFalse();
        result.SkippedCount.Should().Be(0);
        warnings.Should().ContainSingle()
            .Which.Should().Contain("VendorOption");
    }

    [Fact]
    public void TryConvertSldStyle_NoConverterRegistered_ReportsConverterUnavailable()
    {
        var service = CreateService(sldConverter: null);

        var style = new GeoServerStyleInfo
        {
            Name = "no-converter",
            Format = "sld",
            SldContent = "<StyledLayerDescriptor xmlns=\"http://www.opengis.net/sld\"/>"
        };
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            TargetHonuaUrl = "https://example.com/honua"
        };
        var result = new GeoServerImportService.ImportStepResult();

        var warnings = service.TryConvertSldStyle(style, request, result, out var converterAvailable, out var shouldSkip);

        converterAvailable.Should().BeFalse();
        shouldSkip.Should().BeFalse();
        warnings.Should().BeEmpty();
        result.SkippedCount.Should().Be(0);
    }

    private static GeoServerImportService CreateService(ISldStyleConverter? sldConverter)
    {
        using var httpClient = new HttpClient(new NoopHandler());
        var restClient = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);

        return new GeoServerImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            NullLogger<GeoServerImportService>.Instance,
            sldConverter);
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class StubSldConverter : ISldStyleConverter
    {
        private readonly IReadOnlyList<string> _warnings;
        private readonly IReadOnlyList<string> _errors;
        private readonly string? _mapLibreLayersJson;

        public StubSldConverter(
            IReadOnlyList<string> warnings,
            IReadOnlyList<string> errors,
            string? mapLibreLayersJson = null)
        {
            _warnings = warnings;
            _errors = errors;
            _mapLibreLayersJson = mapLibreLayersJson;
        }

        public SldStyleConversionResult Convert(string sldXml)
            => new(
                MapLibreLayersJson: _mapLibreLayersJson,
                DetectedSldVersion: "Sld10",
                Warnings: _warnings,
                Errors: _errors);
    }
}
