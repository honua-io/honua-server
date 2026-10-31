// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Guardrails.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>Persists approved restore work in the shared workflow store before any mutation.</summary>
internal sealed class DurableRestoreQueue(IWorkflowOperationStore store)
{
    /// <summary>Creates or returns the same durable work item for an identical approved replay.</summary>
    public async Task<string> EnqueueAsync(OperationGatewayRequest request, string payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RequestedBy) || string.IsNullOrWhiteSpace(request.ApprovedBy)
            || string.IsNullOrWhiteSpace(request.ApprovedProposalId)
            || string.Equals(request.RequestedBy, request.ApprovedBy, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A separate principal must approve the restore.");
        }

        var material = !string.IsNullOrWhiteSpace(request.IdempotencyKey) ? request.IdempotencyKey! : request.ApprovedProposalId!;
        var id = CreateId(material);
        var now = DateTimeOffset.UtcNow;
        var record = new WorkflowOperationRecord
        {
            OperationId = id,
            Kind = WorkflowOperationKind.PreMigrationRestore,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Approved restore queued.",
            Audit = new OperationAuditInfo
            {
                RequestedBy = request.RequestedBy,
                ProposalId = request.ApprovedProposalId,
                IdempotencyKey = id,
                CorrelationId = request.CorrelationId,
                Reason = request.Reason,
                TenantId = request.TenantId
            },
            PreMigrationRestore = new PreMigrationRestoreContext
            {
                Payload = payload,
                Phase = "queued",
                ApprovedBy = request.ApprovedBy
            }
        };
        if (await store.TryCreateAsync(record, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return id;
        }

        var existing = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var existingContext = existing?.PreMigrationRestore;
        if (existing?.Kind != WorkflowOperationKind.PreMigrationRestore
            || existingContext is null || existingContext.Payload != payload
            || existingContext.ApprovedBy != request.ApprovedBy
            || existing.Audit.RequestedBy != request.RequestedBy
            || existing.Audit.ProposalId != request.ApprovedProposalId)
        {
            throw new InvalidOperationException("The restore idempotency key belongs to a different request.");
        }
        return id;
    }

    /// <summary>Returns the stable checkpoint identity for a proposal or its idempotency key.</summary>
    internal static string CreateId(string material)
        => "restore-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32].ToLowerInvariant();

    /// <summary>Returns the durable restore state, or null for another kind of operation.</summary>
    public async Task<OperationBackendStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
    {
        var operation = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (operation?.Kind != WorkflowOperationKind.PreMigrationRestore) return null;
        return new OperationBackendStatus
        {
            State = operation.Status switch
            {
                WorkflowOperationStatus.Succeeded => OperationBackendState.Succeeded,
                WorkflowOperationStatus.Failed => OperationBackendState.Failed,
                WorkflowOperationStatus.ManualInterventionRequired => OperationBackendState.Indeterminate,
                WorkflowOperationStatus.Submitted => OperationBackendState.Queued,
                _ => OperationBackendState.Running
            },
            Reason = operation.ErrorMessage
        };
    }
}

/// <summary>Runs leased restore work and resumes observation after a serving host is replaced.</summary>
internal sealed partial class DurableRestoreWorker(
    IWorkflowOperationStore store,
    IOperationProposalStore proposals,
    PreMigrationRestoreCoordinator coordinator,
    DeployOperationExecutor deploy,
    DurableRestoreCompletionVerifier verifier,
    IPreMigrationRestoreAudit audit,
    ILogger<DurableRestoreWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private const string CellOwnerId = RestoreDeployAdmission.CellOwnerId;
    private static readonly TimeSpan OwnerRetention = TimeSpan.FromDays(365);
    private readonly string _owner = $"restore:{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>Reconciles one checkpoint. Only the lease owner may advance it.</summary>
    internal async Task ReconcileAsync(string id, CancellationToken cancellationToken)
    {
        if (!await store.TryAcquireLeaseAsync(id, _owner, Lease, cancellationToken).ConfigureAwait(false)) return;
        using var lostLease = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewAsync(id, lostLease);
        try
        {
            var record = await store.GetAsync(id, lostLease.Token).ConfigureAwait(false);
            if (record?.Kind != WorkflowOperationKind.PreMigrationRestore || record.PreMigrationRestore is null
                || record.Status is WorkflowOperationStatus.Succeeded or WorkflowOperationStatus.Failed or WorkflowOperationStatus.ManualInterventionRequired) return;
            var context = record.PreMigrationRestore;
            if (context.Phase == "acting")
            {
                await FailAsync(record, PreMigrationRestoreCodes.RestoreIncomplete, lostLease.Token).ConfigureAwait(false);
                return;
            }
            if (context.Phase == "awaiting-compatible-deploy")
            {
                await CompleteCompatibleAsync(record, lostLease.Token).ConfigureAwait(false);
                return;
            }
            if (context.Phase == "awaiting-deploy")
            {
                await CompleteAsync(record, lostLease.Token).ConfigureAwait(false);
                return;
            }
            if (context.Phase != "queued")
            {
                await FailAsync(record, PreMigrationRestoreCodes.RestoreIncomplete, lostLease.Token).ConfigureAwait(false);
                return;
            }
            // Approval and the operation id must be durable before the side effects begin.
            var proposal = string.IsNullOrWhiteSpace(record.Audit.ProposalId)
                ? null : await proposals.GetAsync(record.Audit.ProposalId, lostLease.Token).ConfigureAwait(false);
            if (proposal is null || proposal.Status is not (OperationProposalStatus.Submitted or OperationProposalStatus.Reconciling)
                || !string.Equals(proposal.ExecutionOperationId, record.OperationId, StringComparison.Ordinal)
                || !string.Equals(proposal.ResolvedBy, context.ApprovedBy, StringComparison.Ordinal))
            {
                if (DateTimeOffset.UtcNow - record.CreatedAt > TimeSpan.FromMinutes(5))
                    await FailAsync(record, PreMigrationRestoreCodes.RestoreIncomplete, lostLease.Token).ConfigureAwait(false);
                return;
            }
            var payload = DeployExecutionPayload.Parse(context.Payload);
            if (payload is null || !payload.Rollback)
            {
                await FailAsync(record, PreMigrationRestoreCodes.RestoreIncomplete, lostLease.Token).ConfigureAwait(false);
                return;
            }
            if (!await ClaimCellAsync(id, lostLease.Token).ConfigureAwait(false)) return;
            var acting = record with
            {
                Status = WorkflowOperationStatus.Reconciling,
                UpdatedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Restoring and verifying the backup.",
                PreMigrationRestore = context with { Phase = "acting" }
            };
            if (!await store.TrySetAsync(acting, cancellationToken: lostLease.Token).ConfigureAwait(false)) return;
            acting = await store.GetAsync(id, lostLease.Token).ConfigureAwait(false) ?? throw new InvalidOperationException("Restore checkpoint disappeared.");
            var request = new OperationGatewayRequest
            {
                Kind = OperationClass.Deploy,
                RequestedBy = acting.Audit.RequestedBy,
                ApprovedBy = context.ApprovedBy,
                IdempotencyKey = acting.OperationId,
                CorrelationId = acting.Audit.CorrelationId,
                Reason = acting.Audit.Reason,
                TenantId = acting.Audit.TenantId
            };
            var startedAudit = await audit.AppendAsync(new PreMigrationRestoreAuditLine
            {
                Time = DateTimeOffset.UtcNow,
                Op = "rollback",
                RequestId = acting.Audit.CorrelationId,
                Image = payload.DesiredRevision,
                BackupId = payload.BackupId,
                Ok = false,
                Code = "restore-in-progress",
                OperationId = id
            }, lostLease.Token).ConfigureAwait(false);
            if (!startedAudit)
            {
                await FailAsync(acting, PreMigrationRestoreCodes.AuditFailed, lostLease.Token).ConfigureAwait(false);
                return;
            }
            var starter = new CheckpointStarter(store, deploy, verifier, acting);
            var completion = await coordinator.ExecuteAsync(payload, request, starter, lostLease.Token).ConfigureAwait(false);
            var latest = await store.GetAsync(id, lostLease.Token).ConfigureAwait(false);
            if (latest?.PreMigrationRestore?.Phase is "awaiting-deploy" or "awaiting-compatible-deploy") return;
            if (latest is null) return;
            if (ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion))
                await FinishAsync(latest, WorkflowOperationStatus.Succeeded, null, lostLease.Token).ConfigureAwait(false);
            else
                await FailAsync(latest, completion.Code ?? PreMigrationRestoreCodes.RestoreIncomplete, lostLease.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lostLease.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ReconcileFailed(logger, ex.GetType().Name);
            var record = await store.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (record is not null) await FailAsync(record, PreMigrationRestoreCodes.ProviderFailed, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await lostLease.CancelAsync().ConfigureAwait(false);
            try { await renewal.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await store.ReleaseLeaseAsync(id, _owner, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // A short Redis lease serializes updates to the durable owner record. The owner survives
    // process replacement and remains held until the full deploy and readback have succeeded.
    // Failed or missing owners require operator intervention rather than another restore.
    internal async Task<bool> ClaimCellAsync(string id, CancellationToken cancellationToken)
    {
        if (!await store.TryAcquireLeaseAsync(CellOwnerId, _owner, Lease, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            var owner = await store.GetAsync(CellOwnerId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(owner?.CurrentPhase, id, StringComparison.Ordinal))
            {
                var activeDeploys = await store.ListActiveAsync(WorkflowOperationKind.Deploy, cancellationToken).ConfigureAwait(false);
                if (activeDeploys.Any(operation => operation.Status is WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.RollbackRequested)) return false;
            }
            if (owner is null)
            {
                var now = DateTimeOffset.UtcNow;
                return await store.TryCreateAsync(new WorkflowOperationRecord
                {
                    OperationId = CellOwnerId,
                    Kind = WorkflowOperationKind.PreMigrationRestore,
                    Status = WorkflowOperationStatus.Succeeded,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CurrentPhase = id
                }, OwnerRetention, cancellationToken).ConfigureAwait(false);
            }
            if (string.Equals(owner.CurrentPhase, id, StringComparison.Ordinal)) return true;
            if (!string.IsNullOrEmpty(owner.CurrentPhase))
            {
                var previous = await store.GetAsync(owner.CurrentPhase, cancellationToken).ConfigureAwait(false);
                if (previous?.Status != WorkflowOperationStatus.Succeeded) return false;
            }
            return await store.TrySetAsync(owner with { CurrentPhase = id, UpdatedAt = DateTimeOffset.UtcNow },
                OwnerRetention, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await store.ReleaseLeaseAsync(CellOwnerId, _owner, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task CompleteCompatibleAsync(WorkflowOperationRecord record, CancellationToken cancellationToken)
    {
        var checkpoint = record.PreMigrationRestore!;
        var payload = DeployExecutionPayload.Parse(checkpoint.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(checkpoint.DeployOperationId))
        {
            await FailAsync(record, PreMigrationRestoreCodes.RestoreIncomplete, cancellationToken).ConfigureAwait(false);
            return;
        }
        var workflow = await deploy.GetWorkflowAsync(checkpoint.DeployOperationId, cancellationToken).ConfigureAwait(false);
        if (workflow is null || workflow.Status is not (WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.Succeeded))
        {
            await FailAsync(record, PreMigrationRestoreCodes.RestoreIncomplete, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (workflow.Status != WorkflowOperationStatus.Succeeded) return;
        var decision = await coordinator.AssessAsync(payload, cancellationToken).ConfigureAwait(false);
        var code = decision.Kind != RestoreDecisionKind.NotRequired
            ? PreMigrationRestoreCodes.ReadbackIncomplete
            : await verifier.VerifyCompatibleAsync(record, payload, cancellationToken).ConfigureAwait(false);
        await FinishAsync(record, code is null ? WorkflowOperationStatus.Succeeded : WorkflowOperationStatus.Failed,
            code, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteAsync(WorkflowOperationRecord record, CancellationToken cancellationToken)
    {
        var checkpoint = record.PreMigrationRestore!;
        var payload = DeployExecutionPayload.Parse(checkpoint.Payload);
        if (payload is null || checkpoint.ExpectedSchema is null || string.IsNullOrWhiteSpace(checkpoint.DeployOperationId))
        {
            await FailAsync(record, PreMigrationRestoreCodes.RestoreIncomplete, cancellationToken).ConfigureAwait(false);
            return;
        }
        var workflow = await deploy.GetWorkflowAsync(checkpoint.DeployOperationId, cancellationToken).ConfigureAwait(false);
        if (workflow is null || workflow.Status is not (WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.Succeeded))
        {
            // A crash before actual submission cannot be retried blindly.
            await FailAsync(record, PreMigrationRestoreCodes.RestoreIncomplete, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Succeeded is emitted only after the workflow's entire post-activation protection window.
        if (workflow.Status != WorkflowOperationStatus.Succeeded) return;
        var code = await verifier.VerifyAsync(record, payload, checkpoint.ExpectedSchema.Value, cancellationToken).ConfigureAwait(false);
        await FinishAsync(record, code is null ? WorkflowOperationStatus.Succeeded : WorkflowOperationStatus.Failed,
            code, cancellationToken).ConfigureAwait(false);
    }

    private Task FailAsync(WorkflowOperationRecord record, string code, CancellationToken cancellationToken)
        => FinishAsync(record, WorkflowOperationStatus.Failed, code, cancellationToken);

    private async Task FinishAsync(WorkflowOperationRecord record, WorkflowOperationStatus status, string? code, CancellationToken cancellationToken)
    {
        var written = await store.TrySetAsync(record with
        {
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = status == WorkflowOperationStatus.Succeeded ? "Restore and protection window completed." : "Restore requires operator attention.",
            ErrorMessage = code
        }, status == WorkflowOperationStatus.Succeeded ? OwnerRetention : null, cancellationToken).ConfigureAwait(false);
        if (written && status == WorkflowOperationStatus.Succeeded)
            await ReleaseCellAsync(record.OperationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReleaseCellAsync(string id, CancellationToken cancellationToken)
    {
        if (!await store.TryAcquireLeaseAsync(CellOwnerId, _owner, Lease, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var owner = await store.GetAsync(CellOwnerId, cancellationToken).ConfigureAwait(false);
            if (owner is not null && string.Equals(owner.CurrentPhase, id, StringComparison.Ordinal))
                await store.TrySetAsync(owner with { CurrentPhase = null, UpdatedAt = DateTimeOffset.UtcNow }, OwnerRetention, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await store.ReleaseLeaseAsync(CellOwnerId, _owner, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RenewAsync(string id, CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token).ConfigureAwait(false);
                if (!await store.RenewLeaseAsync(id, _owner, Lease, cancellation.Token).ConfigureAwait(false))
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.LeaseFailed(logger, ex.GetType().Name);
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = await store.ListActiveAsync(WorkflowOperationKind.PreMigrationRestore, stoppingToken).ConfigureAwait(false);
                foreach (var item in items) await ReconcileAsync(item.OperationId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.ScanFailed(logger, ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9580, LogLevel.Warning, "Restore reconciliation failed ({ExceptionType}).")]
        internal static partial void ReconcileFailed(ILogger logger, string exceptionType);

        [LoggerMessage(9581, LogLevel.Warning, "Restore scan failed ({ExceptionType}).")]
        internal static partial void ScanFailed(ILogger logger, string exceptionType);

        [LoggerMessage(9582, LogLevel.Warning, "Restore lease renewal failed ({ExceptionType}).")]
        internal static partial void LeaseFailed(ILogger logger, string exceptionType);
    }

    private sealed class CheckpointStarter(IWorkflowOperationStore store, DeployOperationExecutor deploy, DurableRestoreCompletionVerifier verifier, WorkflowOperationRecord record) : IDurablePriorImageStarter
    {
        public async Task<string?> StartAsync(OperationGatewayRequest request, string? executionPayload, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(executionPayload)) return null;
            var hostHash = await verifier.CaptureHostHashAsync(cancellationToken).ConfigureAwait(false);
            if (hostHash is null) return null;
            var workflowId = DeployWorkflowService.CreateOperationId(request.IdempotencyKey);
            var checkpoint = record with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Waiting for compatible prior image and its protection window.",
                PreMigrationRestore = record.PreMigrationRestore! with
                {
                    Phase = "awaiting-compatible-deploy",
                    DeployOperationId = workflowId,
                    RestoredHostHash = hostHash
                }
            };
            if (!await store.TrySetAsync(checkpoint, cancellationToken: cancellationToken).ConfigureAwait(false)) return null;
            var actual = await deploy.ExecuteRestoreAsync(request, executionPayload, record.OperationId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, workflowId, StringComparison.Ordinal)) throw new InvalidOperationException("Deploy operation did not match the saved checkpoint.");
            return workflowId;
        }

        public async Task<string?> StartRestoredAsync(OperationGatewayRequest request, string payload, int expectedSchema, string restoredHostHash, CancellationToken cancellationToken)
        {
            var workflowId = DeployWorkflowService.CreateOperationId(request.IdempotencyKey);
            var checkpoint = record with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Waiting for prior image and its protection window.",
                PreMigrationRestore = record.PreMigrationRestore! with
                {
                    Phase = "awaiting-deploy",
                    DeployOperationId = workflowId,
                    ExpectedSchema = expectedSchema,
                    RestoredHostHash = restoredHostHash
                }
            };
            if (!await store.TrySetAsync(checkpoint, cancellationToken: cancellationToken).ConfigureAwait(false)) return null;
            var actual = await deploy.ExecuteRestoreAsync(request, payload, record.OperationId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, workflowId, StringComparison.Ordinal)) throw new InvalidOperationException("Deploy operation did not match the saved checkpoint.");
            return workflowId;
        }
    }
}

/// <summary>Verifies the restored schema and customer readback before recording success.</summary>
internal sealed class DurableRestoreCompletionVerifier(
    IRestoredSchemaProbe schemaProbe,
    IRevisionSchemaCatalog revisions,
    IServingCustomerProbe customerProbe,
    IPreMigrationRestoreAudit audit,
    IConnectionSecretRetarget secrets,
    IOptions<RestoreCellOptions> options)
{
    /// <summary>Returns null only after the checks and durable audit succeed.</summary>
    public async Task<string?> VerifyAsync(WorkflowOperationRecord record, DeployExecutionPayload payload, int expectedSchema, CancellationToken cancellationToken)
    {
        var expectedHostHash = record.PreMigrationRestore?.RestoredHostHash;
        var host = await secrets.ReadHostAsync(options.Value.ConnectionSecretId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(host) || !HostMatches(host, expectedHostHash))
            return PreMigrationRestoreCodes.ReadbackIncomplete;
        var observed = await schemaProbe.ProbeAsync(host, cancellationToken).ConfigureAwait(false);
        var max = observed.Applied.Count == 0 ? (int?)null : observed.Applied.Max(item => item.Number);
        var priorMax = revisions.MaxSchema(payload.DesiredRevision) ?? -1;
        if (!observed.Known || !(max >= expectedSchema && max <= priorMax && observed.Applied.All(item => item.Number <= priorMax))
            || !await customerProbe.SucceededAsync(cancellationToken).ConfigureAwait(false))
            return PreMigrationRestoreCodes.ReadbackIncomplete;
        var confirmedHost = await secrets.ReadHostAsync(options.Value.ConnectionSecretId, cancellationToken).ConfigureAwait(false);
        if (confirmedHost is null || !HostMatches(confirmedHost, expectedHostHash))
            return PreMigrationRestoreCodes.ReadbackIncomplete;
        var written = await audit.AppendAsync(new PreMigrationRestoreAuditLine
        {
            Time = DateTimeOffset.UtcNow,
            Op = "rollback",
            RequestId = record.Audit.CorrelationId,
            Image = payload.DesiredRevision,
            BackupId = payload.BackupId,
            Ok = true,
            SchemaVersion = expectedSchema,
            ReadbackComplete = true,
            OperationId = record.OperationId
        }, cancellationToken).ConfigureAwait(false);
        return written ? null : PreMigrationRestoreCodes.AuditFailed;
    }

    /// <summary>Checks customer readback and records an already-compatible rollback.</summary>
    public async Task<string?> VerifyCompatibleAsync(WorkflowOperationRecord record, DeployExecutionPayload payload, CancellationToken cancellationToken)
    {
        var expected = record.PreMigrationRestore?.RestoredHostHash;
        var host = await secrets.ReadHostAsync(options.Value.ConnectionSecretId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(host) || !HostMatches(host, expected)
            || !await customerProbe.SucceededAsync(cancellationToken).ConfigureAwait(false)) return PreMigrationRestoreCodes.ReadbackIncomplete;
        var confirmed = await secrets.ReadHostAsync(options.Value.ConnectionSecretId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(confirmed) || !HostMatches(confirmed, expected)) return PreMigrationRestoreCodes.ReadbackIncomplete;
        var written = await audit.AppendAsync(new PreMigrationRestoreAuditLine
        {
            Time = DateTimeOffset.UtcNow,
            Op = "rollback",
            RequestId = record.Audit.CorrelationId,
            Image = payload.DesiredRevision,
            BackupId = payload.BackupId,
            Ok = true,
            ReadbackComplete = true,
            OperationId = record.OperationId
        }, cancellationToken).ConfigureAwait(false);
        return written ? null : PreMigrationRestoreCodes.AuditFailed;
    }

    /// <summary>Captures the current database binding without persisting its host name.</summary>
    public async Task<string?> CaptureHostHashAsync(CancellationToken cancellationToken)
    {
        var host = await secrets.ReadHostAsync(options.Value.ConnectionSecretId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(host) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host))).ToLowerInvariant();
    }

    private static bool HostMatches(string host, string? expected)
        => string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host))).ToLowerInvariant(), expected, StringComparison.Ordinal);
}
