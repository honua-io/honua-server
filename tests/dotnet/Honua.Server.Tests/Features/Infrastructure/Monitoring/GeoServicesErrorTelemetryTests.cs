// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Verifies the keystone error-rate telemetry (#2243): every GeoServices error
/// envelope — including the LB-invisible HTTP-200 in-band <c>{error}</c> body and
/// sub-500 errors — must increment the OTel meter counters
/// (<c>honua_geoservices_error_total</c>, <c>honua_request_error_total</c>) and the
/// Core <c>honua_application_errors_total</c> counter.
/// </summary>
[Collection("Unit")]
public sealed class GeoServicesErrorTelemetryTests
{
    [Fact]
    public void FormatError_GeoServicesInBand200Envelope_IncrementsCountersWithInBandTrue()
    {
        // Arrange: GeoServices returns HTTP 200 with an {error} body for Esri compat.
        var context = CreateContext("/rest/services/0/FeatureServer/0/query");
        var errorResponse = new StandardErrorResponse(
            StatusCode: StatusCodes.Status200OK,
            Title: "Unable to complete operation.",
            Detail: "Invalid where clause.");

        using var collector = new ErrorMetricsCollector(
            "honua_geoservices_error_total",
            "honua_request_error_total",
            "honua_application_errors_total");

        // Act
        _ = StandardErrorResponseFormatter.FormatError(context, errorResponse);

        // Assert: filter on this test's distinctive tags so concurrent tests in
        // other collections that share these counters cannot perturb the result.
        var geo = collector.Match(
            "honua_geoservices_error_total",
            ("service_type", "FeatureServer"),
            ("operation", "query"),
            ("error_code", StatusCodes.Status200OK));
        geo.Should().ContainSingle("the in-band 200 {error} envelope must record a GeoServices error");
        geo[0].Value.Should().Be(1);

        collector.Match(
                "honua_request_error_total",
                ("service_type", "FeatureServer"),
                ("error_code", StatusCodes.Status200OK),
                ("in_band", true))
            .Should().ContainSingle("a 2xx error body is invisible to LB 5xx metrics (in_band=true)");

        collector.Match(
                "honua_application_errors_total",
                ("service_type", "FeatureServer"),
                ("operation", "query"))
            .Should().NotBeEmpty("the protocol error path must finally increment ApplicationErrors");
    }

    [Fact]
    public void FormatError_GeoServicesSub500Error_IncrementsCountersWithInBandFalse()
    {
        // Arrange: a sub-500 error returned with its real HTTP status (400).
        var context = CreateContext("/rest/services/0/FeatureServer/0/query");
        var errorResponse = StandardErrorResponse.BadRequest("Invalid where clause");

        using var collector = new ErrorMetricsCollector(
            "honua_geoservices_error_total",
            "honua_request_error_total");

        // Act
        _ = StandardErrorResponseFormatter.FormatError(context, errorResponse);

        // Assert
        collector.Match(
                "honua_geoservices_error_total",
                ("service_type", "FeatureServer"),
                ("error_code", StatusCodes.Status400BadRequest))
            .Should().ContainSingle();

        collector.Match(
                "honua_request_error_total",
                ("service_type", "FeatureServer"),
                ("error_code", StatusCodes.Status400BadRequest),
                ("in_band", false))
            .Should().ContainSingle("a 4xx status is reflected by the HTTP transport (in_band=false)");
    }

    [Fact]
    public void CreateGeoServicesValidationError_BypassBuilder_IncrementsCounters()
    {
        // Arrange / Act: this builder bypasses the central formatter entirely.
        using var collector = new ErrorMetricsCollector(
            "honua_geoservices_error_total",
            "honua_request_error_total");

        _ = ValidationErrorHelpers.CreateGeoServicesValidationError("Missing required parameter 'f'.");

        // Assert
        collector.Match(
                "honua_geoservices_error_total",
                ("service_type", "GeoServices"),
                ("operation", "validation"))
            .Should().ContainSingle("the bypassing GeoServices validation builder must still emit telemetry");
        collector.Match(
                "honua_request_error_total",
                ("service_type", "GeoServices"),
                ("operation", "validation"))
            .Should().ContainSingle();
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        return context;
    }

    private sealed record Sample(long Value, KeyValuePair<string, object?>[] Tags)
    {
        public bool HasTag(string name, object? expected)
        {
            foreach (var tag in Tags.Where(t => t.Key == name))
            {
                return Equals(tag.Value, expected);
            }

            return false;
        }
    }

    /// <summary>
    /// Test-only MeterListener capturing long measurements for a set of named
    /// instruments on the "Honua" meter.
    /// </summary>
    private sealed class ErrorMetricsCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly HashSet<string> _instrumentNames;
        private readonly List<(string Name, Sample Sample)> _samples = new();
        private readonly object _gate = new();

        public ErrorMetricsCollector(params string[] instrumentNames)
        {
            _instrumentNames = new HashSet<string>(instrumentNames, StringComparer.Ordinal);
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "Honua" && _instrumentNames.Contains(instrument.Name))
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                lock (_gate)
                {
                    _samples.Add((instrument.Name, new Sample(measurement, tags.ToArray())));
                }
            });
            _listener.Start();
        }

        public Sample[] Match(string instrumentName, params (string Key, object? Value)[] expectedTags)
        {
            lock (_gate)
            {
                return _samples
                    .Where(s => s.Name == instrumentName)
                    .Select(s => s.Sample)
                    .Where(sample => expectedTags.All(t => sample.HasTag(t.Key, t.Value)))
                    .ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
