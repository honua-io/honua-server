// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
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
        var requestHash = RequestHash([itemId.ToString("D"), versionId.ToString("D"), contentHash, route, visibility, argument.Note ?? string.Empty]);
        var explicitKey = NonBlank(argument.IdempotencyKey)
            ?? NonBlank(httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault());
        if (explicitKey is { Length: > MaxIdempotencyKeyLength })
        {
            throw new GeoprocessingValidationException($"'idempotencyKey' must be at most {MaxIdempotencyKeyLength} characters.");
        }

        var mutationContext = new StudioDraftMutationContext
        {
            PrincipalId = actorId,
            TenantId = httpContext.RequestServices.GetService<ITenantContext>()?.TenantId,
            SchemaName = httpContext.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
            CorrelationId = httpContext.TraceIdentifier,
            IdempotencyRequestHash = requestHash,
            AuthorizationOutcome = "authorized",
            Roles = AgentPrincipal.EffectiveRoles(principal),
            ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal),
            RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
                .OrderBy(static scope => scope, StringComparer.Ordinal).ToArray(),
            // Every MCP caller is an agent, administrators included, so the publication
            // always waits for a person. Without this floor a direct-execute edition published
            // first and reported failure after the published pointer had already moved.
            ActionDiscriminator = BuiltInGuardrailActions.StudioPublicationProposal,
        };
        var runtime = RequireMutationRuntime(httpContext);
        var submission = new ProposalSubmission(
            httpContext,
            async key => (await runtime.CreatePublicationRequestAsync(
                    itemId, versionId, contentHash, intent, argument.Note, actorId,
                    mutationContext with { IdempotencyKey = key },
                    cancellationToken).ConfigureAwait(false)).Operation,
            cancellationToken);
        var (operation, idempotencyKey) = explicitKey is null
            ? await submission.SubmitDerivedAsync(DerivedKeyPrefix + requestHash).ConfigureAwait(false)
            : await submission.SubmitAsync(explicitKey).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(
            ProposalOutput(operation, idempotencyKey),
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
                $"'route' is a public path and must start with '/', for example '/{argument.Route.Trim().TrimStart('/')}'.");
        }

        return (itemId, versionId, argument.ContentHash, argument.Route, argument.Visibility);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private const string DerivedKeyPrefix = "mcp-propose-publication:";
    private const int MaxIdempotencyKeyLength = 200;
    private const int MaxDerivedChain = 8;

    // Length-prefixed fields so no two argument sets share an encoding. The runtime scopes
    // keys to the caller's tenant and principal and binds each key to this hash.
    private static string RequestHash(string[] parts)
    {
        var material = string.Join('|', parts.Select(static part => $"{part.Length}:{part}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    // Submits one publication proposal under an idempotency key and decides what a replay means.
    private sealed class ProposalSubmission(
        HttpContext httpContext,
        Func<string, Task<OperationHandle>> submit,
        CancellationToken cancellationToken)
    {
        private readonly DateTimeOffset _startedAt =
            (httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

        // A caller-chosen key is honoured as given; a replayed proposal that ended without
        // publishing is refused with the advice to choose a new key.
        public async Task<(OperationHandle Operation, string Key)> SubmitAsync(string key)
        {
            var operation = await SubmitOnceAsync(key).ConfigureAwait(false);
            return await IsSpentReplayAsync(operation).ConfigureAwait(false)
                ? throw Spent(operation)
                : (operation, key);
        }

        // A derived key must not pin the caller to a proposal that cannot lead anywhere: when
        // the replayed proposal predates this call and failed, was cancelled or expired, the
        // next key in a chain seeded by that operation files a fresh proposal.
        public async Task<(OperationHandle Operation, string Key)> SubmitDerivedAsync(string seed)
        {
            var key = seed;
            for (var hop = 1; ; hop++)
            {
                var operation = await SubmitOnceAsync(key).ConfigureAwait(false);
                if (!await IsSpentReplayAsync(operation).ConfigureAwait(false))
                {
                    return (operation, key);
                }

                if (hop >= MaxDerivedChain)
                {
                    throw Spent(operation);
                }

                key = $"{seed}:{operation.OperationInstanceId}";
            }
        }

        private async Task<OperationHandle> SubmitOnceAsync(string key)
        {
            var operation = await submit(key).ConfigureAwait(false);
            return IsRetryConflict(operation)
                ? throw new GeoprocessingPreconditionFailedException(
                    $"{operation.Reason} Send a new idempotencyKey for a different publication request.")
                : operation;
        }

        private async Task<bool> IsSpentReplayAsync(OperationHandle operation)
        {
            if (operation.CreatedAt >= _startedAt.AddMilliseconds(-1))
            {
                return false;
            }

            if (operation.Status is OperationHandleStatus.Failed or OperationHandleStatus.Cancelled)
            {
                return true;
            }

            if (operation.Status != OperationHandleStatus.RequiresApproval || string.IsNullOrWhiteSpace(operation.ProposalId)
                || httpContext.RequestServices.GetService<IOperationProposalStore>() is not { } proposals)
            {
                return false;
            }

            var proposal = await proposals.GetAsync(operation.ProposalId, cancellationToken).ConfigureAwait(false);
            return proposal is { Status: OperationProposalStatus.AwaitingApproval }
                && OperationProposalApprovalWindow.IsElapsed(proposal, _startedAt);
        }

        private static GeoprocessingPreconditionFailedException Spent(OperationHandle operation) => new(
            $"The publication proposal for this idempotencyKey ended as {operation.Status} without publishing. "
            + "Send a new idempotencyKey to propose again.");

        private static bool IsRetryConflict(OperationHandle operation)
            => operation.Status == OperationHandleStatus.Failed
                && operation.Result?.Details.TryGetValue("errorKind", out var kind) == true
                && string.Equals(kind, "conflict", StringComparison.Ordinal);
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
