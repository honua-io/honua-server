// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp;
using Honua.Server.Features.Mcp.Models;
using Honua.Server.Features.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Verifies that the MCP functional tools delegate to
/// <see cref="IGeoprocessingJobService"/> with the right resource type,
/// operation, and domain plan, and that they translate the service result into
/// the published MCP output shape.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class McpToolDelegationTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_DelegatesToDomainServiceAndReturnsStructuredOutput()
    {
        _jobService.ValidatePlan(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new PlanValidationResult
            {
                IsExecutable = true,
                RequiresApproval = true,
                Violations = [],
                Warnings = ["verify inputs"]
            });

        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpPlanArgument { Plan = McpTestFactory.CreateValidPlanInput() },
            McpJsonContext.Default.McpPlanArgument);

        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent.Should().NotBeNull();
        result.StructuredContent!.Value.GetProperty("isExecutable").GetBoolean().Should().BeTrue();
        result.StructuredContent!.Value.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        result.StructuredContent!.Value.GetProperty("warnings").GetArrayLength().Should().Be(1);

        _jobService.Received(1).EnsureCallerAuthorized(
            Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process, OperatorOperation.Read);
        _jobService.Received(1).ValidatePlan(
            Arg.Is<AnalysisPlan>(p => p.PlanId == "plan-1"), Arg.Any<ClaimsPrincipal>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_dry_run_plan")]
    public async Task DryRunPlan_DelegatesAndReturnsEstimatedArtifacts()
    {
        _jobService.DryRunPlan(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new DryRunResult
            {
                EstimatedDurationSeconds = 42,
                EstimatedArtifacts = [ArtifactKind.FeatureLayer],
                SideEffects = ["writes workspace scratch"]
            });

        var tool = new DryRunPlanTool(_jobService, NullLogger<DryRunPlanTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpPlanArgument { Plan = McpTestFactory.CreateValidPlanInput() },
            McpJsonContext.Default.McpPlanArgument);

        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("estimatedDurationSeconds").GetDouble().Should().Be(42);
        var artifacts = structured.GetProperty("estimatedArtifacts");
        artifacts.GetArrayLength().Should().Be(1);
        artifacts[0].GetString().Should().Be(nameof(ArtifactKind.FeatureLayer));
        structured.GetProperty("sideEffects").GetArrayLength().Should().Be(1);

        _jobService.Received(1).EnsureCallerAuthorized(
            Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process, OperatorOperation.Read);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_execute_plan")]
    public async Task ExecutePlan_DelegatesToSubmitJobAndReturnsResourceUri()
    {
        var now = DateTimeOffset.UtcNow;
        _jobService.SubmitJobAsync(
                Arg.Any<AnalysisPlan>(),
                "idem-key-1",
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobRecord
            {
                OperationId = "job-xyz",
                Status = ExecutionJobStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "buffer"
                }
            });

        var tool = new ExecutePlanTool(_jobService, NullLogger<ExecutePlanTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpExecutePlanArgument
            {
                Plan = McpTestFactory.CreateValidPlanInput(),
                IdempotencyKey = "idem-key-1"
            },
            McpJsonContext.Default.McpExecutePlanArgument);

        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("jobId").GetString().Should().Be("job-xyz");
        structured.GetProperty("status").GetString().Should().Be(nameof(ExecutionJobStatus.Queued));
        structured.GetProperty("resourceUri").GetString().Should().Be("honua://jobs/job-xyz");

        _jobService.Received(1).EnsureCallerAuthorized(
            Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process, OperatorOperation.Execute);
        await _jobService.Received(1).SubmitJobAsync(
            Arg.Is<AnalysisPlan>(p => p.PlanId == "plan-1"),
            "idem-key-1",
            Arg.Any<ClaimsPrincipal>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(m => m["submittedVia"] == "honua.mcp"),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_execute_plan")]
    public async Task ExecutePlan_BlankIdempotencyKey_IsPassedAsNull()
    {
        var now = DateTimeOffset.UtcNow;
        _jobService.SubmitJobAsync(
                Arg.Any<AnalysisPlan>(),
                Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobRecord
            {
                OperationId = "job-abc",
                Status = ExecutionJobStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                Spec = new ExecutionJobSpec
                {
                    Kind = ExecutionJobKind.Geoprocessing,
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local",
                    WorkloadName = "buffer"
                }
            });

        var tool = new ExecutePlanTool(_jobService, NullLogger<ExecutePlanTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpExecutePlanArgument
            {
                Plan = McpTestFactory.CreateValidPlanInput(),
                IdempotencyKey = "   "
            },
            McpJsonContext.Default.McpExecutePlanArgument);

        await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await _jobService.Received(1).SubmitJobAsync(
            Arg.Any<AnalysisPlan>(),
            (string?)null,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /mcp tools/call honua_cancel_job")]
    public async Task CancelJob_DelegatesAndReportsCancellationRequested()
    {
        var tool = new CancelJobTool(_jobService, NullLogger<CancelJobTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpCancelJobArgument { JobId = "job-xyz" },
            McpJsonContext.Default.McpCancelJobArgument);

        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("jobId").GetString().Should().Be("job-xyz");
        structured.GetProperty("cancellationRequested").GetBoolean().Should().BeTrue();

        await _jobService.Received(1).CancelJobAsync(
            "job-xyz", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /mcp tools/call honua_cancel_job")]
    public async Task CancelJob_WithBlankJobId_ThrowsValidation()
    {
        var tool = new CancelJobTool(_jobService, NullLogger<CancelJobTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpCancelJobArgument { JobId = "" },
            McpJsonContext.Default.McpCancelJobArgument);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _jobService.DidNotReceive().CancelJobAsync(
            Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_UnsupportedStepKind_ThrowsValidationBeforeDelegation()
    {
        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);
        var planInput = McpTestFactory.CreateValidPlanInput();
        ((List<McpPlanStepInput>)planInput.Steps!)[0].Kind = "BogusKind";
        var arguments = McpTestFactory.ToArguments(
            new McpPlanArgument { Plan = planInput },
            McpJsonContext.Default.McpPlanArgument);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        _jobService.DidNotReceiveWithAnyArgs().ValidatePlan(default!, default!);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_NullArguments_ThrowsValidation()
    {
        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);

        JsonElement? arguments = null;
        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_NullStepEntry_ThrowsValidationBeforeDelegation()
    {
        // A malformed `{"steps":[null]}` payload must surface as invalid_argument
        // instead of letting the NullReferenceException at step.Kind propagate
        // into the tool-error envelope as an internal error.
        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""
            {"plan":{"planId":"plan-1","steps":[null],"outputs":["FeatureLayer"]}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("Plan step at index 0");
        _jobService.DidNotReceiveWithAnyArgs().ValidatePlan(default!, default!);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_dry_run_plan")]
    public async Task DryRunPlan_NullStepEntry_ThrowsValidationBeforeDelegation()
    {
        var tool = new DryRunPlanTool(_jobService, NullLogger<DryRunPlanTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""
            {"plan":{"planId":"plan-1","steps":[null]}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        _jobService.DidNotReceiveWithAnyArgs().DryRunPlan(default!, default!);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_execute_plan")]
    public async Task ExecutePlan_NullStepEntry_ThrowsValidationBeforeDelegation()
    {
        var tool = new ExecutePlanTool(_jobService, NullLogger<ExecutePlanTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""
            {"plan":{"planId":"plan-1","steps":[null]}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        await _jobService.DidNotReceiveWithAnyArgs().SubmitJobAsync(
            default!, default!, default!, default!, default!);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_NullOutputEntry_ThrowsValidationBeforeDelegation()
    {
        // Sibling null-list case: outputs:[null] must also surface as
        // invalid_argument rather than propagate a null into Enum.TryParse.
        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""
            {"plan":{"planId":"plan-1","steps":[],"outputs":[null]}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("Plan output at index 0");
        _jobService.DidNotReceiveWithAnyArgs().ValidatePlan(default!, default!);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_NullWarningEntry_ThrowsValidationBeforeDelegation()
    {
        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""
            {"plan":{"planId":"plan-1","steps":[],"warnings":[null]}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("Plan warning at index 0");
        _jobService.DidNotReceiveWithAnyArgs().ValidatePlan(default!, default!);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_NullDependsOnEntry_ThrowsValidationBeforeDelegation()
    {
        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""
            {"plan":{"planId":"plan-1","steps":[{"stepId":"s1","kind":"Geoprocess","dependsOn":[null]}]}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("dependsOn");
        _jobService.DidNotReceiveWithAnyArgs().ValidatePlan(default!, default!);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_plan")]
    public async Task ValidatePlan_NullInputsValue_ThrowsValidationBeforeDelegation()
    {
        var tool = new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""
            {"plan":{"planId":"plan-1","steps":[{"stepId":"s1","kind":"Geoprocess","inputs":{"layer":null}}]}}
            """);

        var act = async () => await tool.InvokeAsync(
            McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("null value for input");
        _jobService.DidNotReceiveWithAnyArgs().ValidatePlan(default!, default!);
    }
}
