// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Unit tests for <see cref="OgcProcessesConversionHelpers"/>.
/// </summary>
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesConversionHelpersTests
{
    private const string BaseUrl = "https://example.com";
    private const string ProcessId = "honua-geoprocessing";
    private const string ResultsRelation = "http://www.opengis.net/def/rel/ogc/1.0/results";
    private const string MonitorRelation = "monitor";

    [Theory]
    [InlineData(ArtifactKind.Scalar, "application/json", "object", null)]
    [InlineData(ArtifactKind.FeatureLayer, "application/geo+json", "object", null)]
    [InlineData(ArtifactKind.Table, "application/json", "object", null)]
    [InlineData(ArtifactKind.Raster, "image/tiff", "string", "binary")]
    [InlineData(ArtifactKind.File, "application/octet-stream", "string", "binary")]
    [InlineData(ArtifactKind.Report, "application/json", "object", null)]
    [InlineData(ArtifactKind.Map, "application/json", "object", null)]
    [InlineData(ArtifactKind.AppBundle, "application/octet-stream", "string", "binary")]
    [Operation(Operations.ProcessDiscovery)]
    public void GetDefaultOutputSchema_MapsEveryArtifactKind(
        ArtifactKind kind,
        string expectedMediaType,
        string expectedType,
        string? expectedFormat)
    {
        var schema = ProcessEndpoints.GetDefaultOutputSchema(kind);

        schema.ContentMediaType.Should().Be(expectedMediaType);
        schema.Type.Should().Be(expectedType);
        schema.Format.Should().Be(expectedFormat);
    }

    [Fact]
    [Operation(Operations.JobStatus)]
    public void ToOgcStatusInfo_SucceededJob_AdvertisesResultsLink()
    {
        var job = CreateJob(ExecutionJobStatus.Succeeded);

        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(job, ProcessId, BaseUrl);

        statusInfo.Links.Should().NotBeNull();
        statusInfo.Links!.Value.Should().ContainSingle(
            l => l.Rel == ResultsRelation && l.Href == $"{BaseUrl}/ogc/processes/jobs/{job.OperationId}/results",
            "succeeded jobs expose a results resource per OGC API Processes Part 1 §7.11.1");
    }

    [Theory]
    [InlineData(ExecutionJobStatus.Failed)]
    [InlineData(ExecutionJobStatus.Cancelled)]
    [Operation(Operations.JobStatus)]
    public void ToOgcStatusInfo_UnsuccessfulTerminalJob_DoesNotAdvertiseResultsLink(ExecutionJobStatus status)
    {
        var job = CreateJob(status);

        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(job, ProcessId, BaseUrl);

        statusInfo.Links.Should().NotBeNull();
        statusInfo.Links!.Value.Should().NotContain(l => l.Rel == ResultsRelation,
            "failed and cancelled jobs resolve to 500/410, so the results link would misdirect clients");
    }

    [Theory]
    [InlineData(ExecutionJobStatus.Queued)]
    [InlineData(ExecutionJobStatus.Running)]
    [InlineData(ExecutionJobStatus.Provisioning)]
    [Operation(Operations.JobStatus)]
    public void ToOgcStatusInfo_NonTerminalJob_DoesNotAdvertiseResultsLink(ExecutionJobStatus status)
    {
        var job = CreateJob(status);

        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(job, ProcessId, BaseUrl);

        statusInfo.Links.Should().NotBeNull();
        statusInfo.Links!.Value.Should().NotContain(l => l.Rel == ResultsRelation,
            "non-terminal jobs should never have a results link");
    }

    [Theory]
    [InlineData(ExecutionJobStatus.Queued)]
    [InlineData(ExecutionJobStatus.Running)]
    [InlineData(ExecutionJobStatus.Provisioning)]
    [Operation(Operations.JobStatus)]
    public void ToOgcStatusInfo_NonTerminalJob_AdvertisesMonitorLink(ExecutionJobStatus status)
    {
        var job = CreateJob(status);

        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(job, ProcessId, BaseUrl);

        statusInfo.Links.Should().NotBeNull();
        statusInfo.Links!.Value.Should().ContainSingle(
            l => l.Rel == MonitorRelation && l.Href == $"{BaseUrl}/ogc/processes/jobs/{job.OperationId}",
            "clients need an explicit monitor relation to discover asynchronous polling");
    }

    [Theory]
    [InlineData(ExecutionJobStatus.Succeeded)]
    [InlineData(ExecutionJobStatus.Failed)]
    [InlineData(ExecutionJobStatus.Cancelled)]
    [Operation(Operations.JobStatus)]
    public void ToOgcStatusInfo_TerminalJob_DoesNotAdvertiseMonitorLink(ExecutionJobStatus status)
    {
        var job = CreateJob(status);

        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(job, ProcessId, BaseUrl);

        statusInfo.Links.Should().NotBeNull();
        statusInfo.Links!.Value.Should().NotContain(l => l.Rel == MonitorRelation,
            "terminal jobs no longer need a polling relation");
    }

    [Fact]
    [Operation(Operations.JobStatus)]
    public void ToOgcStatusInfo_AlwaysIncludesSelfLink()
    {
        var job = CreateJob(ExecutionJobStatus.Running);

        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(job, ProcessId, BaseUrl);

        statusInfo.Links.Should().NotBeNull();
        statusInfo.Links!.Value.Should().ContainSingle(l => l.Rel == "self");
    }

    [Theory]
    [InlineData(ExecutionJobStatus.Queued, "accepted")]
    [InlineData(ExecutionJobStatus.Provisioning, "accepted")]
    [InlineData(ExecutionJobStatus.Running, "running")]
    [InlineData(ExecutionJobStatus.Succeeded, "successful")]
    [InlineData(ExecutionJobStatus.Failed, "failed")]
    [InlineData(ExecutionJobStatus.Cancelled, "dismissed")]
    [Operation(Operations.JobStatus)]
    public void ToOgcStatus_MapsCanonicalToOgcString(ExecutionJobStatus canonical, string expected)
    {
        OgcProcessesConversionHelpers.ToOgcStatus(canonical).Should().Be(expected);
    }

    [Fact]
    [Operation(Operations.ProcessExecution)]
    public void TryParseStep_TypedRasterSource_PreservesBinding()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "stepId": "s1",
              "kind": "geoprocess",
              "processId": "surface.slope",
              "rasterSources": {
                "source": {
                  "sourceType": "inline",
                  "sourceContractVersion": 1,
                  "version": "inline-v1",
                  "content": { "sizeBytes": 3, "mediaType": "image/tiff" },
                  "securityContext": {
                    "tenantId": "default",
                    "authorizationSnapshotReference": "request-context"
                  },
                  "payload": "AAAA"
                }
              }
            }
            """);

        var parsed = ProcessEndpoints.TryParseStep(document.RootElement, out var step, out var error);

        parsed.Should().BeTrue(error);
        step.Should().NotBeNull();
        var source = step!.RasterSources.Should().ContainKey("source").WhoseValue;
        var inline = source.Should().BeOfType<InlineRasterSourceDescriptor>().Subject;
        inline.Payload.Should().Equal(0, 0, 0);
    }

    [Fact]
    [Operation(Operations.ProcessExecution)]
    public void TryConvertGeoJsonInput_LegacyGeometryService_ReturnsSanitizedError()
    {
        using var document = JsonDocument.Parse(
            """{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{}}""");

        var converted = ProcessEndpoints.TryConvertGeoJsonInput(
            "wkb",
            document.RootElement,
            4326,
            new LegacyGeometryService(),
            out var normalized,
            out var error);

        converted.Should().BeFalse();
        normalized.Should().BeNull();
        error.Should().Be("Input 'wkb' must contain valid GeoJSON geometry.");
    }

    private static ExecutionJobRecord CreateJob(ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "test-job-001",
            Status = status,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test-backend",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "test-workload"
            }
        };
    }

    private sealed class LegacyGeometryService : IGeometryService
    {
        public (bool HasZ, bool HasM) DetectZM(byte[]? wkb) => (false, false);

        public (bool HasZ, bool HasM) DetectZM(Memory<byte> wkb) => (false, false);

        public string? ConvertWkbToGeoJson(byte[]? wkb) => null;

        public string? ConvertWkbToGeoJson(Memory<byte> wkb) => null;

        public byte[]? ConvertGeoJsonToWkb(string? geoJson, int? srid = null) => [1, 2, 3];

        public byte[]? ConvertWktToWkb(string? wkt, int? srid = null) => null;

        public GeometryInfo? GetGeometryInfo(byte[]? wkb) => null;
    }
}
