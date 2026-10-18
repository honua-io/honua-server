// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Styling;
using Honua.Server.Features.WorkflowPackages;

namespace Honua.Server.Features.Operations;

internal static class SharedStyleEditOperation
{
    public const string OperationId = "style.replace-shared";
    internal static OperationDescriptor BuildDescriptor() => new()
    {
        OperationId = OperationId,
        ProviderId = ServicePublishOperation.ProviderId,
        Category = "style",
        Title = "Replace reviewed shared style",
        Description = "Replace exact canonical style bytes and their reviewed layer impact after distinct human approval.",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new()
        {
            SideEffectClass = OperationSideEffectClass.MutatesMetadata,
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            Determinism = OperationDeterminism.RuntimeDynamic,
            SupportsDryRun = true
        },
        InputSchema = [],
        OutputSchema = [],
    };
}

internal sealed class SharedStyleEditExecutor(IServiceProvider services) : Honua.Core.Features.Operations.Abstractions.IOperationExecutor, IOperationCommittedReceiptReader, IOperationRequestPreparer
{
    public string OperationId => SharedStyleEditOperation.OperationId;

    public async Task<OperationRequest> PrepareAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
    {
        AgentApprovalEntitlement.Require(services);
        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            _ = await SharedStyleApprovedReplay.VerifyAsync(services, request, context, cancellationToken).ConfigureAwait(false);
            return request;
        }
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: true);
        var turn = await SharedStyleWorkspaceAuthorization.AuthorizeAsync(http, http.User,
            Required(request, "workspaceId"), Required(request, "turnId"), cancellationToken).ConfigureAwait(false);
        var actor = CanonicalSecurityActor.Resolve(http.User)!.ActorId;
        if (context.PrincipalId != actor || context.TenantId != turn.Binding.TenantId)
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        var intent = JsonSerializer.Deserialize(Required(request, "intent"), SharedStyleGovernanceJsonContext.Default.SharedStyleEditIntent)
            ?? throw new GeoprocessingValidationException("A complete proposed style is required.");
        SharedStyleAgentEditor.RequireIntent(turn, intent);
        request.Parameters.TryGetValue("restoreSourceProposalId", out var restoreSource);
        if (restoreSource is not null)
        {
            var restored = await SharedStyleHistory.LoadRestoreAsync(http, turn, restoreSource, cancellationToken).ConfigureAwait(false);
            if (StyleEditHashes.Content(restored.Style.GetRawText()) != StyleEditHashes.Content(intent.Style.GetRawText()))
                throw new GeoprocessingPreconditionFailedException("Restore bytes must match the verified canonical receipt.");
        }
        var current = await services.GetRequiredService<StyleEditImpactService>().ResolveAsync(intent.StyleId, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException("Shared style not found.");
        SharedStyleAgentEditor.RequireTask(turn, current);
        if (!current.Impact.CanEdit) throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        var (candidate, error) = await SharedStyleCandidatePreparation.PrepareAsync(current, intent.Style.GetRawText(), false, http, cancellationToken).ConfigureAwait(false);
        if (error is not null) throw new GeoprocessingValidationException(error.ErrorMessage ?? "The proposed style is invalid.");
        var credential = await AgentCredentialAuthority.CaptureAsync(http.User, services, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        var payload = new SharedStyleApprovalPayload
        {
            ActorId = actor,
            KeyId = credential.Kind == "api-key" ? Guid.Parse(credential.Id) : Guid.Empty,
            Credential = credential,
            Permissions = credential.Permissions,
            AuthorSecurityContext = AgentCredentialAuthority.IsDelegated(http.User)
                ? new Honua.Core.Features.Authorization.Domain.JobSecurityContext(actor, turn.Binding.TenantId,
                    credential.Permissions.Select(permission => new Honua.Core.Features.Authorization.Domain.JobSecurityClaim("permission", permission)).ToArray()) { OwnerActorId = actor }
                : await services.GetRequiredService<WorkflowAuthorSecurityContextCapturer>().CaptureAsync(http.User, cancellationToken).ConfigureAwait(false),
            WorkspaceId = turn.Binding.WorkspaceId,
            TurnId = turn.Binding.TurnId,
            OwnerActorId = turn.Binding.OwnerActorId!,
            TargetId = turn.Binding.TargetId,
            ReleaseId = turn.Binding.ReleaseId,
            TenantId = turn.Binding.TenantId,
            SchemaName = context.SchemaName,
            Impact = current.Impact,
            StoreSnapshot = current.StoreSnapshot,
            PrimaryResourceId = current.PrimaryResource?.Metadata.Id,
            Candidate = candidate!,
            BeforeMapLibreJson = current.CanonicalStyleJson,
            RestoreSourceProposalId = restoreSource,
        };
        return request with
        {
            Parameters = new Dictionary<string, string?>
            { ["sealedPayload"] = JsonSerializer.Serialize(payload, SharedStyleGovernanceJsonContext.Default.SharedStyleApprovalPayload) }
        };
    }

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        _ = Required(request, "sealedPayload");
        return Task.FromResult(new OperationValidation { IsValid = true, Status = "valid", Messages = [] });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
    {
        AgentApprovalEntitlement.Require(services);
        // Even an unexpected Allow decision cannot skip the canonical sealed approval proof.
        var replay = await SharedStyleApprovedReplay.VerifyAsync(services, request, context, cancellationToken).ConfigureAwait(false);
        var outcome = await services.GetRequiredService<SharedStyleEditor>().ApplyReviewedOnceAsync(replay, cancellationToken).ConfigureAwait(false);
        return CompletedHandle(replay, outcome, context);
    }

    public async Task<OperationHandle?> ReadCommittedReceiptAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var replay = await SharedStyleApprovedReplay.VerifyAsync(services, request, context, cancellationToken, receiptOnly: true).ConfigureAwait(false);
        if (replay.Receipt is null) return null;
        await replay.RevalidateReceiptAsync(cancellationToken).ConfigureAwait(false);
        // The same canonical editor performs only idempotent projection refresh for an existing receipt.
        var receipt = await services.GetRequiredService<SharedStyleEditor>().ApplyReviewedOnceAsync(replay, cancellationToken).ConfigureAwait(false);
        return CompletedHandle(replay, receipt, context);
    }

    private OperationHandle CompletedHandle(SharedStyleApprovedReplay replay,
        Honua.Core.Features.Styling.Domain.StyleMutationReceipt outcome, OperationPolicyContext context)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        return new()
        {
            OperationId = OperationId,
            OperationInstanceId = context.OperationInstanceId ?? throw new InvalidOperationException("Missing canonical operation instance."),
            CorrelationId = context.CorrelationId ?? "",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Result = new()
            {
                Summary = "Reviewed shared style replacement completed.",
                Details = new Dictionary<string, string>
                {
                    ["styleId"] = replay.Payload.Impact.StyleId,
                    ["styleVersion"] = outcome.ResultVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["contentHash"] = outcome.ResultContentHash
                }
            },
        };
    }

    public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
        => Task.FromResult(new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = handle.OperationId,
            CorrelationId = handle.CorrelationId,
            Status = handle.Status,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            Result = handle.Result,
            Reason = handle.Reason,
        });

    private static string Required(OperationRequest request, string name) => request.Parameters.TryGetValue(name, out var value) &&
        !string.IsNullOrWhiteSpace(value) ? value : throw new GeoprocessingValidationException($"'{name}' is required.");
}

internal sealed class SharedStyleEditApprovalMapper : IOperationApprovalRequestMapper
{
    public string OperationId => SharedStyleEditOperation.OperationId;
    public OperationGatewayRequest Map(IOperationDescriptor descriptor, OperationRequest request, OperationPolicyContext context, PolicyDecision decision)
    {
        if (descriptor.OperationId != OperationId || request.OperationId != OperationId ||
            !request.Parameters.TryGetValue("sealedPayload", out var json) || json is null)
            throw new InvalidOperationException("An authorized prepared shared style replacement is required.");
        var payload = JsonSerializer.Deserialize(json, SharedStyleGovernanceJsonContext.Default.SharedStyleApprovalPayload)
            ?? throw new InvalidOperationException("Invalid prepared shared style plan.");
        if (payload.ActorId != context.PrincipalId || payload.TenantId != context.TenantId)
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        return new()
        {
            OperationInstanceId = context.OperationInstanceId,
            OperationId = OperationId,
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = json,
            Plan = new()
            {
                Summary = $"Replace shared style '{payload.Impact.StyleId}' revision {payload.Impact.StyleVersion}.",
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = json,
                Diff = [.. (payload.RestoreSourceProposalId is { } restore ? new[] { "Restore retained appearance from canonical proposal: " + restore } : Array.Empty<string>()),
                    $"Current content: {payload.Impact.ContentHash}; impact: {payload.Impact.ImpactHash}",
                    $"Proposed content: {StyleEditHashes.Content(payload.Candidate.MapLibreJson)}",
                    .. payload.Impact.Associations.Select(item => $"Affected resource: {item.ResourceId}; service: {item.ServiceId ?? "unpublished"}"),
                    .. payload.Impact.Consumers.Select(item => $"Affected {item.Kind}: {item.PackageKey}; item {item.ItemId:D}; version {item.VersionId?.ToString("D") ?? "draft"}; state {item.PublicationState}"),
                    "Proposed canonical MapLibre: " + payload.Candidate.MapLibreJson],
            },
        };
    }
    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        var json = request.Plan?.ExecutionPayload ?? throw new InvalidOperationException("Missing sealed shared style plan.");
        var payload = JsonSerializer.Deserialize(json, SharedStyleGovernanceJsonContext.Default.SharedStyleApprovalPayload)
            ?? throw new InvalidOperationException("Invalid sealed shared style plan.");
        return new()
        {
            Request = new() { OperationId = OperationId, Parameters = new Dictionary<string, string?> { ["sealedPayload"] = json } },
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName,
        };
    }
}
