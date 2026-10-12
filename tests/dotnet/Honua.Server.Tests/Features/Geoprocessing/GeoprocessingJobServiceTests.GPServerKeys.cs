// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed partial class GeoprocessingJobServiceTests
{
    private static readonly GPServerExecutionContext AreaExecution = new("test", "Area", "geometry.area");

    [UnitTheory]
    [InlineData("read:test")]
    [InlineData("write:test")]
    [InlineData("execute:other/geometry.area")]
    [InlineData("execute:test/geometry.buffer")]
    [InlineData("execute:test/Area")]
    [InlineData("execute:test")]
    [InlineData("execute:test/*")]
    [InlineData("execute:*/geometry.area")]
    [Operation(Operations.ErrorHandling)]
    public async Task GPScopedKey_InexactGrant_CannotAuthorizeExecution(string permission)
    {
        DenyGenericExecutionAndJobs();
        await FluentActions.Awaiting(() => _sut.EnsureGPServerExecutionAuthorizedAsync(
            GPKey(permission), AreaExecution)).Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task GPScopedKey_ExactGrant_SurvivesSharedSubmitAndPersistsBinding()
    {
        DenyGenericExecutionAndJobs();
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(true);
        var key = GPKey("read:test", "execute:test/geometry.area");
        await _sut.EnsureGPServerExecutionAuthorizedAsync(key, AreaExecution);
        var job = await _sut.SubmitGPServerJobAsync(AreaPlan(), null, key, AreaExecution);
        job.Spec.GPServerBinding.Should().Be(new GPServerJobBinding("test", "Area", "geometry.area"));
        job.Spec.Parameters[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions].Should().Be("geometry.area");
        _jobStore.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        (await _sut.GetGPServerJobAsync(job.OperationId, "test", "Area", key)).OperationId.Should().Be(job.OperationId);
        // SOAP polling discovers the published task from the owned record, not a client task assertion.
        (await _sut.GetGPServerJobAsync(job.OperationId, "test", null, key)).OperationId.Should().Be(job.OperationId);
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task GPScopedKey_MixedPlan_CannotSmuggleAnUnauthorizedProcess()
    {
        DenyGenericExecutionAndJobs();
        var plan = AreaPlan();
        plan = plan with { Steps = [.. plan.Steps, BufferStep("unauthorized")] };
        await FluentActions.Awaiting(() => _sut.SubmitGPServerJobAsync(
            plan, null, GPKey("execute:test/geometry.area"), AreaExecution))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobStore.DidNotReceiveWithAnyArgs().TryCreateAsync(default!, default, default);
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task GPScopedKey_ExecuteGrant_DoesNotAuthorizeGenericOperatorSubmissionOrJobAccess()
    {
        DenyGenericExecutionAndJobs();
        var key = GPKey("execute:test/geometry.area");
        var forged = new Dictionary<string, string>
        {
            [GeoprocessingProtocolMetadataKeys.GPServerServiceId] = "test",
            [GeoprocessingProtocolMetadataKeys.GPServerTaskName] = "Area",
        };
        await FluentActions.Awaiting(() => _sut.SubmitJobAsync(AreaPlan(), null, key, forged))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await FluentActions.Awaiting(() => _sut.EnsureCallerAuthorizedAsync(
            key, OperatorResourceType.Process, OperatorOperation.Execute))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await FluentActions.Awaiting(() => _sut.GetJobAsync("anything", key))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await FluentActions.Awaiting(() => _sut.CancelJobAsync("anything", key))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTheory]
    [InlineData("other", "Area", false)]
    [InlineData("test", "Buffer", false)]
    [InlineData("test", "Area", true)]
    [Operation(Operations.ErrorHandling)]
    public async Task GPScopedKey_JobRead_RequiresServiceTaskAndOwner(string service, string task, bool foreignOwner)
    {
        DenyGenericExecutionAndJobs();
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(true);
        var owner = GPKey("execute:test/geometry.area");
        var job = await _sut.SubmitGPServerJobAsync(AreaPlan(), null, owner, AreaExecution);
        _jobStore.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        var caller = foreignOwner ? GPKeyWithId(Guid.NewGuid(), "execute:test/geometry.area") : owner;
        var action = () => _sut.GetGPServerJobAsync(job.OperationId, service, task, caller);
        if (foreignOwner)
        {
            await FluentActions.Awaiting(action).Should().ThrowAsync<GeoprocessingNotFoundException>();
        }
        else
        {
            await FluentActions.Awaiting(action).Should().ThrowAsync<GeoprocessingAuthorizationException>();
        }
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task GPScopedKey_ParameterOnlyBinding_DoesNotAuthorizeLegacyOrOtherProtocolJob()
    {
        DenyGenericExecutionAndJobs();
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(true);
        var key = GPKey("execute:test/geometry.area");
        var job = await _sut.SubmitGPServerJobAsync(AreaPlan(), null, key, AreaExecution);
        job = job with { Spec = job.Spec with { GPServerBinding = null } };
        _jobStore.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        await FluentActions.Awaiting(() => _sut.GetGPServerJobAsync(job.OperationId, "test", "Area", key))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task GPScopedKey_OAuthPermissionLookalike_DoesNotAcquireAPIKeyAuthority()
    {
        DenyGenericExecutionAndJobs();
        var key = GPKey("execute:test/geometry.area");
        var identity = (ClaimsIdentity)key.Identity!;
        identity.RemoveClaim(identity.FindFirst("auth_type")!);
        identity.AddClaim(new Claim("auth_type", "oauth"));
        await FluentActions.Awaiting(() => _sut.EnsureGPServerExecutionAuthorizedAsync(key, AreaExecution))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task GPScopedKey_ExactMutatingGrant_StillRequiresMutatingTier()
    {
        DenyGenericExecutionAndJobs();
        DenyMutatingProcessPermission();
        var execution = new GPServerExecutionContext("test", "ImportDataset", "import.dataset");
        await FluentActions.Awaiting(() => _sut.SubmitGPServerJobAsync(CreateImportPlan(), null,
            GPKey("execute:test/import.dataset"), execution))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobStore.DidNotReceiveWithAnyArgs().TryCreateAsync(default!, default, default);
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task GPScopedKey_ExactGrant_StillRequiresCustomCodeTier()
    {
        DenyGenericExecutionAndJobs();
        _authEvaluator.EvaluateAsync(Arg.Any<ClaimsPrincipal>(),
            Arg.Is<OperatorAuthorizationRequest>(request => request.Operation == OperatorOperation.ExecuteCustomCode),
            Arg.Any<CancellationToken>()).Returns(Task.FromResult(AccessDecision.Forbidden("No custom code grant.")));
        await FluentActions.Awaiting(() => _sut.SubmitGPServerJobAsync(AreaPlan(), null,
            GPKey("execute:test/geometry.area"), AreaExecution,
            new Dictionary<string, string> { [CustomCodeJobContract.RuntimeParam] = "python" }))
            .Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobStore.DidNotReceiveWithAnyArgs().TryCreateAsync(default!, default, default);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task GPScopedKey_OwnedCancellation_RetainsBindingAndApprovalChecks()
    {
        DenyGenericExecutionAndJobs();
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(true);
        var key = GPKey("execute:test/geometry.area");
        var job = await _sut.SubmitGPServerJobAsync(AreaPlan(), null, key, AreaExecution);
        _jobStore.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        await _sut.CancelGPServerJobAsync(job.OperationId, "test", "Area", key);
        _approvalEvaluator.Received().Evaluate(key,
            Arg.Is<OperatorAuthorizationRequest>(request => request.ResourceType == OperatorResourceType.Job
                && request.Operation == OperatorOperation.Execute));
    }

    private void DenyGenericExecutionAndJobs()
        => _authEvaluator.EvaluateAsync(Arg.Any<ClaimsPrincipal>(),
            Arg.Is<OperatorAuthorizationRequest>(request =>
                request.ResourceType == OperatorResourceType.Job
                || request.ResourceType == OperatorResourceType.Process && request.Operation == OperatorOperation.Execute),
            Arg.Any<CancellationToken>()).Returns(Task.FromResult(AccessDecision.Forbidden("No operator grant.")));

    private static ClaimsPrincipal GPKey(params string[] permissions)
        => GPKeyWithId(Guid.Parse("11111111-1111-1111-1111-111111111111"), permissions);

    private static ClaimsPrincipal GPKeyWithId(Guid id, params string[] permissions)
        => new(new ClaimsIdentity([
            new Claim("api_key_id", id.ToString("D")),
            new Claim("auth_type", "admin-api-key"),
            new Claim(ClaimTypes.Role, "scoped-api-key"),
            .. permissions.Select(permission => new Claim("permission", permission))
        ], "ApiKey"));

    private static AnalysisPlan AreaPlan() => new()
    {
        PlanId = "scoped-area", IntentId = "gpserver:test:Area",
        Steps = [new AnalysisPlanStep
        {
            StepId = "area", Kind = AnalysisPlanStepKind.Geoprocess, ProcessId = "geometry.area",
            Inputs = new Dictionary<string, string> { ["wkb"] = "AAAA", ["srid"] = "3857" }
        }]
    };
}
