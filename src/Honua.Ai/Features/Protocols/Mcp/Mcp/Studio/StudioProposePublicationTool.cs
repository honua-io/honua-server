// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
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

internal sealed class ProposeStudioPublicationTool : StudioDraftToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_propose_publication";

    private readonly ILogger<ProposeStudioPublicationTool> _typedLogger;

    public ProposeStudioPublicationTool(IGeoprocessingJobService jobService, ILogger<ProposeStudioPublicationTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Propose Studio publication",
        Description =
            "Submit an exact saved Studio item/version/contentHash plus route and visibility. "
            + "An agent caller, including every MCP caller, receives a proposal; the publication pointer does not move "
            + "until a person approves it in Console.",
        InputSchema = StudioMcpSchemas.ProposePublicationArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioProposePublicationOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Propose Studio publication", destructive: false, idempotent: true)
    };

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
        var (itemId, versionId, contentHash, route, visibility) = RequireArguments(argument);

        // Authorize against the item owner before disclosing whether the item or version exists:
        // a missing item is an ownerless target, so a non-owner receives the same governed
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
            // keeps deciding; an existing item is refused when it belongs to another tenant.
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

        if (!string.Equals(version.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new GeoprocessingPreconditionFailedException("The supplied content hash does not match the saved Studio version.");
        }

        var actorId = ActorIdFor(authorization, principal);

        var intent = new StudioPublicationIntent
        {
            Route = route,
            Visibility = visibility,
        };

        // MCP clients cannot set a per-call header, so the argument comes first. Without either,
        // the call's own arguments are the key: an identical retry returns the original proposal
        // instead of filing a duplicate, which is what the idempotent annotation promises.
        var idempotencyKey = NonBlank(argument.IdempotencyKey)
            ?? NonBlank(httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault())
            ?? DerivedIdempotencyKey([itemId.ToString("D"), versionId.ToString("D"), contentHash, route, visibility, argument.Note ?? string.Empty]);
        var receipt = await RequireMutationRuntime(httpContext).CreatePublicationRequestAsync(
            itemId,
            versionId,
            contentHash,
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
                Roles = AgentPrincipal.EffectiveRoles(principal),
                ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal),
                RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
                    .OrderBy(static scope => scope, StringComparer.Ordinal).ToArray(),
                // Every MCP caller is an agent, administrators included, so the publication
                // always waits for a person. Without this floor a direct-execute edition published
                // first and reported failure after the published pointer had already moved.
                ActionDiscriminator = BuiltInGuardrailActions.StudioPublicationProposal,
            },
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(
            ProposalOutput(receipt.Operation, idempotencyKey),
            StudioMcpJsonContext.Default.McpStudioProposePublicationOutput);
    }

    private static (Guid ItemId, Guid VersionId, string ContentHash, string Route, string Visibility) RequireArguments(McpStudioProposePublicationArgument argument)
    {
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

        if (!argument.Route.StartsWith('/'))
        {
            throw new GeoprocessingValidationException(
                $"'route' is a public path and must start with '/', for example '/{argument.Route.Trim()}'.");
        }

        return (itemId, versionId, argument.ContentHash, argument.Route, argument.Visibility);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DerivedIdempotencyKey(string[] parts)
    {
        // Length-prefixed fields so no two argument sets share an encoding. The runtime scopes
        // the key to the caller's tenant and principal.
        var material = string.Join('|', parts.Select(static part => $"{part.Length}:{part}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"mcp-propose-publication:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    // A first call always enters approval. A retry with the same key returns that proposal in
    // whatever state a person has since put it, so it is reported as it stands. An operation
    // without a durable proposal never counts as one.
    private static McpStudioProposePublicationOutput ProposalOutput(OperationHandle operation, string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(operation.ProposalId)
            || string.IsNullOrWhiteSpace(operation.AuditId))
        {
            throw new GeoprocessingPreconditionFailedException(
                operation.Reason ?? "Studio publication intent did not enter durable approval.");
        }

        return new McpStudioProposePublicationOutput
        {
            Operation = operation,
            OperationInstanceId = operation.OperationInstanceId,
            ProposalId = operation.ProposalId!,
            ProposalUri = McpResourceUris.ProposalUri(operation.ProposalId!),
            AuditId = operation.AuditId!,
            CorrelationId = operation.CorrelationId,
            IdempotencyIdentity = idempotencyKey ?? operation.OperationInstanceId,
            Status = ProposalStatus(operation.Status),
            HumanConfirmationRequired = operation.Status == OperationHandleStatus.RequiresApproval,
            Message = ProposalMessage(operation.Status),
        };
    }

    private static string ProposalStatus(OperationHandleStatus status) => status switch
    {
        OperationHandleStatus.RequiresApproval => "AwaitingApproval",
        OperationHandleStatus.Completed => "Published",
        _ => status.ToString(),
    };

    private static string ProposalMessage(OperationHandleStatus status) => status switch
    {
        OperationHandleStatus.RequiresApproval => "Publication proposal is awaiting approval by a person in Console.",
        OperationHandleStatus.Completed => "This proposal was already approved and published; no new proposal was filed.",
        OperationHandleStatus.Rejected or OperationHandleStatus.Denied =>
            "This proposal was already decided and not published; no new proposal was filed. Send a new idempotencyKey to propose again.",
        _ => $"This proposal already exists with status {status}; no new proposal was filed.",
    };
}
