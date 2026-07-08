// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that proposes an in-scope mutating control-plane operation through the
/// shared <see cref="IOperationGateway"/>. When the guardrail tier is
/// <c>RequiresApproval</c> the tool returns a structured result carrying a
/// <c>proposalId</c> and the <c>honua://proposals/{id}</c> resource URI so the
/// agent can poll until a human resolves it, rather than failing (#1696).
/// </summary>
internal sealed class ProposeOperationTool : IMcpTool
{
    public const string ToolName = "honua_propose_operation";

    private const string AgentActorPrefix = "agent:";

    private readonly ILogger<ProposeOperationTool> _logger;

    public ProposeOperationTool(ILogger<ProposeOperationTool> logger)
    {
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Propose operation",
        Description = "Propose an in-scope mutating control-plane operation (admin config change, deploy, metadata release, or seed). "
            + "Returns the execution result directly when the edition permits, or a proposalId plus honua://proposals/{id} resource URI when human approval is required.",
        InputSchema = McpToolSchemas.ProposeOperationArgumentSchema,
        OutputSchema = McpToolOutputSchemas.ProposeOperationOutputSchema,
        // Write tool: it routes a mutating control-plane operation through the
        // approval gateway. Idempotent because it honors the optional
        // idempotencyKey; not flagged destructive at the propose layer (the
        // underlying operation class governs its own destructiveness).
        Annotations = McpToolAnnotationSets.Write("Propose operation", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ProposeOperation");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpProposeOperationArgument);

        // Executor-discovery surface (#2563): report which kinds are genuinely routable on every
        // response, including rejections, so an agent proposing an unsupported kind (Seed today)
        // learns the real supported set instead of hitting a silent dead end.
        var catalog = httpContext.RequestServices.GetService<IOperationExecutorCatalog>();
        var supportedKinds = catalog?.SupportedKinds
            .Select(supportedKind => supportedKind.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (string.IsNullOrWhiteSpace(argument.Kind) ||
            !Enum.TryParse<OperationClass>(argument.Kind, ignoreCase: true, out var kind) ||
            !Enum.IsDefined(kind))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = "Unknown or missing operation 'kind'. Expected one of: AdminConfigChange, Deploy, MetadataRelease, Seed."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        var gateway = httpContext.RequestServices.GetService<IOperationGateway>();
        if (gateway is null)
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "unavailable",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = "The operation gateway is unavailable (durable storage is not configured)."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        var actor = principal.Identity?.Name;
        var request = new OperationGatewayRequest
        {
            Kind = kind,
            RequestedByAgent = string.IsNullOrWhiteSpace(actor) ? $"{AgentActorPrefix}mcp" : $"{AgentActorPrefix}{actor}",
            RequestedBy = actor,
            Reason = argument.Reason,
            IdempotencyKey = argument.IdempotencyKey,
            ExecutionPayload = argument.ExecutionPayload,
        };

        var result = await gateway.RouteAsync(request, cancellationToken).ConfigureAwait(false);

        var output = new McpProposeOperationOutput
        {
            Outcome = result.Outcome.ToString(),
            RequiresApproval = result.Outcome == OperationGatewayOutcome.ProposalCreated,
            ProposalId = result.ProposalId,
            ResourceUri = result.ProposalId == null ? null : McpResourceUris.ProposalUri(result.ProposalId),
            ExecutionOperationId = result.ExecutionOperationId,
            SupportedKinds = supportedKinds,
            Message = result.Message,
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpProposeOperationOutput);
    }
}
