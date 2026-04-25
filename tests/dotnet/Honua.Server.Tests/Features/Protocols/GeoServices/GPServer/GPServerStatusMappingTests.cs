// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Unit tests for GPServer lifecycle state mapping per ADR-0029.
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerStatusMappingTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public void Queued_MapsToEsriJobSubmitted()
    {
        GPServerStatusMapping.ToEsriJobStatus(ExecutionJobStatus.Queued)
            .Should().Be("esriJobSubmitted");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public void Provisioning_MapsToEsriJobWaiting()
    {
        GPServerStatusMapping.ToEsriJobStatus(ExecutionJobStatus.Provisioning)
            .Should().Be("esriJobWaiting");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public void Running_MapsToEsriJobExecuting()
    {
        GPServerStatusMapping.ToEsriJobStatus(ExecutionJobStatus.Running)
            .Should().Be("esriJobExecuting");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public void Succeeded_MapsToEsriJobSucceeded()
    {
        GPServerStatusMapping.ToEsriJobStatus(ExecutionJobStatus.Succeeded)
            .Should().Be("esriJobSucceeded");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public void Failed_MapsToEsriJobFailed()
    {
        GPServerStatusMapping.ToEsriJobStatus(ExecutionJobStatus.Failed)
            .Should().Be("esriJobFailed");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public void Cancelled_MapsToEsriJobCancelled()
    {
        GPServerStatusMapping.ToEsriJobStatus(ExecutionJobStatus.Cancelled)
            .Should().Be("esriJobCancelled");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public void IsTerminalEsriStatus_IdentifiesTerminalStates()
    {
        GPServerStatusMapping.IsTerminalEsriStatus("esriJobSucceeded").Should().BeTrue();
        GPServerStatusMapping.IsTerminalEsriStatus("esriJobFailed").Should().BeTrue();
        GPServerStatusMapping.IsTerminalEsriStatus("esriJobCancelled").Should().BeTrue();
        GPServerStatusMapping.IsTerminalEsriStatus("esriJobSubmitted").Should().BeFalse();
        GPServerStatusMapping.IsTerminalEsriStatus("esriJobExecuting").Should().BeFalse();
        GPServerStatusMapping.IsTerminalEsriStatus("esriJobWaiting").Should().BeFalse();
    }
}
