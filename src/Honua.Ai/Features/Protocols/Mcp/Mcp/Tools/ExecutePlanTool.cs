// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Licensing;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that submits an analysis plan for asynchronous execution. Delegates
/// to <see cref="IGeoprocessingJobService.SubmitJobAsync"/> and returns the job
/// identifier along with the <c>honua://jobs/{jobId}</c> resource URI so clients
/// can poll lifecycle state via <c>resources/read</c>.
/// </summary>
/// <remarks>
/// This is a mutating agent operation, so it crosses the AI-operations guardrail
/// ladder (#1631) through the shared, protocol-neutral
/// <see cref="IAgentGuardrailPolicy"/>:
/// <list type="bullet">
///   <item>Community — direct execution, no enforced guardrails.</item>
///   <item>Pro — requires the <c>ai.agent-operations</c> validation-layer
///   entitlement; denials surface as a <c>failed_precondition</c> tool error.</item>
///   <item>Enterprise — additionally requires the <c>ai.approval-workflows</c>
///   entitlement so approval/policy/audit guardrails are provisioned.</item>
/// </list>
/// </remarks>
internal sealed class ExecutePlanTool : IMcpTool
{
    public const string ToolName = "honua_execute_plan";

    private const string ProtocolMetadataSource = "honua.mcp";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ExecutePlanTool> _logger;

    public ExecutePlanTool(IGeoprocessingJobService jobService, ILogger<ExecutePlanTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Submit an analysis plan for asynchronous execution and return the job identifier and resource URI.",
        InputSchema = McpToolSchemas.ExecutePlanArgumentSchema
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ExecutePlan");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Execute, cancellationToken)
            .ConfigureAwait(false);

        // AI-operations guardrail ladder (#1631): agent-initiated execution is a
        // mutating operation, so it crosses the shared edition-driven policy.
        // Community runs directly; Pro requires the validation-layer entitlement;
        // Enterprise additionally requires the approval-workflows entitlement.
        EnforceGuardrails(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpExecutePlanArgument);
        var plan = McpToolHelpers.ToDomainPlan(argument.Plan);
        var idempotencyKey = string.IsNullOrWhiteSpace(argument.IdempotencyKey)
            ? null
            : argument.IdempotencyKey;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["submittedVia"] = ProtocolMetadataSource
        };

        var jobRecord = await _jobService
            .SubmitJobAsync(plan, idempotencyKey, principal, metadata, cancellationToken)
            .ConfigureAwait(false);

        var output = new McpExecuteOutput
        {
            JobId = jobRecord.OperationId,
            Status = jobRecord.Status.ToString(),
            CreatedAt = jobRecord.CreatedAt,
            ResourceUri = McpResourceUris.JobUri(jobRecord.OperationId)
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpExecuteOutput);
    }

    private static void EnforceGuardrails(HttpContext httpContext)
    {
        var policy = httpContext.RequestServices.GetService<IAgentGuardrailPolicy>();
        if (policy is null)
        {
            // No policy registered (lightweight test host): preserve Community
            // direct-execution behavior rather than failing closed.
            return;
        }

        var descriptor = new AgentOperationDescriptor
        {
            OperationId = ToolName,
            Protocol = ProtocolMetadataSource,
            IsMutating = true,
            IsDestructive = false,
            SupportsSnapshot = false,
        };

        var decision = policy.Evaluate(descriptor);
        if (!decision.HasGuardrails)
        {
            return;
        }

        // Pro validation layer: the agent-operations entitlement gates execution.
        var operations = LicenseGate.CheckEntitlement(
            httpContext.RequestServices, FeatureCatalog.AiOperationsKey);
        if (!operations.IsActive)
        {
            throw new GeoprocessingPreconditionFailedException(operations.UpgradeMessage);
        }

        // Enterprise approval + policy layer: the approval-workflows entitlement
        // must be provisioned before approval-gated agent execution proceeds.
        if (decision.RequiresApproval)
        {
            var approval = LicenseGate.CheckEntitlement(
                httpContext.RequestServices, FeatureCatalog.AiApprovalWorkflowsKey);
            if (!approval.IsActive)
            {
                throw new GeoprocessingPreconditionFailedException(approval.UpgradeMessage);
            }
        }
    }
}
