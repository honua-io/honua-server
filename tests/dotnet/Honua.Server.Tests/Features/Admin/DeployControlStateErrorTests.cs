// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Deploy state conflicts stay 409. A missing Redis store stays 503 (honua-server#5625).
/// </summary>
public sealed class DeployControlStateErrorTests
{
    [UnitTest]
    public async Task Submit_NonDeployOperation_Is409_AndMissingRedisStore_Is503()
    {
        var conflict = await SubmitAsync(StoreWith(WorkflowOperationKind.MetadataRelease));
        var conflictBody = conflict.Should().BeOfType<JsonHttpResult<ProblemDetailsResponse>>().Subject;
        conflictBody.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        conflictBody.Value!.Detail.Should().Be(
            "The requested deploy action conflicts with the current operation state.");
        conflictBody.Value.Detail.Should().NotContain("temporarily unavailable");

        var unavailable = await SubmitAsync(store: null);
        var problem = unavailable.Should().BeOfType<ProblemHttpResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        problem.ProblemDetails.Detail.Should().Be("Deploy control is temporarily unavailable.");
    }

    private static async Task<IResult> SubmitAsync(IWorkflowOperationStore? store)
    {
        var stores = store is null ? Array.Empty<IWorkflowOperationStore>() : new[] { store };
        var service = new DeployWorkflowService(
            Substitute.For<IDeployTargetRegistry>(),
            stores,
            Array.Empty<IDeployBackend>(),
            Substitute.For<IOperatorApprovalEvaluator>(),
            NullLogger<DeployWorkflowService>.Instance);
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var method = typeof(DeployControlEndpoints).GetMethod(
            "HandleSubmitDeployOperation",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        return await (Task<IResult>)method!.Invoke(null, ["op-1", null, service, context])!;
    }

    private static IWorkflowOperationStore StoreWith(WorkflowOperationKind kind)
    {
        var store = Substitute.For<IWorkflowOperationStore>();
        store.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns(new WorkflowOperationRecord
        {
            OperationId = "op-1",
            Kind = kind,
            Status = WorkflowOperationStatus.Planned,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        return store;
    }
}
