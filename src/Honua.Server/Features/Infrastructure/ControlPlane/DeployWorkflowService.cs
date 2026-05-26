// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Server-side control-plane service for durable deploy workflow operations.
/// </summary>
internal sealed partial class DeployWorkflowService
{
    private static readonly Regex UnsafeOperationIdCharacters = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IDeployTargetRegistry _targetRegistry;
    private readonly IWorkflowOperationStore? _workflowStore;
    private readonly Dictionary<(string Backend, DeployTargetKind TargetKind), IDeployBackend> _backends;
    private readonly IOperatorApprovalEvaluator _approvalEvaluator;
    private readonly ILogger<DeployWorkflowService> _logger;

    public DeployWorkflowService(
        IDeployTargetRegistry targetRegistry,
        IEnumerable<IWorkflowOperationStore> workflowStores,
        IEnumerable<IDeployBackend> backends,
        IOperatorApprovalEvaluator approvalEvaluator,
        ILogger<DeployWorkflowService> logger)
    {
        _targetRegistry = targetRegistry;
        _workflowStore = workflowStores.FirstOrDefault();
        _backends = backends.ToDictionary(
            backend => (backend.BackendName, backend.TargetKind),
            backend => backend,
            EqualityComparer<(string Backend, DeployTargetKind TargetKind)>.Default);
        _approvalEvaluator = approvalEvaluator;
        _logger = logger;
    }

    public bool HasDurableStore => _workflowStore != null;

    public async Task<DeployWorkflowPlanResult?> PlanAsync(
        string targetId,
        string desiredRevision,
        string? currentRevision,
        IReadOnlyDictionary<string, string>? parameterOverrides,
        ClaimsPrincipal? principal = null,
        CancellationToken cancellationToken = default)
    {
        var target = await _targetRegistry.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (target == null)
        {
            return null;
        }

        var spec = BuildSpec(target, desiredRevision, currentRevision, parameterOverrides);
        var backend = ResolveBackend(target);
        var capabilities = backend == null
            ? null
            : await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

        // Bridge canonical approval evaluator with static RequiresApproval flag.
        // OR-combine: approval is required if either source says so.
        ApprovalRequirement? canonicalApproval = null;
        var requiresApproval = spec.RequiresApproval;
        if (principal != null)
        {
            canonicalApproval = _approvalEvaluator.Evaluate(principal, new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Deployment,
                Operation = OperatorOperation.Publish
            });
            var combinedApproval = spec.RequiresApproval || canonicalApproval.IsRequired;
            OperatorAuthorizationLog.DeployApprovalBridged(
                _logger, spec.RequiresApproval, canonicalApproval.IsRequired, combinedApproval);
            requiresApproval = combinedApproval;
        }

        var plan = backend == null
            ? new DeployPlan
            {
                IsReadyToSubmit = false,
                RequiresApproval = requiresApproval,
                RequiresOutOfBandMigrations = spec.RequiresOutOfBandMigrations,
                BlockingReasons =
                [
                    $"No deploy backend is registered for target '{target.TargetId}' ({target.Backend})."
                ],
                Warnings = Array.Empty<string>()
            }
            : await backend.PlanAsync(spec, cancellationToken).ConfigureAwait(false);

        // If the backend returned its own plan, override RequiresApproval with the bridged value
        // and recompute IsReadyToSubmit — backends derive it from spec.RequiresApproval which
        // does not yet include the canonical evaluator's decision.
        if (backend != null && requiresApproval != plan.RequiresApproval)
        {
            plan = plan with
            {
                RequiresApproval = requiresApproval,
                IsReadyToSubmit = plan.IsReadyToSubmit && !requiresApproval
            };
        }

        return new DeployWorkflowPlanResult(target, spec, plan, capabilities, canonicalApproval);
    }

    public async Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();
        return await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowOperationRecord?> CreateAsync(
        string targetId,
        string desiredRevision,
        string? currentRevision,
        string? requestedBy,
        string? reason,
        string? idempotencyKey,
        string? correlationId,
        OperationPriority priority,
        bool submitImmediately,
        IReadOnlyDictionary<string, string>? parameterOverrides,
        ClaimsPrincipal? principal = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();

        var planningResult = await PlanAsync(
                targetId,
                desiredRevision,
                currentRevision,
                parameterOverrides,
                principal,
                cancellationToken)
            .ConfigureAwait(false);

        if (planningResult == null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var operationId = CreateOperationId(idempotencyKey);
        var requestFingerprint = CreateRequestFingerprint(
            targetId,
            planningResult.Target.Backend,
            planningResult.Target.TargetKind,
            desiredRevision,
            currentRevision,
            priority,
            submitImmediately,
            parameterOverrides);

        var operation = new WorkflowOperationRecord
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = DeterminePersistedStatus(planningResult.Plan),
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = DeterminePersistedPhase(planningResult.Plan, submitImmediately),
            ErrorMessage = planningResult.Plan.BlockingReasons.Count > 0
                ? string.Join(" ", planningResult.Plan.BlockingReasons)
                : null,
            Warnings = planningResult.Plan.Warnings,
            BlockingReasons = planningResult.Plan.BlockingReasons,
            Audit = new OperationAuditInfo
            {
                RequestedBy = requestedBy,
                Reason = reason,
                IdempotencyKey = idempotencyKey,
                CorrelationId = correlationId,
                RequestFingerprint = requestFingerprint,
                ApprovalPolicyRef = planningResult.CanonicalApproval?.PolicyRef,
                ApprovalReasonCodes = planningResult.CanonicalApproval?.ReasonCodes ?? []
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = $"{planningResult.Target.Environment}:{planningResult.Target.TargetId}",
                RequiresExclusiveLease = true
            },
            Deploy = planningResult.Spec
        };

        var created = await _workflowStore!.TryCreateAsync(operation, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            var existing = await _workflowStore.GetAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                EnsureMatchingIdempotentRequest(existing, requestFingerprint);
                return existing;
            }

            throw new InvalidOperationException("Failed to durably record the deploy operation before backend submission.");
        }

        if (submitImmediately && planningResult.Plan.IsReadyToSubmit)
        {
            var backend = ResolveBackend(planningResult.Target);
            if (backend != null)
            {
                try
                {
                    var submission = await backend.StartAsync(operation, cancellationToken).ConfigureAwait(false);
                    operation = operation with
                    {
                        Status = submission.Status,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ProviderOperationId = submission.ProviderOperationId,
                        CurrentPhase = submission.Message ?? "Submitted to deploy backend",
                        ErrorMessage = null,
                        Deploy = operation.Deploy with
                        {
                            CurrentRevision = submission.ObservedRevision ?? operation.Deploy.CurrentRevision
                        }
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    operation = operation with
                    {
                        Status = WorkflowOperationStatus.Failed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CompletedAt = DateTimeOffset.UtcNow,
                        CurrentPhase = "Backend submission failed.",
                        ErrorMessage = ex.Message
                    };
                }

                await _workflowStore.SetAsync(operation, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return operation;
    }

    public async Task<WorkflowOperationRecord?> SubmitAsync(
        string operationId,
        string? requestedBy,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();

        var operation = await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation == null)
        {
            return null;
        }

        if (operation.Kind != WorkflowOperationKind.Deploy || operation.Deploy == null)
        {
            throw new InvalidOperationException("Only deploy workflow operations can be submitted.");
        }

        if (operation.Status is WorkflowOperationStatus.Submitted
            or WorkflowOperationStatus.Reconciling
            or WorkflowOperationStatus.RollbackRequested
            or WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired
            or WorkflowOperationStatus.Failed)
        {
            return operation;
        }

        if (operation.BlockingReasons.Count > 0)
        {
            throw new ResourceConflictException(
                $"Deploy operation '{operation.OperationId}' cannot be submitted: {string.Join(" ", operation.BlockingReasons)}");
        }

        var target = await _targetRegistry.GetAsync(operation.Deploy.TargetId, cancellationToken).ConfigureAwait(false);
        var backend = target == null ? null : ResolveBackend(target);
        if (backend == null)
        {
            throw new InvalidOperationException(
                $"No deploy backend is registered for target '{operation.Deploy.TargetId}' ({operation.Deploy.Backend}).");
        }

        try
        {
            var submission = await backend.StartAsync(operation, cancellationToken).ConfigureAwait(false);
            var updated = operation with
            {
                Status = submission.Status,
                UpdatedAt = DateTimeOffset.UtcNow,
                ProviderOperationId = submission.ProviderOperationId,
                CurrentPhase = submission.Message ?? "Submitted to deploy backend",
                ErrorMessage = null,
                Audit = MergeSubmissionAudit(operation.Audit, requestedBy, reason),
                Deploy = operation.Deploy with
                {
                    CurrentRevision = submission.ObservedRevision ?? operation.Deploy.CurrentRevision
                }
            };

            await _workflowStore.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = operation with
            {
                Status = WorkflowOperationStatus.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Backend submission failed.",
                ErrorMessage = ex.Message,
                Audit = MergeSubmissionAudit(operation.Audit, requestedBy, reason)
            };

            await _workflowStore.SetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false);
            return failed;
        }
    }

    public async Task<WorkflowOperationRecord?> RequestRollbackAsync(
        string operationId,
        string? requestedBy,
        string? reason,
        bool? approvedMetadataReleaseIsDataAffecting = null,
        bool? approvedMetadataReleaseRequiresApproval = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();

        var operation = await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation == null)
        {
            return null;
        }

        if (operation.Kind == WorkflowOperationKind.MetadataRelease)
        {
            EnsureMetadataReleaseRollbackApprovalStillMatches(
                operation,
                approvedMetadataReleaseIsDataAffecting,
                approvedMetadataReleaseRequiresApproval);
            return await RequestMetadataReleaseRollbackAsync(operation, requestedBy, reason, cancellationToken)
                .ConfigureAwait(false);
        }

        if (operation.Kind != WorkflowOperationKind.Deploy || operation.Deploy == null)
        {
            throw new InvalidOperationException("Rollback is only supported for deploy workflow operations.");
        }

        if (operation.Status is WorkflowOperationStatus.RolledBack or WorkflowOperationStatus.Failed)
        {
            return operation;
        }

        WorkflowOperationRecord updated;
        if (operation.Status is WorkflowOperationStatus.Planned or WorkflowOperationStatus.AwaitingApproval)
        {
            updated = operation with
            {
                Status = WorkflowOperationStatus.RolledBack,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Rolled back before provider submission.",
                Audit = operation.Audit with
                {
                    RequestedBy = requestedBy ?? operation.Audit.RequestedBy,
                    Reason = reason ?? operation.Audit.Reason
                }
            };
        }
        else
        {
            var target = await _targetRegistry.GetAsync(operation.Deploy.TargetId, cancellationToken).ConfigureAwait(false);
            var backend = target == null ? null : ResolveBackend(target);
            if (backend == null)
            {
                updated = operation with
                {
                    Status = WorkflowOperationStatus.ManualInterventionRequired,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Rollback requested but no backend is registered for this target.",
                    ErrorMessage = "Manual rollback is required because no deploy backend is registered for this target.",
                    Audit = operation.Audit with
                    {
                        RequestedBy = requestedBy ?? operation.Audit.RequestedBy,
                        Reason = reason ?? operation.Audit.Reason
                    }
                };
            }
            else
            {
                var observation = await backend.RollbackAsync(operation, cancellationToken).ConfigureAwait(false);
                updated = operation with
                {
                    Status = observation.Status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = observation.Status is WorkflowOperationStatus.RolledBack
                        or WorkflowOperationStatus.Failed
                        or WorkflowOperationStatus.ManualInterventionRequired
                        ? DateTimeOffset.UtcNow
                        : null,
                    ProviderOperationId = observation.ProviderOperationId ?? operation.ProviderOperationId,
                    CurrentPhase = observation.Message ?? "Rollback requested",
                    ErrorMessage = observation.Status == WorkflowOperationStatus.Failed ? observation.Message : null,
                    Audit = operation.Audit with
                    {
                        RequestedBy = requestedBy ?? operation.Audit.RequestedBy,
                        Reason = reason ?? operation.Audit.Reason
                    }
                };
            }
        }

        await _workflowStore.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<WorkflowOperationRecord> RequestMetadataReleaseRollbackAsync(
        WorkflowOperationRecord operation,
        string? requestedBy,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (operation.MetadataRelease == null)
        {
            throw new InvalidOperationException("Metadata release rollback requires metadata release context.");
        }

        if (operation.Status is WorkflowOperationStatus.RollbackRequested or WorkflowOperationStatus.RolledBack)
        {
            return operation;
        }

        var now = DateTimeOffset.UtcNow;
        var rollbackPlan = operation.MetadataRelease.RollbackPlan;
        var updatedMetadataRelease = operation.MetadataRelease with
        {
            CurrentStage = MetadataReleaseStage.RollbackRequested
        };
        var updated = operation with
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            UpdatedAt = now,
            CompletedAt = null,
            CurrentPhase = rollbackPlan == null
                ? "Metadata release rollback requested; rollback class was not precomputed."
                : $"Metadata release rollback requested using {rollbackPlan.Class} plan.",
            ErrorMessage = null,
            MetadataRelease = updatedMetadataRelease,
            Audit = operation.Audit with
            {
                RequestedBy = requestedBy ?? operation.Audit.RequestedBy,
                Reason = reason ?? operation.Audit.Reason
            }
        };

        Log.MetadataReleaseRollbackRequested(
            _logger,
            operation.OperationId,
            operation.MetadataRelease.PackageId,
            rollbackPlan?.Class.ToString() ?? "Unspecified",
            rollbackPlan?.IsDataAffecting ?? true);

        await _workflowStore!.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static void EnsureMetadataReleaseRollbackApprovalStillMatches(
        WorkflowOperationRecord operation,
        bool? approvedIsDataAffecting,
        bool? approvedRequiresApproval)
    {
        if (!approvedIsDataAffecting.HasValue && !approvedRequiresApproval.HasValue)
        {
            return;
        }

        var currentPlan = operation.MetadataRelease?.RollbackPlan;
        var currentIsDataAffecting = currentPlan?.IsDataAffecting ?? true;
        var currentRequiresApproval = currentIsDataAffecting || currentPlan?.RequiresExplicitApproval == true;
        if ((!approvedIsDataAffecting.HasValue || currentIsDataAffecting == approvedIsDataAffecting.Value) &&
            (!approvedRequiresApproval.HasValue || currentRequiresApproval == approvedRequiresApproval.Value))
        {
            return;
        }

        throw new ResourceConflictException(
            "Metadata release rollback plan changed before rollback could be requested. Re-read the operation and retry approval.");
    }

    private static WorkflowOperationStatus DeterminePersistedStatus(DeployPlan plan)
    {
        if (plan.RequiresApproval)
        {
            return WorkflowOperationStatus.AwaitingApproval;
        }

        return WorkflowOperationStatus.Planned;
    }

    private static string DeterminePersistedPhase(DeployPlan plan, bool submitImmediately)
    {
        if (plan.RequiresApproval)
        {
            return "Awaiting approval before backend submission.";
        }

        if (submitImmediately && plan.IsReadyToSubmit)
        {
            return "Submitting to deploy backend.";
        }

        if (plan.BlockingReasons.Count > 0)
        {
            return "Blocked pending operator action.";
        }

        return "Planned and ready for submission.";
    }

    private static string CreateOperationId(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return $"deploy-{Guid.NewGuid():N}";
        }

        var normalizedKey = idempotencyKey.Trim();
        var slug = UnsafeOperationIdCharacters
            .Replace(normalizedKey.ToLowerInvariant(), "-")
            .Trim('-');

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "key";
        }

        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey));
        var hash = Convert.ToHexString(hashBytes.AsSpan(0, 6)).ToLowerInvariant();
        return $"deploy-{slug}-{hash}";
    }

    private static string CreateRequestFingerprint(
        string targetId,
        string backend,
        DeployTargetKind targetKind,
        string desiredRevision,
        string? currentRevision,
        OperationPriority priority,
        bool submitImmediately,
        IReadOnlyDictionary<string, string>? parameterOverrides)
    {
        var normalizedParameters = parameterOverrides == null
            ? Array.Empty<DeployRequestFingerprintParameter>()
            : parameterOverrides
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new DeployRequestFingerprintParameter
                {
                    Key = pair.Key,
                    Value = pair.Value
                })
                .ToArray();

        var payload = JsonSerializer.Serialize(
            new DeployRequestFingerprintPayload
            {
                TargetId = targetId.Trim(),
                // Backend + TargetKind participate in the fingerprint: two targets
                // may share a TargetId string but route to different backends (e.g.
                // kubernetes vs. cloud-run). Excluding them lets a replay for one
                // backend be accepted as idempotent for another.
                Backend = backend?.Trim(),
                TargetKind = targetKind.ToString(),
                DesiredRevision = desiredRevision.Trim(),
                CurrentRevision = currentRevision?.Trim(),
                Priority = priority.ToString(),
                SubmitImmediately = submitImmediately,
                Parameters = normalizedParameters
            },
            DeployControlJsonContext.Default.DeployRequestFingerprintPayload);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static void EnsureMatchingIdempotentRequest(WorkflowOperationRecord existing, string requestFingerprint)
    {
        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint) &&
            string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new ResourceConflictException(
            $"Idempotency key '{existing.Audit.IdempotencyKey}' is already associated with a different deploy request.");
    }

    private static OperationAuditInfo MergeSubmissionAudit(OperationAuditInfo audit, string? requestedBy, string? reason)
        => audit with
        {
            RequestedBy = audit.RequestedBy ?? requestedBy,
            Reason = string.IsNullOrWhiteSpace(reason)
                ? audit.Reason
                : string.IsNullOrWhiteSpace(audit.Reason)
                    ? reason
                    : $"{audit.Reason} | submit: {reason}"
        };

    private static partial class Log
    {
        [LoggerMessage(9100, LogLevel.Information, "Metadata release rollback requested for operation {OperationId}, package {PackageId}, class {RollbackClass}, data affecting {IsDataAffecting}")]
        public static partial void MetadataReleaseRollbackRequested(
            ILogger logger,
            string operationId,
            string packageId,
            string rollbackClass,
            bool isDataAffecting);
    }

    private static DeployOperationSpec BuildSpec(
        DeployTargetDefinition target,
        string desiredRevision,
        string? currentRevision,
        IReadOnlyDictionary<string, string>? parameterOverrides)
    {
        var mergedParameters = new Dictionary<string, string>(target.Parameters, StringComparer.Ordinal);
        if (parameterOverrides != null)
        {
            foreach (var (key, value) in parameterOverrides)
            {
                mergedParameters[key] = value;
            }
        }

        var resolvedCurrentRevision = currentRevision;
        if (string.IsNullOrWhiteSpace(resolvedCurrentRevision))
        {
            if (mergedParameters.TryGetValue("deployment.current_revision", out var deploymentCurrentRevision) &&
                !string.IsNullOrWhiteSpace(deploymentCurrentRevision))
            {
                resolvedCurrentRevision = deploymentCurrentRevision;
            }
            else if (mergedParameters.TryGetValue("lambda.current_version", out var lambdaCurrentVersion) &&
                     !string.IsNullOrWhiteSpace(lambdaCurrentVersion))
            {
                resolvedCurrentRevision = lambdaCurrentVersion;
            }
            else if (mergedParameters.TryGetValue("aws.lambda.alias_current_version", out var awsLambdaCurrentVersion) &&
                     !string.IsNullOrWhiteSpace(awsLambdaCurrentVersion))
            {
                resolvedCurrentRevision = awsLambdaCurrentVersion;
            }
        }

        return new DeployOperationSpec
        {
            TargetId = target.TargetId,
            TargetKind = target.TargetKind,
            Backend = target.Backend,
            Environment = target.Environment,
            TargetName = target.TargetName,
            ArtifactReference = target.ArtifactReference,
            RuntimeProfile = target.RuntimeProfile,
            CurrentRevision = resolvedCurrentRevision,
            DesiredRevision = desiredRevision,
            RequiresOutOfBandMigrations = target.RequiresOutOfBandMigrations,
            RequiresApproval = target.RequiresApproval,
            Parameters = mergedParameters
        };
    }

    private IDeployBackend? ResolveBackend(DeployTargetDefinition target)
        => _backends.GetValueOrDefault((target.Backend, target.TargetKind));

    private void EnsureDurableStoreConfigured()
    {
        if (_workflowStore == null)
        {
            throw new InvalidOperationException("Deploy workflow operations require Redis-backed durable storage.");
        }
    }
}

internal sealed record DeployRequestFingerprintPayload
{
    public required string TargetId { get; init; }
    public string? Backend { get; init; }
    public string? TargetKind { get; init; }
    public required string DesiredRevision { get; init; }
    public string? CurrentRevision { get; init; }
    public required string Priority { get; init; }
    public required bool SubmitImmediately { get; init; }
    public required DeployRequestFingerprintParameter[] Parameters { get; init; }
}

internal sealed record DeployRequestFingerprintParameter
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// Result of resolving a deploy target and planning a workflow operation.
/// </summary>
internal sealed record DeployWorkflowPlanResult(
    DeployTargetDefinition Target,
    DeployOperationSpec Spec,
    DeployPlan Plan,
    DeployBackendCapabilities? Capabilities,
    ApprovalRequirement? CanonicalApproval = null);
