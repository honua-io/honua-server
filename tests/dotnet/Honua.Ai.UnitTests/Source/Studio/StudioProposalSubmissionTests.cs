// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Submission = Honua.Ai.Protocols.Mcp.Studio.ProposeStudioPublicationTool.ProposalSubmission;

namespace Honua.Ai.UnitTests.Studio;

// An MCP publication proposal replays under its idempotency key. A derived key must move on
// from a replayed proposal that ended without publishing; a caller's own key must not.
[Protocol(TestProtocols.Mcp)]
public sealed class StudioProposalSubmissionTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.UtcNow.AddHours(-1);

    [UnitTest]
    public async Task DerivedKey_ReplayedFailure_FilesUnderTheNextKey()
    {
        var keys = new List<string>();
        var submission = Create(key =>
        {
            keys.Add(key);
            return keys.Count == 1
                ? Handle("opinst-old", OperationHandleStatus.Failed, Earlier)
                : Handle("opinst-new", OperationHandleStatus.RequiresApproval, DateTimeOffset.UtcNow);
        });

        var (operation, key) = await submission.SubmitDerivedAsync("seed");

        operation.OperationInstanceId.Should().Be("opinst-new");
        key.Should().Be("seed:opinst-old");
        keys.Should().Equal("seed", "seed:opinst-old");
    }

    [UnitTest]
    public async Task DerivedKey_ReplayedRejection_IsReportedNotRefiled()
    {
        var calls = 0;
        var submission = Create(_ =>
        {
            calls++;
            return Handle("opinst-old", OperationHandleStatus.Rejected, Earlier);
        });

        var (operation, _) = await submission.SubmitDerivedAsync("seed");

        operation.Status.Should().Be(OperationHandleStatus.Rejected);
        calls.Should().Be(1);
    }

    [UnitTest]
    public async Task DerivedKey_FailureFiledByThisCall_IsReturnedWithoutAnotherFiling()
    {
        var calls = 0;
        var submission = Create(_ =>
        {
            calls++;
            return Handle("opinst-fresh", OperationHandleStatus.Failed, DateTimeOffset.UtcNow);
        });

        var (operation, _) = await submission.SubmitDerivedAsync("seed");

        operation.OperationInstanceId.Should().Be("opinst-fresh");
        calls.Should().Be(1);
    }

    [UnitTest]
    public async Task DerivedKey_ChainIsBounded()
    {
        var calls = 0;
        var submission = Create(_ => Handle($"opinst-{++calls}", OperationHandleStatus.Cancelled, Earlier));

        var act = () => submission.SubmitDerivedAsync("seed");

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*Send a new idempotencyKey*");
        calls.Should().Be(Submission.MaxDerivedChain);
    }

    [UnitTest]
    public async Task CallerKey_ReplayedFailure_AsksForANewKey()
    {
        var submission = Create(_ => Handle("opinst-old", OperationHandleStatus.Failed, Earlier));

        var act = () => submission.SubmitAsync("caller-key");

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*ended as Failed*Send a new idempotencyKey*");
    }

    [UnitTest]
    public async Task KeyBoundToDifferentRequest_AsksForANewKey()
    {
        var submission = Create(_ => Conflict("The idempotency key is already bound to a different request."));

        var act = () => submission.SubmitAsync("caller-key");

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*Send a new idempotencyKey*");
    }

    [UnitTest]
    public async Task InFlightOriginal_AsksToRetryLater()
    {
        var submission = Create(_ => Conflict("The idempotent invocation is still being accepted or executed; retry later."));

        var act = () => submission.SubmitDerivedAsync("seed");

        (await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>())
            .Which.Message.Should().Contain("Retry the same call later").And.NotContain("new idempotencyKey");
    }

    [UnitTest]
    public async Task StoredValidationConflict_IsAnOutcomeNotAKeyConflict()
    {
        // A route claimed by another item is stored as errorKind=conflict without statusCode 409;
        // a derived-key retry must move on once it predates the call.
        var calls = 0;
        var submission = Create(_ => ++calls == 1
            ? Handle("opinst-old", OperationHandleStatus.Failed, Earlier) with
            {
                Result = new OperationResultSummary
                {
                    Summary = "Route is already claimed.",
                    Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["errorKind"] = "conflict" },
                },
            }
            : Handle("opinst-new", OperationHandleStatus.RequiresApproval, DateTimeOffset.UtcNow));

        var (operation, _) = await submission.SubmitDerivedAsync("seed");

        operation.OperationInstanceId.Should().Be("opinst-new");
    }

    private static Submission Create(Func<string, OperationHandle> submit)
    {
        var httpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        return new Submission(httpContext, key => Task.FromResult(submit(key)), CancellationToken.None);
    }

    private static OperationHandle Conflict(string reason) => Handle("opinst-old", OperationHandleStatus.Failed, Earlier) with
    {
        Reason = reason,
        Result = new OperationResultSummary
        {
            Summary = reason,
            Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["statusCode"] = "409", ["errorKind"] = "conflict" },
        },
    };

    private static OperationHandle Handle(string id, OperationHandleStatus status, DateTimeOffset createdAt) => new()
    {
        OperationInstanceId = id,
        OperationId = "studio.content.create-publication-request",
        Status = status,
        CorrelationId = "corr",
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };
}
