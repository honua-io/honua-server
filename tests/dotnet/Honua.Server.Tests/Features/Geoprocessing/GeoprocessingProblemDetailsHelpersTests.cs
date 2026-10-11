// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingProblemDetailsHelpersTests
{
    [Fact]
    public async Task StoreUnavailable_WithReceipt_EmitsCanonicalCapabilityProblem()
    {
        var context = BuildContext();
        var exception = GeoprocessingStoreUnavailableException.ForCause(
            DurableJobSubstrateCause.RedisNotEntitled);

        await GeoprocessingProblemDetailsHelpers.StoreUnavailable(context, exception)
            .ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be(CapabilityUnavailableCodes.ProblemType);
        root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.EntitlementErrorCode);
        root.GetProperty("capability").GetString().Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        root.GetProperty("missingEntitlement").GetString().Should().Be(CapabilityUnavailableCodes.RedisCacheEntitlement);
        root.TryGetProperty("missingDependency", out _).Should().BeFalse();
        root.GetProperty("remediation").GetString().Should().Be(CapabilityUnavailableCodes.EntitlementRemediation);
    }

    [UnitTest]
    public async Task IdempotencyConflict_Returns409WithConflictingJobId()
    {
        var context = BuildContext();
        var exception = new GeoprocessingIdempotencyConflictException("job-existing");

        await GeoprocessingProblemDetailsHelpers.IdempotencyConflict(context, exception)
            .ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var root = await ReadBodyAsync(context);
        root.GetProperty("code").GetString().Should().Be("idempotency_conflict");
        root.GetProperty("conflictingJobId").GetString().Should().Be("job-existing");
    }

    [UnitTest]
    public async Task Admission_WhenThrottled_Returns429WithRetryAfter()
    {
        var context = BuildContext();
        var exception = new GeoprocessingAdmissionException(
            ExecutionAdmissionOutcome.Throttled,
            ExecutionAdmissionDimension.Rate,
            "admission.rate",
            "Too many submissions.",
            retryAfterSeconds: 12);

        await GeoprocessingProblemDetailsHelpers.Admission(context, exception)
            .ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().Should().Be("12");
        var root = await ReadBodyAsync(context);
        root.GetProperty("code").GetString().Should().Be("admission_throttled");
        root.GetProperty("retryAfterSeconds").GetInt32().Should().Be(12);
        root.GetProperty("policyRef").GetString().Should().Be("admission.rate");
    }

    [UnitTest]
    public async Task Admission_WhenDenied_Returns503WithRetryAfter()
    {
        var context = BuildContext();
        var exception = new GeoprocessingAdmissionException(
            ExecutionAdmissionOutcome.Denied,
            ExecutionAdmissionDimension.Concurrency,
            "admission.concurrency",
            "Capacity is exhausted.",
            retryAfterSeconds: 30);

        await GeoprocessingProblemDetailsHelpers.Admission(context, exception)
            .ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().Should().Be("30");
        var root = await ReadBodyAsync(context);
        root.GetProperty("code").GetString().Should().Be("admission_denied");
        root.GetProperty("policyRef").GetString().Should().Be("admission.concurrency");
    }

    [UnitTest]
    public async Task ApprovalRequired_Returns403WithPolicyAndProposal()
    {
        var context = BuildContext();
        var exception = new GeoprocessingApprovalRequiredException("policy.export", proposalId: "proposal-9");

        await GeoprocessingProblemDetailsHelpers.ApprovalRequired(context, exception)
            .ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var root = await ReadBodyAsync(context);
        root.GetProperty("code").GetString().Should().Be("approval_required");
        root.GetProperty("policyRef").GetString().Should().Be("policy.export");
        root.GetProperty("proposalId").GetString().Should().Be("proposal-9");
        root.GetProperty("detail").GetString().Should().Contain("proposal-9");
    }

    private static async Task<JsonElement> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static DefaultHttpContext BuildContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Request = { Path = "/api/v1/analysis/reports/job-1" },
            Response = { Body = new MemoryStream() },
        };
    }
}
