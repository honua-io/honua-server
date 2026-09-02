// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// MCP bridge from an immutable Studio publication intent into the canonical
/// typed operation/proposal/approval lifecycle.
/// </summary>
internal sealed class ProposeStudioPublicationTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_propose_publication";

    private readonly ILogger<ProposeStudioPublicationTool> _typedLogger;

    public ProposeStudioPublicationTool(IGeoprocessingJobService jobService, ILogger<ProposeStudioPublicationTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Propose Studio publication",
        Description =
            "Submit an exact saved Studio item/version/contentHash plus route and visibility for human approval. "
            + "Returns canonical operation and proposal identities; no publication pointer moves before separate-principal approval.",
        InputSchema = StudioMcpSchemas.ProposePublicationArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioProposePublicationOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Propose Studio publication", destructive: false, idempotent: true)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioProposePublication");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(
                httpContext,
                OperatorOperation.Create,
                StudioAuthorizationOperation.UpdateDraft,
                cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioProposePublicationArgument);
        var itemId = argument.ItemId is { } suppliedItemId && suppliedItemId != Guid.Empty
            ? suppliedItemId
            : throw new GeoprocessingValidationException("'itemId' is required.");
        var versionId = argument.VersionId is { } suppliedVersionId && suppliedVersionId != Guid.Empty
            ? suppliedVersionId
            : throw new GeoprocessingValidationException("'versionId' is required.");
        if (string.IsNullOrWhiteSpace(argument.ContentHash))
        {
            throw new GeoprocessingValidationException("'contentHash' is required.");
        }

        if (string.IsNullOrWhiteSpace(argument.Route) || string.IsNullOrWhiteSpace(argument.Visibility))
        {
            throw new GeoprocessingValidationException("'route' and 'visibility' are required.");
        }

        var version = await lifecycleService.GetVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException($"Studio content version '{versionId:D}' was not found.");
        var pointers = await lifecycleService.GetPointersAsync(itemId, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException($"Studio content item '{itemId:D}' was not found.");
        var authorization = RequireAuthorizationService(httpContext);
        await EnsureStudioAuthorizedAsync(
            httpContext,
            authorization,
            principal,
            StudioAuthorizationOperation.ReadContentItem,
            pointers.OwnerId,
            itemId.ToString("D"),
            "studio-content-item",
            OperatorOperation.Read,
            cancellationToken).ConfigureAwait(false);
        await EnsureStudioAuthorizedAsync(
            httpContext,
            authorization,
            principal,
            StudioAuthorizationOperation.PublishRequest,
            pointers.OwnerId,
            itemId.ToString("D"),
            "studio-content-item",
            OperatorOperation.Create,
            cancellationToken).ConfigureAwait(false);
        if (pointers.CurrentVersionId != versionId)
        {
            throw new GeoprocessingPreconditionFailedException("The publication proposal must bind the current saved Studio version.");
        }

        if (!string.Equals(version.ContentHash, argument.ContentHash, StringComparison.Ordinal))
        {
            throw new GeoprocessingPreconditionFailedException("The supplied content hash does not match the saved Studio version.");
        }

        var actorId = ActorIdFor(authorization, principal);

        var intent = new StudioPublicationIntent
        {
            Route = argument.Route,
            Visibility = argument.Visibility,
        };

        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();
        var receipt = await RequireMutationRuntime(httpContext).CreatePublicationRequestAsync(
            itemId,
            versionId,
            argument.ContentHash,
            intent,
            argument.Note,
            actorId,
            new StudioDraftMutationContext
            {
                PrincipalId = actorId,
                TenantId = httpContext.RequestServices.GetService<ITenantContext>()?.TenantId,
                SchemaName = httpContext.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
                CorrelationId = httpContext.TraceIdentifier,
                IdempotencyKey = idempotencyKey,
                AuthorizationOutcome = "authorized",
                Roles = principal.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToArray(),
                ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal),
                RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
                    .OrderBy(static scope => scope, StringComparer.Ordinal).ToArray(),
            },
            cancellationToken).ConfigureAwait(false);
        var operation = receipt.Operation;
        if (operation.Status != OperationHandleStatus.RequiresApproval
            || string.IsNullOrWhiteSpace(operation.ProposalId)
            || string.IsNullOrWhiteSpace(operation.AuditId))
        {
            throw new GeoprocessingPreconditionFailedException(
                operation.Reason ?? "Studio publication intent did not enter durable approval.");
        }

        var output = new McpStudioProposePublicationOutput
        {
            Operation = operation,
            OperationInstanceId = operation.OperationInstanceId,
            ProposalId = operation.ProposalId,
            ProposalUri = McpResourceUris.ProposalUri(operation.ProposalId),
            AuditId = operation.AuditId,
            CorrelationId = operation.CorrelationId,
            IdempotencyIdentity = idempotencyKey ?? operation.OperationInstanceId,
            Status = "AwaitingApproval",
            HumanConfirmationRequired = true,
            Message = "Publication proposal is awaiting approval by a separate authorized principal.",
        };

        return McpToolHelpers.SuccessResult(output, StudioMcpJsonContext.Default.McpStudioProposePublicationOutput);
    }
}
