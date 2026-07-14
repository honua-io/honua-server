// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Live post-action verifier for the only currently auto-safe action: bounded alert
/// dead-letter redrive. It refreshes the dispatch health snapshot twice across a short
/// observation window so the gateway cannot call execution alone a successful heal.
/// Redrive is intentionally not compensatable: moving every pending row back to dead-letter
/// would race normal delivery and could corrupt unrelated dispatches.
/// </summary>
internal sealed class AlertDispatchAutonomousOperationConvergence(
    IAlertDispatchHealth dispatchHealth,
    IOptionsMonitor<OpsFindingsOptions> options,
    TimeProvider timeProvider) : IAutonomousOperationConvergence
{
    private const int ObservationCount = 2;
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(100);

    public bool CanHandle(OperationGatewayRequest request)
        => request.Kind == OperationClass.AdminConfigChange
            && string.Equals(ResolveAction(request), OpsActionNames.RedriveDeadLetters, StringComparison.Ordinal);

    public bool SupportsCompensation(OperationGatewayRequest request) => false;

    public async Task<AutonomousVerificationResult> VerifyAsync(
        OperationGatewayRequest request,
        string? executionOperationId,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandle(request))
        {
            return new AutonomousVerificationResult(
                AutonomousVerificationState.Indeterminate,
                "The alert-dispatch verifier does not own this action.");
        }

        for (var observation = 1; observation <= ObservationCount; observation++)
        {
            AlertDispatchBacklog backlog;
            try
            {
                backlog = await dispatchHealth.RefreshBacklogAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Intentional broad catch: this is a best-effort post-action verification
                // probe; any failure refreshing the backlog snapshot is reported as an
                // Indeterminate verification result rather than throwing out of the
                // convergence check.
                return new AutonomousVerificationResult(
                    AutonomousVerificationState.Indeterminate,
                    $"Alert-dispatch backlog observation {observation.ToString(CultureInfo.InvariantCulture)} "
                    + $"could not complete ({ex.GetType().Name}).");
            }

            var current = options.CurrentValue;
            var deadLetterFindingPersists = backlog.DeadLetteredCount >= current.AlertDispatchDeadLetterThreshold;
            var pendingFindingPersists = backlog.PendingCount >= current.AlertDispatchPendingBacklogThreshold;
            if (deadLetterFindingPersists || pendingFindingPersists)
            {
                return new AutonomousVerificationResult(
                    AutonomousVerificationState.Failed,
                    $"The alert-dispatch finding persisted at observation "
                    + $"{observation.ToString(CultureInfo.InvariantCulture)} of {ObservationCount.ToString(CultureInfo.InvariantCulture)} "
                    + $"(pending={backlog.PendingCount.ToString(CultureInfo.InvariantCulture)}, "
                    + $"deadLettered={backlog.DeadLetteredCount.ToString(CultureInfo.InvariantCulture)}).");
            }

            if (observation < ObservationCount)
            {
                await Task.Delay(ObservationInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        var finalBacklog = dispatchHealth.LastBacklog;
        return new AutonomousVerificationResult(
            AutonomousVerificationState.Converged,
            $"The alert-dispatch finding remained clear for {ObservationCount.ToString(CultureInfo.InvariantCulture)} observations "
            + $"(pending={(finalBacklog?.PendingCount ?? 0).ToString(CultureInfo.InvariantCulture)}, "
            + $"deadLettered={(finalBacklog?.DeadLetteredCount ?? 0).ToString(CultureInfo.InvariantCulture)}).");
    }

    public Task<AutonomousCompensationResult> CompensateAsync(
        OperationGatewayRequest request,
        string? executionOperationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(
            new AutonomousCompensationResult(
                AutonomousCompensationState.NotSupported,
                "Dead-letter redrive cannot be safely compensated because redriven rows may already be in delivery."));

    private static string? ResolveAction(OperationGatewayRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ActionDiscriminator))
        {
            return request.ActionDiscriminator;
        }

        try
        {
            return OpsActionRequest.Parse(request.ExecutionPayload).Name;
        }
        catch (OpsActionException)
        {
            return null;
        }
    }
}
