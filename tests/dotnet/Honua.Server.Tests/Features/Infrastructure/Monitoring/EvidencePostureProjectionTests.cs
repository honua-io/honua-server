// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class EvidencePostureProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AlertEvents_TruncatedPage_PreservesWindowAndFailsClosed()
    {
        var posture = EvidencePostureProjection.ForAlertEvents(
            Now,
            Now.AddMinutes(-10),
            Now,
            50,
            [Now.AddMinutes(-2), Now.AddMinutes(-1)],
            hasMore: true);

        var source = Assert.Single(posture.Sources);
        Assert.Equal(EvidenceCompletenessStatuses.Partial, source.Completeness);
        Assert.Equal(Now.AddMinutes(-10), source.Coverage.RequestedFrom);
        Assert.Equal(Now.AddMinutes(-1), source.Coverage.ReturnedTo);
        Assert.Equal(2, source.Coverage.ReturnedCount);
        Assert.Contains(EvidenceReasonCodes.PageTruncated, source.ReasonCodes);
        Assert.False(posture.Actionable);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AlertEvents_EmptyPage_DoesNotInventObservationTime()
    {
        var posture = EvidencePostureProjection.ForAlertEvents(
            Now,
            requestedFrom: null,
            requestedTo: null,
            requestedPageSize: 50,
            returnedTimestamps: [],
            hasMore: false);

        var source = Assert.Single(posture.Sources);
        Assert.Null(source.ObservedAt);
        Assert.Equal(EvidenceCompletenessStatuses.Unavailable, source.Completeness);
        Assert.Contains(EvidenceReasonCodes.MissingObservationTime, source.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void OperateEvents_SourceFailure_MapsToPartialComponentCoverage()
    {
        var posture = EvidencePostureProjection.ForOperateEvents(
            Now,
            Now.AddHours(-1),
            Now,
            100,
            [Now.AddMinutes(-1)],
            partialResult: true,
            hasMore: false,
            includedSources: ["alert"],
            failedSources: ["audit"]);

        var source = Assert.Single(posture.Sources);
        Assert.Equal(EvidenceCompletenessStatuses.Partial, source.Completeness);
        Assert.Equal(["alert"], source.Coverage.IncludedComponentIds);
        Assert.Equal(["alert", "audit"], source.Coverage.ExpectedComponentIds);
        Assert.Contains(EvidenceReasonCodes.PartialResult, source.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void OperateEvents_TruncatedPage_FailsClosed()
    {
        var posture = EvidencePostureProjection.ForOperateEvents(
            Now,
            Now.AddHours(-1),
            Now,
            1,
            [Now.AddMinutes(-1)],
            partialResult: false,
            hasMore: true,
            includedSources: ["alert", "audit"],
            failedSources: []);

        var source = Assert.Single(posture.Sources);
        Assert.True(source.Coverage.HasMore);
        Assert.True(source.Coverage.Truncated);
        Assert.Equal(EvidenceCompletenessStatuses.Partial, source.Completeness);
        Assert.Contains(EvidenceReasonCodes.PageTruncated, source.ReasonCodes);
        Assert.False(posture.Actionable);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void DeployOperations_HasMore_ReportsPageTruncation()
    {
        var posture = EvidencePostureProjection.ForDeployOperations(
            Now,
            page: 2,
            pageSize: 25,
            returnedCount: 25,
            hasMore: true,
            returnedTimestamps: [Now.AddMinutes(-1)]);

        var source = Assert.Single(posture.Sources);
        Assert.Equal(25, source.Coverage.RequestedPageSize);
        Assert.Equal(["page-2"], source.Coverage.IncludedComponentIds);
        Assert.Equal(EvidenceCompletenessStatuses.Partial, source.Completeness);
        Assert.Contains(EvidenceReasonCodes.PageTruncated, source.ReasonCodes);
    }
}
