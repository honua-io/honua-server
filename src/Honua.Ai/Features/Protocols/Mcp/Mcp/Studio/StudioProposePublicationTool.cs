// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Guardrails.Domain;
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
            "Submit an exact saved Studio item/version/contentHash plus route and visibility. "
            + "An admin publishes that version in the same call and receives its share URL. "
            + "Any other caller receives a proposal; the publication pointer does not move until a separate principal approves it.",
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

        // Authorize against the item owner before disclosing whether the item or version exists
        // (#3429): a missing item is an ownerless target, so a non-owner receives the same governed
        // denial for an unknown id as for another owner's item.
        var pointers = await lifecycleService.GetPointersAsync(itemId, cancellationToken).ConfigureAwait(false);
        var authorization = RequireAuthorizationService(httpContext);
        await EnsureStudioAuthorizedAsync(
            httpContext,
            authorization,
            principal,
            StudioAuthorizationOperation.PublishRequest,
            pointers?.OwnerId,
            // A missing item carries the caller's own tenant so the ownerless-target denial
            // keeps deciding; an existing item is refused when it belongs to another tenant
            // (honua-server#4905).
            pointers is null ? RequestTenantId(httpContext) : pointers.TenantId,
            itemId.ToString("D"),
            "studio-content-item",
            OperatorOperation.Create,
            cancellationToken).ConfigureAwait(false);
        if (pointers is null)
        {
            throw new GeoprocessingNotFoundException($"Studio content item '{itemId:D}' was not found.");
        }

        var version = await lifecycleService.GetVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException($"Studio content version '{versionId:D}' was not found.");
        if (pointers.CurrentVersionId != versionId)
        {
            throw new GeoprocessingPreconditionFailedException("The publication proposal must bind the current saved Studio version.");
        }

        if (!string.Equals(version.ContentHash, argument.ContentHash, StringComparison.Ordinal))
        {
            throw new GeoprocessingPreconditionFailedException("The supplied content hash does not match the saved Studio version.");
        }

        var actorId = ActorIdFor(authorization, principal);
        // Admin publication is decided here, before the proposal guardrail is applied.
        // The approve route forbids this same principal from approving a proposal they opened.
        var isAdmin = authorization.IsAdmin(principal);

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
                // A non-admin proposal must wait for a separate principal on every edition; without
                // this floor a direct-execute edition published first and reported failure after
                // the published pointer had already moved (#3429). An admin does not take the floor.
                ActionDiscriminator = isAdmin ? null : BuiltInGuardrailActions.StudioPublicationProposal,
                PublishImmediately = isAdmin,
            },
            cancellationToken).ConfigureAwait(false);
        var operation = receipt.Operation;
        var output = isAdmin
            ? PublishedOutput(operation, receipt.Value, idempotencyKey)
            : ProposalOutput(operation, idempotencyKey);

        return McpToolHelpers.SuccessResult(output, StudioMcpJsonContext.Default.McpStudioProposePublicationOutput);
    }

    private static McpStudioProposePublicationOutput PublishedOutput(
        OperationHandle operation,
        StudioPublicationRequest? publication,
        string? idempotencyKey)
    {
        var route = publication?.Intent?.Route;
        if (operation.Status != OperationHandleStatus.Completed
            || string.IsNullOrWhiteSpace(operation.AuditId)
            || publication is not { Status: StudioPublicationRequestStatus.Accepted }
            || string.IsNullOrWhiteSpace(route))
        {
            throw new GeoprocessingPreconditionFailedException(
                operation.Reason ?? "Studio publication did not complete for the admin caller.");
        }

        return new McpStudioProposePublicationOutput
        {
            Operation = operation,
            OperationInstanceId = operation.OperationInstanceId,
            AuditId = operation.AuditId!,
            CorrelationId = operation.CorrelationId,
            IdempotencyIdentity = idempotencyKey ?? operation.OperationInstanceId,
            Status = "Published",
            HumanConfirmationRequired = false,
            ShareUrl = StudioPublishedRoutes.BuildActiveUrl(route!),
            Message = "Saved Studio version was published.",
        };
    }

    private static McpStudioProposePublicationOutput ProposalOutput(OperationHandle operation, string? idempotencyKey)
    {
        if (operation.Status != OperationHandleStatus.RequiresApproval
            || string.IsNullOrWhiteSpace(operation.ProposalId)
            || string.IsNullOrWhiteSpace(operation.AuditId))
        {
            throw new GeoprocessingPreconditionFailedException(
                operation.Reason ?? "Studio publication intent did not enter durable approval.");
        }

        return new McpStudioProposePublicationOutput
        {
            Operation = operation,
            OperationInstanceId = operation.OperationInstanceId,
            ProposalId = operation.ProposalId,
            ProposalUri = McpResourceUris.ProposalUri(operation.ProposalId!),
            AuditId = operation.AuditId!,
            CorrelationId = operation.CorrelationId,
            IdempotencyIdentity = idempotencyKey ?? operation.OperationInstanceId,
            Status = "AwaitingApproval",
            HumanConfirmationRequired = true,
            Message = "Publication proposal is awaiting approval by a separate authorized principal.",
        };
    }
}
