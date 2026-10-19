// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Import.FileImport.Durable;

namespace Honua.Server.Features.Operations;

// The existing proposal inbox remains the only approval store. This adapter is
// intentionally not registered until the job-kind and submission adapters are wired.
internal sealed class ExecutionJobLeasePolicy(IOperationProposalStore proposals,
    IOperatorScopeAuthorizer scopes, IOperatorAuthorizationEvaluator authorization,
    IOperatorApprovalEvaluator approvals, TimeProvider clock) : IJobExecutionLeasePolicy, IJobExecutionLeaseObservationPolicy
{
    internal const string ApprovalFormat = "honua.execution-job-approval/v1";
    private const int MaximumPayloadBytes = 1048576;

    public async Task<JobExecutionLeasePolicyDecision> AuthorizeIssuanceAsync(JobExecutionLeaseIssuanceRequest request,
        JobExecutionLeaseScope scope, CancellationToken cancellationToken = default)
    {
        var requirements = await EvaluateAsync(request.Executor, scope, cancellationToken).ConfigureAwait(false);
        var job = request.PreparedJob;
        if (scope.Kind != job.Spec.Kind || request.RequestedExpiresAt <= clock.GetUtcNow()) throw Denied();
        if (job.Audit.ProposalId is null)
        {
            if (requirements.Any(requirement => requirement.IsRequired)) throw Denied();
            return new()
            {
                IsAllowed = true,
                DecisionId = "benign:" + ScopeFingerprint(scope),
                MaximumExpiresAt = request.RequestedExpiresAt
            };
        }
        var receipt = await ReadApprovalAsync(job, scope, requirements, cancellationToken).ConfigureAwait(false);
        return new()
        {
            IsAllowed = true,
            DecisionId = receipt.Proposal.SealedPlanHash!,
            ApprovalReceiptId = receipt.Proposal.ProposalId,
            ApprovalPolicyRef = receipt.Proposal.Audit.ApprovalPolicyRef,
            MaximumExpiresAt = receipt.Binding.ExpiresAt < request.RequestedExpiresAt
                ? receipt.Binding.ExpiresAt : request.RequestedExpiresAt
        };
    }

    public async Task RequireCurrentAsync(ExecutionJobRecord job, JobExecutionLeasePolicyDecision decision,
        JobExecutionLeaseScope scope, IJobExecutionLeaseCurrentPolicy currentPolicy,
        CancellationToken cancellationToken = default)
    {
        if (scope.Kind != job.Spec.Kind || !decision.IsAllowed || decision.MaximumExpiresAt <= clock.GetUtcNow()) throw Denied();
        _ = ScopeFingerprint(scope);
        var requirements = new ApprovalRequirement[scope.Operations.Length];
        for (var index = 0; index < requirements.Length; index++)
        {
            var current = await currentPolicy.EvaluateAsync(scope.Operations[index], cancellationToken).ConfigureAwait(false);
            if (!current.Authorization.IsAllowed) throw Denied();
            requirements[index] = CopyRequirement(current.Approval);
        }
        if (decision.ApprovalReceiptId is null)
        {
            if (job.Audit.ProposalId is not null || decision.ApprovalPolicyRef is not null ||
                decision.DecisionId != "benign:" + ScopeFingerprint(scope) || requirements.Any(requirement => requirement.IsRequired)) throw Denied();
            return;
        }
        var receipt = await ReadApprovalAsync(job, scope, requirements, cancellationToken).ConfigureAwait(false);
        if (receipt.Proposal.ProposalId != decision.ApprovalReceiptId || receipt.Proposal.SealedPlanHash != decision.DecisionId ||
            receipt.Proposal.Audit.ApprovalPolicyRef != decision.ApprovalPolicyRef || receipt.Binding.ExpiresAt < decision.MaximumExpiresAt) throw Denied();
    }

    public async Task RequireObservationAsync(ExecutionJobRecord job, JobExecutionLeasePolicyDecision decision,
        JobExecutionLeaseScope scope, ClaimsPrincipal observer, JobExecutionLeaseObservationKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (!JobExecutionLeaseObservationEligibility.AllowsRetainedFact(job, kind)
            || scope.Kind != job.Spec.Kind || !decision.IsAllowed) throw Denied();
        // This validates retained historical receipt integrity only. Current observer
        // grants/provenance are independently checked by Host and the kind adapter;
        // expired execution approval is never renewed or restored as authority.
        if (decision.ApprovalReceiptId is null)
        {
            if (job.Audit.ProposalId is not null || decision.ApprovalPolicyRef is not null
                || decision.DecisionId != "benign:" + ScopeFingerprint(scope)) throw Denied();
            return;
        }
        var receipt = await ReadApprovalAsync(job, scope, null, cancellationToken, historical: true).ConfigureAwait(false);
        if (receipt.Proposal.ProposalId != decision.ApprovalReceiptId || receipt.Proposal.SealedPlanHash != decision.DecisionId
            || receipt.Proposal.Audit.ApprovalPolicyRef != decision.ApprovalPolicyRef
            || receipt.Binding.ExpiresAt < decision.MaximumExpiresAt) throw Denied();
    }

    // Caller uses the canonical gateway's reserved proposal identity in job.Audit
    // BEFORE generating this plan, then persists this exact plan through that gateway.
    // Planning grants no execution authority; issuance always re-reads the claimed proposal.
    internal async Task<OperationProposalPlan> PrepareApprovalPlanAsync(ExecutionJobRecord job,
        JobExecutionLeaseScope scope, ClaimsPrincipal executor, OperationGatewayRequest proposalRequest,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var descriptorId = proposalRequest.OperationId;
        var operationClass = proposalRequest.Kind;
        if (scope.Kind != job.Spec.Kind || string.IsNullOrWhiteSpace(job.Audit.ProposalId) || job.Audit.OperationInstanceId != job.OperationId ||
            proposalRequest.OperationInstanceId != job.OperationId || proposalRequest.TenantId != job.Audit.TenantId ||
            proposalRequest.RequestedBy != job.Audit.RequestedBy || string.IsNullOrWhiteSpace(proposalRequest.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(descriptorId) || descriptorId.Length > 256 || !Enum.IsDefined(operationClass) ||
            job.Audit.ProposalId != Honua.ControlPlane.OperationGateway.DeriveProposalId(operationClass, descriptorId, proposalRequest.IdempotencyKey, proposalRequest.TenantId) ||
            expiresAt <= clock.GetUtcNow() || expiresAt > clock.GetUtcNow().AddHours(24)) throw Denied();
        var requirements = await EvaluateAsync(executor, scope, cancellationToken).ConfigureAwait(false);
        if (!requirements.Any(requirement => requirement.IsRequired)) throw Denied();
        var binding = new ExecutionJobApprovalBinding(ApprovalFormat, descriptorId, operationClass,
            JobExecutionLeaseIntentFingerprint.Compute(job), ScopeFingerprint(scope), expiresAt, requirements);
        var payload = JsonSerializer.Serialize(binding, ExecutionJobApprovalJsonContext.Default.ExecutionJobApprovalBinding);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes) throw Denied();
        var review = ReviewIntent(job, proposalRequest.Plan);
        return new()
        {
            Summary = review.Summary,
            Diff = review.Diff.Concat(scope.Operations.Select(operation => $"{operation.ResourceType}: {operation.ResourceId ?? "all in scope"} — {operation.Operation}")).ToArray(),
            DryRun = review.DryRun.Concat([$"Execution authority expires at {expiresAt:O}.", "Approval is bound to this exact job, its source authority and its resource scope."]).ToArray(),
            Warnings = review.Warnings.ToArray(),
            RiskLevel = scope.Operations.Any(operation => operation.IsDestructive) || review.RiskLevel == ProposalRiskLevel.High
                ? ProposalRiskLevel.High : ProposalRiskLevel.Medium,
            ExecutionPayload = payload
        };
    }

    private static OperationProposalPlan ReviewIntent(ExecutionJobRecord job, OperationProposalPlan? domainPlan)
    {
        if (job.Spec.Kind == ExecutionJobKind.FileImport)
        {
            if (!FileImportJobParameters.TryParse(job.Spec.Parameters, out var request, out var bytes, out _) || request is null) throw Denied();
            var diff = new List<string>
            {
                $"Source: {request.FileName} ({bytes} bytes).",
                $"Destination: {request.TargetSchema ?? "default schema"}.{request.TableName}.",
                $"Data action: {request.EffectiveLoadMode}; target SRID: {request.TargetSrid}."
            };
            if (request.StagedSource is { } source)
            {
                diff.Add($"Verified source SHA-256: {source.Sha256}.");
                if (source.Transfer is { } transfer) diff.Add($"Import task: {transfer.TaskId}, revision {transfer.TaskRevision}.");
            }
            return new()
            {
                Summary = $"Import {request.FileName} into {request.TargetSchema ?? "default schema"}.{request.TableName}",
                Diff = diff,
                RiskLevel = request.EffectiveLoadMode == Honua.Core.Features.Import.Domain.ImportLoadMode.Replace ? ProposalRiskLevel.High : ProposalRiskLevel.Medium
            };
        }
        // Other job kinds must supply their existing canonical domain dry-run plan;
        // hashes and a generic job ID alone are not a reviewable human decision.
        if (domainPlan is null || string.IsNullOrWhiteSpace(domainPlan.Summary) || domainPlan.Summary.Length > 1024 ||
            domainPlan.Diff.Count is < 1 or > 128 || domainPlan.Diff.Any(line => line is null || line.Length > 2048) ||
            domainPlan.DryRun.Count > 128 || domainPlan.DryRun.Any(line => line is null || line.Length > 2048) ||
            domainPlan.Warnings.Count > 128 || domainPlan.Warnings.Any(line => line is null || line.Length > 2048) ||
            domainPlan.BlockingReasons.Count != 0) throw Denied();
        return domainPlan;
    }

    private async Task<ApprovalRequirement[]> EvaluateAsync(ClaimsPrincipal executor, JobExecutionLeaseScope scope,
        CancellationToken cancellationToken)
    {
        _ = ScopeFingerprint(scope);
        var result = new ApprovalRequirement[scope.Operations.Length];
        for (var index = 0; index < result.Length; index++)
        {
            var operation = scope.Operations[index];
            if (!scopes.Evaluate(executor, operation.ResourceType, operation.Operation).IsAllowed ||
                !(await authorization.EvaluateAsync(executor, operation, cancellationToken).ConfigureAwait(false)).IsAllowed) throw Denied();
            result[index] = CopyRequirement(approvals.Evaluate(executor, operation));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<(OperationProposal Proposal, ExecutionJobApprovalBinding Binding)> ReadApprovalAsync(ExecutionJobRecord job,
        JobExecutionLeaseScope scope, ApprovalRequirement[]? current, CancellationToken cancellationToken, bool historical = false)
    {
        var proposalId = job.Audit.ProposalId;
        if (string.IsNullOrWhiteSpace(proposalId) || proposalId.Length > 256) throw Denied();
        var proposal = await proposals.GetAsync(proposalId, cancellationToken).ConfigureAwait(false) ?? throw Denied();
        var now = clock.GetUtcNow();
        if (proposal.ProposalId != proposalId || proposal.TenantId != job.Audit.TenantId || proposal.RequestedBy != job.Audit.RequestedBy ||
            job.Audit.OperationInstanceId != job.OperationId || proposal.Audit.OperationInstanceId != job.OperationId ||
            (!historical && proposal.Status is not (OperationProposalStatus.Executing or OperationProposalStatus.Submitted or OperationProposalStatus.Reconciling)) ||
            (historical && proposal.Status is not (OperationProposalStatus.Executing or OperationProposalStatus.Submitted or OperationProposalStatus.Reconciling
                or OperationProposalStatus.Succeeded or OperationProposalStatus.Failed or OperationProposalStatus.RolledBack or OperationProposalStatus.Cancelled)) ||
            (proposal.ExecutionOperationId is not null && proposal.ExecutionOperationId != job.OperationId) ||
            string.IsNullOrWhiteSpace(proposal.Audit.AuditId) || string.IsNullOrWhiteSpace(proposal.ApprovedByAtClaim) ||
            proposal.ApprovedByAtClaim.Equals(proposal.RequestedBy, StringComparison.OrdinalIgnoreCase) ||
            proposal.ApprovedByAtClaim.Equals(proposal.RequestedByAgent, StringComparison.OrdinalIgnoreCase) ||
            proposal.ApprovedAtClaim is not { } approvedAt || approvedAt < proposal.CreatedAt || approvedAt > now ||
            proposal.Plan.BlockingReasons.Count != 0 || proposal.SealedPlanHash is not { Length: 64 } ||
            proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan) ||
            proposal.Plan.ExecutionPayload is not { } payload || Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes) throw Denied();
        ExecutionJobApprovalBinding binding;
        try { binding = JsonSerializer.Deserialize(payload, ExecutionJobApprovalJsonContext.Default.ExecutionJobApprovalBinding) ?? throw Denied(); }
        catch (JsonException exception) { throw new UnauthorizedAccessException("The exact job approval is unavailable or changed.", exception); }
        if (binding.Format != ApprovalFormat || string.IsNullOrWhiteSpace(binding.DescriptorId) || binding.DescriptorId != proposal.OperationId || binding.OperationClass != proposal.Kind ||
            binding.IntentFingerprint != JobExecutionLeaseIntentFingerprint.Compute(job) || binding.ScopeFingerprint != ScopeFingerprint(scope) ||
            (!historical && binding.ExpiresAt <= now) || binding.ExpiresAt <= approvedAt || binding.ExpiresAt - proposal.CreatedAt > TimeSpan.FromHours(24) ||
            binding.Requirements is null || binding.Requirements.Length != scope.Operations.Length ||
            !binding.Requirements.Any(requirement => requirement is { IsRequired: true })) throw Denied();
        for (var index = 0; index < binding.Requirements.Length; index++)
        {
            var original = CopyRequirement(binding.Requirements[index]);
            if (current is not null && current[index].IsRequired && (!original.IsRequired || original.PolicyRef != current[index].PolicyRef ||
                !original.ReasonCodes.SequenceEqual(current[index].ReasonCodes, StringComparer.Ordinal))) throw Denied();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (proposal, binding);
    }

    private static ApprovalRequirement CopyRequirement(ApprovalRequirement requirement)
    {
        if (requirement is null || requirement.ReasonCodes is null || requirement.ReasonCodes.Count > 32 ||
            requirement.ReasonCodes.Any(reason => reason is null || reason.Length > 128) || requirement.PolicyRef?.Length > 1024 ||
            (requirement.IsRequired && string.IsNullOrWhiteSpace(requirement.PolicyRef))) throw Denied();
        return requirement with { ReasonCodes = requirement.ReasonCodes.ToArray() };
    }

    private static string ScopeFingerprint(JobExecutionLeaseScope scope)
    {
        if (scope.Resources is null || scope.Operations is null || scope.Resources.Length > 128 || scope.Operations.Length is < 1 or > 128 ||
            scope.ProviderBinding is null || scope.ProviderBinding.Length > 65536 ||
            scope.Resources.Any(resource => resource is null || string.IsNullOrWhiteSpace(resource.ResourceId) || resource.ResourceId.Length > 1024) ||
            scope.Operations.Any(operation => operation is null || operation.ResourceId?.Length > 1024 || operation.WorkspaceOwnerId?.Length > 1024 ||
                operation.WorkspaceScopeId?.Length > 1024 || operation.ApprovalPolicyRef?.Length > 1024 || operation.ApprovalReasonCodes is null ||
                operation.ApprovalReasonCodes.Count > 32 || operation.ApprovalReasonCodes.Any(reason => reason is null || reason.Length > 128))) throw Denied();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(scope, ExecutionJobApprovalJsonContext.Default.JobExecutionLeaseScope);
        if (bytes.Length > MaximumPayloadBytes) throw Denied();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static UnauthorizedAccessException Denied() => new("The exact job approval is unavailable, changed or no longer current.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ExecutionJobApprovalBinding(string Format, string DescriptorId, OperationClass OperationClass,
    string IntentFingerprint, string ScopeFingerprint, DateTimeOffset ExpiresAt, ApprovalRequirement[] Requirements);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ExecutionJobApprovalBinding))]
[JsonSerializable(typeof(JobExecutionLeaseScope))]
internal sealed partial class ExecutionJobApprovalJsonContext : JsonSerializerContext;
