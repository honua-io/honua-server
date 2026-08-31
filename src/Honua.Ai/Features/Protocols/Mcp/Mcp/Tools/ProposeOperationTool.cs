// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that proposes an in-scope mutating control-plane operation through the
/// shared <see cref="IOperationGateway"/>. The model-facing contract always creates
/// an approval proposal and returns a structured result carrying a
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
        Description = "Propose a deploy or metadata-release operation for governed approval. "
            + "This model-facing tool never executes an operation directly.",
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
            .Where(McpProposableOperationKinds.Contains)
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
                    Message = "Unknown or missing operation 'kind'. Expected one of: Deploy, MetadataRelease."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        if (!McpProposableOperationKinds.Contains(kind))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = $"Operation kind '{kind}' is not safely representable by this generic proposal surface."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        var scopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal);
        var recognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(principal);
        if (scopeGoverned && !OperatorScopeCatalog.PermitsOperation(recognizedScopes, OperatorOperation.Publish))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "rejected",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = $"Operation kind '{kind}' requires OAuth scope '{OperatorScopeCatalog.Publish}'."
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

        var actor = McpAuthorizationHelper.ResolveActorId(principal);
        var request = new OperationGatewayRequest
        {
            Kind = kind,
            RequestedByAgent = string.IsNullOrWhiteSpace(actor) ? $"{AgentActorPrefix}mcp" : $"{AgentActorPrefix}{actor}",
            RequestedBy = actor,
            Reason = argument.Reason,
            IdempotencyKey = argument.IdempotencyKey,
            ExecutionPayload = argument.ExecutionPayload,
            ScopeGoverned = scopeGoverned,
            RecognizedScopes = recognizedScopes.OrderBy(scope => scope, StringComparer.Ordinal).ToArray(),
        };

        var envelopeFactory = httpContext.RequestServices.GetService<IOperationEnvelopeFactory>();
        if (envelopeFactory is null)
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = "unavailable",
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = "The canonical operation envelope runtime is unavailable."
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        var accepted = await envelopeFactory.CreateAcceptedAsync(
            $"control-plane.{kind.ToString().ToLowerInvariant()}",
            new OperationPolicyContext
            {
                PrincipalId = actor,
                IdempotencyKey = argument.IdempotencyKey,
                AuthorizationOutcome = "mcp-authorized",
                ScopeGoverned = request.ScopeGoverned,
                RecognizedScopes = request.RecognizedScopes,
            },
            cancellationToken).ConfigureAwait(false);
        if (accepted.Status == OperationHandleStatus.Failed || string.IsNullOrWhiteSpace(accepted.AuditId))
        {
            return McpToolHelpers.SuccessResult(
                new McpProposeOperationOutput
                {
                    Outcome = OperationGatewayOutcome.Failed.ToString(),
                    RequiresApproval = false,
                    SupportedKinds = supportedKinds,
                    Message = accepted.Reason
                        ?? "The operation could not be durably accepted and audited.",
                },
                McpJsonContext.Default.McpProposeOperationOutput);
        }

        var result = await gateway.CreateApprovalProposalAsync(
            accepted.OperationInstanceId,
            request with
            {
                OperationInstanceId = accepted.OperationInstanceId,
                CorrelationId = accepted.CorrelationId,
            },
            cancellationToken).ConfigureAwait(false);

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
