// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
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
}
