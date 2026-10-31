// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Deploy backend for one installed Compose project (honua-server#6811). The server process does
/// not receive the Docker socket. It asks the sidecar, and only after a different principal has
/// approved the operation. Cutover has downtime: the candidate is not staged beside live traffic.
/// </summary>
internal sealed partial class ComposeDeployBackend(
    IComposeExecutorClient client,
    IOptions<ComposeExecutorOptions> options,
    ILogger<ComposeDeployBackend> logger) : IDeployBackend
{
    internal const string AdapterBackendName = "honua-compose";

    private readonly ComposeExecutorOptions _options = options.Value;

    /// <inheritdoc />
    public string BackendName => AdapterBackendName;

    /// <inheritdoc />
    public DeployTargetKind TargetKind => DeployTargetKind.LocalCompose;

    /// <inheritdoc />
    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true,
            SupportsCancellation = false,
            SupportsTrafficShifting = false,
            // The candidate image applies its own migrations. This executor takes the backup and,
            // when the prior image cannot read the journal, restores it before that image starts.
            RequiresOutOfBandMigrations = false,
            SupportsProgressPolling = true,
            SupportsRevisionPinning = true,
            StagesCandidateWithoutTraffic = false
        });

    /// <inheritdoc />
    public async Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        var blocking = new List<string>();
        ClassifyParameters(spec.Parameters, blocking);
        if (!ComposeExecutorRules.IsImmutableImage(spec.DesiredRevision))
        {
            blocking.Add(ComposeExecutorCodes.ImageNotImmutable);
        }

        if (string.IsNullOrWhiteSpace(_options.SocketPath) || !ComposeExecutorRules.IsImmutableImage(_options.ReleaseImage))
        {
            blocking.Add(ComposeExecutorCodes.ExecutorUnconfigured);
        }

        var isConvergence = string.Equals(spec.DesiredRevision, _options.ReleaseImage, StringComparison.Ordinal);
        if (blocking.Count == 0 && isConvergence)
        {
            try
            {
                var status = await client.ExecuteAsync(
                        new ComposeExecutorRequest { Op = "status", RequestId = "plan" },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!status.Ok || !status.ReadbackComplete)
                {
                    blocking.Add(status.Code ?? ComposeExecutorCodes.ReadbackIncomplete);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                blocking.Add(ComposeExecutorCodes.ExecutorUnconfigured);
            }
        }

        return new DeployPlan
        {
            IsReadyToSubmit = blocking.Count == 0 && !spec.RequiresApproval,
            RequiresApproval = spec.RequiresApproval,
            RequiresOutOfBandMigrations = false,
            BlockingReasons = blocking
        };
    }

    /// <inheritdoc />
    public async Task<DeploySubmissionResult> StartAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var deploy = operation.Deploy ?? throw new InvalidOperationException("Compose start requires a deploy spec.");
        if (!ApprovedByOtherPrincipal(operation))
        {
            Log.ApprovalRefused(logger, operation.OperationId);
            return Submission(operation, WorkflowOperationStatus.Failed, ComposeExecutorCodes.ApprovalRequired);
        }

        var desired = deploy.DesiredRevision;
        var op = string.Equals(desired, _options.ReleaseImage, StringComparison.Ordinal) ? "converge" : "rollback";
        try
        {
            var priorRevision = deploy.CurrentRevision;
            if (op == "converge")
            {
                var prior = await client.ExecuteAsync(new ComposeExecutorRequest
                {
                    Op = "status",
                    RequestId = operation.OperationId
                }, cancellationToken).ConfigureAwait(false);
                if (!prior.Ok || !prior.ReadbackComplete || !ComposeExecutorRules.IsImmutableImage(prior.RunningImage))
                    return Submission(operation, WorkflowOperationStatus.Failed, ComposeExecutorCodes.ReadbackIncomplete);
                priorRevision = prior.RunningImage;
            }
            var response = await client.ExecuteAsync(
                    new ComposeExecutorRequest
                    {
                        Op = op,
                        Image = desired,
                        BackupId = deploy.PreMigrationBackupId,
                        RequestId = operation.OperationId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var ready = response.Ok
                && response.ReadbackComplete
                && string.Equals(response.RunningImage, desired, StringComparison.Ordinal);
            return new DeploySubmissionResult
            {
                Status = ready ? WorkflowOperationStatus.Reconciling : WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.OperationId,
                // Keep the prior revision. The workflow copies ObservedRevision onto CurrentRevision.
                ObservedRevision = priorRevision,
                Message = ready ? response.Message : response.Code ?? response.Message
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
        {
            return Submission(operation, WorkflowOperationStatus.Failed, ComposeExecutorCodes.ReadbackIncomplete);
        }
    }

    /// <inheritdoc />
    public Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
        => ObserveCoreAsync(operation, cancellationToken);

    /// <inheritdoc />
    public Task<DeployObservation> PromoteAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
        => ObserveAsync(operation, cancellationToken);

    /// <inheritdoc />
    public async Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var prior = operation.Deploy?.CurrentRevision;
        if (string.IsNullOrWhiteSpace(prior))
        {
            return Observation(operation, WorkflowOperationStatus.ManualInterventionRequired, null, "Rollback requires the prior revision.");
        }

        try
        {
            var response = await client.ExecuteAsync(
                    new ComposeExecutorRequest
                    {
                        Op = "rollback",
                        Image = prior,
                        BackupId = operation.Deploy?.PreMigrationBackupId,
                        RequestId = operation.OperationId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var rolled = response.Ok
                && response.ReadbackComplete
                && string.Equals(response.RunningImage, prior, StringComparison.Ordinal);
            return Observation(
                operation,
                rolled ? WorkflowOperationStatus.RolledBack : WorkflowOperationStatus.Failed,
                rolled ? prior : response.RunningImage,
                rolled ? response.Message : response.Code ?? response.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
        {
            return Observation(operation, WorkflowOperationStatus.Failed, null, ComposeExecutorCodes.ReadbackIncomplete);
        }
    }

    private async Task<DeployObservation> ObserveCoreAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var deploy = operation.Deploy;
        if (deploy == null)
        {
            return Observation(operation, WorkflowOperationStatus.Failed, null, ComposeExecutorCodes.ConfigUnsupported);
        }

        var rollback = operation.Status == WorkflowOperationStatus.RollbackRequested;
        if (rollback && string.IsNullOrWhiteSpace(deploy.CurrentRevision))
        {
            return Observation(operation, WorkflowOperationStatus.ManualInterventionRequired, null, "Rollback requires the prior revision.");
        }

        var expected = rollback ? deploy.CurrentRevision : deploy.DesiredRevision;
        try
        {
            var response = await client.ExecuteAsync(
                    new ComposeExecutorRequest
                    {
                        Op = "status",
                        Image = expected,
                        RequestId = operation.OperationId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var matches = response.Ok
                && response.ReadbackComplete
                && string.Equals(response.RunningImage, expected, StringComparison.Ordinal);
            var status = rollback
                ? matches ? WorkflowOperationStatus.RolledBack : WorkflowOperationStatus.Failed
                : matches ? WorkflowOperationStatus.Succeeded : WorkflowOperationStatus.Failed;
            return Observation(
                operation,
                status,
                matches ? expected : response.RunningImage,
                matches ? response.Message : response.Code ?? response.Message,
                rollbackRecommended: !matches && !rollback && ProtectionIsOpen(operation));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Observation(operation, WorkflowOperationStatus.Failed, null, ComposeExecutorCodes.ReadbackIncomplete,
                rollbackRecommended: !rollback && ProtectionIsOpen(operation));
        }
    }

    private static bool ProtectionIsOpen(WorkflowOperationRecord operation)
        => operation.Deploy?.Protection?.Phase is DeployProtectionPhase.Observing or DeployProtectionPhase.Protected;

    private static bool ApprovedByOtherPrincipal(WorkflowOperationRecord operation)
    {
        var approvedBy = operation.Audit.ApprovedBy;
        return !string.IsNullOrWhiteSpace(approvedBy)
            && !string.Equals(approvedBy, operation.Audit.RequestedBy, StringComparison.OrdinalIgnoreCase);
    }

    private static void ClassifyParameters(IReadOnlyDictionary<string, string> parameters, List<string> blocking)
    {
        foreach (var key in parameters.Keys)
        {
            if (string.Equals(key, DeployPromotionPolicy.PromotionGateParameterKey, StringComparison.Ordinal)
                || key.StartsWith("deployment.protection.", StringComparison.Ordinal))
            {
                continue;
            }

            if (key.Contains("command", StringComparison.OrdinalIgnoreCase)
                || key.Contains("project", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("compose.", StringComparison.Ordinal))
            {
                blocking.Add(ComposeExecutorCodes.ScopeViolation);
                continue;
            }

            blocking.Add(ComposeExecutorCodes.ConfigUnsupported);
        }
    }

    private static DeploySubmissionResult Submission(
        WorkflowOperationRecord operation,
        WorkflowOperationStatus status,
        string code)
        => new()
        {
            Status = status,
            ProviderOperationId = operation.OperationId,
            ObservedRevision = operation.Deploy?.CurrentRevision,
            Message = code
        };

    private static DeployObservation Observation(
        WorkflowOperationRecord operation,
        WorkflowOperationStatus status,
        string? revision,
        string? message,
        bool rollbackRecommended = false)
        => new()
        {
            Status = status,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = revision,
            Message = message,
            RollbackRecommended = rollbackRecommended
        };

    private static partial class Log
    {
        [LoggerMessage(EventId = 9532, Level = LogLevel.Warning, Message = "Compose deploy {OperationId} was refused because approval is missing.")]
        public static partial void ApprovalRefused(ILogger logger, string operationId);
    }
}
