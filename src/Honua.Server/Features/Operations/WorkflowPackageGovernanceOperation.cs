// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.WorkflowPackages;

namespace Honua.Server.Features.Operations;

internal static class WorkflowPackageGovernanceOperation
{
    public const string OperationId = "workflow-package.governance";
    public static OperationDescriptor BuildDescriptor() => new()
    {
        OperationId = OperationId,
        ProviderId = ServicePublishOperation.ProviderId,
        Title = "Govern immutable workflow publication",
        Category = "workflow",
        Description = "Publish, delete or change the status of an exact immutable workflow publication after distinct human approval.",
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

internal sealed class WorkflowPackageGovernanceExecutor(IServiceProvider services) : IOperationExecutor, IOperationRequestPreparer
{
    public string OperationId => WorkflowPackageGovernanceOperation.OperationId;

    public async Task<OperationRequest> PrepareAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            _ = await WorkflowPackageApprovedReplay.VerifyAsync(services, request, context, cancellationToken).ConfigureAwait(false);
            return request;
        }
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: true);
        var principal = http.User;
        var workspace = Required(request, "workspaceId");
        var turn = Required(request, "turnId");
        var access = await services.GetRequiredService<WorkflowPackageAuthorization>()
            .ResolveAsync(principal, cancellationToken, workspace, turn, true).ConfigureAwait(false);
        if (!access.WorkspaceAgent || string.IsNullOrWhiteSpace(access.DelegatedOwnerActorId) ||
            access.ActorId != context.PrincipalId || access.TenantId != context.TenantId) Deny();
        if (!Guid.TryParse(principal.FindFirst("api_key_id")?.Value, out var keyId)) Deny();
        var record = await services.GetRequiredService<IAdminApiKeyStore>().GetAsync(keyId, cancellationToken).ConfigureAwait(false);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (record is null || record.RevokedAt is not null || record.ExpiresAt <= now ||
            !record.Permissions.Order(StringComparer.Ordinal).SequenceEqual(
                principal.FindAll("permission").Select(claim => claim.Value).Order(StringComparer.Ordinal))) Deny();
        var binding = await services.GetRequiredService<IAgentWorkspaceTurnAuthorizer>()
            .AuthorizeAsync(principal, workspace, turn, cancellationToken).ConfigureAwait(false);
        if (binding is null || binding.OwnerActorId != access.DelegatedOwnerActorId || binding.TenantId != access.TenantId) Deny();
        var intent = JsonSerializer.Deserialize(Required(request, "intent"),
            WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageGovernanceIntent)
            ?? throw new GeoprocessingValidationException("A workflow governance intent is required.");
        if (intent.Action is not ("publish" or "delete" or "set-status") ||
            string.IsNullOrWhiteSpace(intent.PackageId) || intent.Version < 1 || string.IsNullOrWhiteSpace(intent.PackageHash) ||
            string.IsNullOrWhiteSpace(intent.PublicationId) ||
            !Enum.IsDefined(intent.Target) || !Enum.IsDefined(intent.Status))
            throw new GeoprocessingValidationException("An exact supported workflow transition and immutable version/hash are required.");
        if (intent.Action != "publish" && (intent.Target != default || intent.ProcessId is not null ||
            intent.Schedule is not null || !intent.Enabled) || intent.Action != "set-status" && intent.Status != default)
            throw new GeoprocessingValidationException("Only fields used by the requested workflow transition may be supplied.");
        var publication = await services.GetRequiredService<IWorkflowPackageStore>()
            .GetPublicationAsync(intent.PublicationId, cancellationToken).ConfigureAwait(false);
        var payload = new WorkflowPackageApprovalPayload
        {
            Intent = intent,
            AuthorSecurityContext = await services.GetRequiredService<WorkflowAuthorSecurityContextCapturer>().CaptureAsync(principal, cancellationToken).ConfigureAwait(false),
            KeyId = keyId,
            ActorId = access.ActorId,
            Permissions = record!.Permissions.ToArray(),
            TenantId = access.TenantId!,
            SchemaName = context.SchemaName,
            WorkspaceId = workspace,
            TurnId = turn,
            OwnerActorId = access.DelegatedOwnerActorId!,
            TargetId = binding!.TargetId,
            ReleaseId = binding.ReleaseId,
            PublicationFingerprint = publication is null ? null : WorkflowPackageApprovedReplay.Fingerprint(publication),
        };
        await WorkflowPackageApprovedReplay.VerifyResourceAsync(services, payload, access, cancellationToken).ConfigureAwait(false);
        return request with
        {
            Parameters = new Dictionary<string, string?>
            {
                ["sealedPayload"] = JsonSerializer.Serialize(payload, WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageApprovalPayload),
            }
        };
    }

    public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        _ = Required(request, "sealedPayload");
        return Task.FromResult(new OperationValidation { IsValid = true, Status = "valid", Messages = [] });
    }

    public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        // A direct Allow decision cannot bypass the durable proposal proof, even if a
        // caller invokes this typed executor outside the advertised governed adapter.
        var replay = await WorkflowPackageApprovedReplay.VerifyAsync(services, request, context, cancellationToken).ConfigureAwait(false);
        var packageService = replay.CreateService(services);
        var intent = replay.Payload.Intent;
        WorkflowPublication? publication = intent.Action switch
        {
            "publish" => await packageService.PublishVersionAsync(intent.PackageId, intent.Version, new()
            {
                PublicationId = intent.PublicationId,
                Target = intent.Target,
                ProcessId = intent.ProcessId,
                Schedule = intent.Schedule,
                Enabled = intent.Enabled,
            }, replay.Principal, cancellationToken).ConfigureAwait(false),
            "delete" => await packageService.DeletePublicationAsync(intent.PublicationId, replay.Principal, cancellationToken).ConfigureAwait(false),
            "set-status" => await packageService.SetPublicationStatusAsync(intent.PublicationId, intent.Status, replay.Principal, cancellationToken).ConfigureAwait(false),
            _ => throw new GeoprocessingValidationException("Unsupported workflow transition."),
        };
        if (publication is null) throw new GeoprocessingNotFoundException("Workflow publication was not found.");
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        return new OperationHandle
        {
            OperationId = OperationId,
            OperationInstanceId = context.OperationInstanceId ?? throw new InvalidOperationException("Missing canonical operation instance."),
            CorrelationId = context.CorrelationId ?? "",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Result = new OperationResultSummary
            {
                Summary = $"Workflow publication {intent.Action} completed.",
                Details = new Dictionary<string, string>
                {
                    ["packageId"] = intent.PackageId,
                    ["publicationId"] = publication.PublicationId,
                    ["packageHash"] = intent.PackageHash
                }
            },
        };
    }

    private static string Required(OperationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new GeoprocessingValidationException($"'{name}' is required.");
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
}

internal sealed class WorkflowPackageGovernanceApprovalMapper : IOperationApprovalRequestMapper
{
    public string OperationId => WorkflowPackageGovernanceOperation.OperationId;
    public OperationGatewayRequest Map(IOperationDescriptor descriptor, OperationRequest request,
        OperationPolicyContext context, PolicyDecision decision)
    {
        if (descriptor.OperationId != OperationId || request.OperationId != OperationId ||
            !request.Parameters.TryGetValue("sealedPayload", out var serialized) || serialized is null)
            throw new InvalidOperationException("An authorized prepared workflow governance operation is required.");
        var payload = JsonSerializer.Deserialize(serialized, WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageApprovalPayload)
            ?? throw new InvalidOperationException("Invalid prepared workflow governance payload.");
        if (payload.ActorId != context.PrincipalId || payload.TenantId != context.TenantId)
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        return new OperationGatewayRequest
        {
            OperationInstanceId = context.OperationInstanceId,
            OperationId = OperationId,
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = serialized,
            Plan = new OperationProposalPlan
            {
                Summary = $"Workflow {payload.Intent.Action}: {payload.Intent.PackageId} version {payload.Intent.Version}, publication {payload.Intent.PublicationId}.",
                Diff = [$"Immutable content hash: {payload.Intent.PackageHash}", $"Workspace: {payload.WorkspaceId}, frozen turn: {payload.TurnId}"],
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = serialized,
            },
        };
    }
    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        var serialized = request.Plan?.ExecutionPayload ?? throw new InvalidOperationException("Missing sealed workflow plan.");
        var payload = JsonSerializer.Deserialize(serialized, WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageApprovalPayload)
            ?? throw new InvalidOperationException("Invalid sealed workflow plan.");
        return new OperationApprovalReplayMapping
        {
            Request = new OperationRequest { OperationId = OperationId, Parameters = new Dictionary<string, string?> { ["sealedPayload"] = serialized } },
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName,
        };
    }
}
