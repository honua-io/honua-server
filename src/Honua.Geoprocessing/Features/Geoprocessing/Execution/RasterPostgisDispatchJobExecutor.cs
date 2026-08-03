// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Dedicated <c>raster-postgis</c> durable-job dispatcher. It routes only exact provider,
/// semantic, implementation, and policy identities pinned in the durable raster decision and
/// never executes SQL itself. Provider packages remain behind <see cref="IRasterProviderExecutor"/>.
/// </summary>
internal sealed partial class RasterPostgisDispatchJobExecutor : IJobExecutor
{
    private readonly FrozenDictionary<RasterProviderRouteKey, RasterProviderExecutorRegistration> _routes;
    private readonly ILogger<RasterPostgisDispatchJobExecutor> _logger;

    /// <summary>Creates a fail-fast exact-variant PostGIS raster dispatcher.</summary>
    public RasterPostgisDispatchJobExecutor(
        IEnumerable<IRasterProviderExecutor> executors,
        ILogger<RasterPostgisDispatchJobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(logger);

        _routes = RasterProviderExecutorRouteTable.Build(executors);
        if (_routes.Keys.Any(key => key.Engine != RasterEngine.Postgis))
        {
            throw new InvalidOperationException(
                "The raster-postgis dispatcher may contain only PostGIS raster provider routes.");
        }

        _logger = logger;
    }

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public IReadOnlySet<string> AcceptedRuntimeProfiles => RuntimeProfiles.RasterPostgisAccepted;

    /// <summary>Exact provider routes owned by this dispatcher.</summary>
    internal IReadOnlyCollection<RasterProviderRouteKey> SupportedRoutes => _routes.Keys;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(
                RuntimeProfiles.Normalize(job.Spec.RuntimeProfile),
                RuntimeProfiles.RasterPostgis,
                StringComparison.Ordinal))
        {
            return Refused(job, "runtime-profile-mismatch",
                "The durable job is not fenced to the raster-postgis runtime profile.");
        }

        var decision = job.Spec.RasterExecution;
        if (decision is null
            || decision.Engine != RasterEngine.Postgis
            || decision.Placement != RasterExecutionPlacement.DurablePostgis)
        {
            return Refused(job, "decision-missing-or-invalid",
                "The durable job does not carry a PostGIS durable raster execution decision.");
        }

        var processId = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (string.IsNullOrWhiteSpace(processId)
            || !string.Equals(processId, decision.ProcessId, StringComparison.Ordinal))
        {
            return Refused(job, "process-decision-mismatch",
                "The durable process identity does not match the pinned raster decision.");
        }

        if (string.IsNullOrWhiteSpace(decision.ProviderId)
            || string.IsNullOrWhiteSpace(decision.ProviderPolicyVersion))
        {
            return Refused(job, "provider-decision-missing",
                "The pinned raster decision does not contain provider and provider-policy identities.");
        }

        if (!job.Spec.Parameters.TryGetValue(
                RasterProviderExecutionParameterKeys.TenantId,
                out var tenantId)
            || string.IsNullOrWhiteSpace(tenantId))
        {
            return Refused(job, "tenant-fence-missing",
                "The raster provider request does not contain a pinned tenant identity.");
        }

        var routeKey = new RasterProviderRouteKey(
            decision.Engine,
            decision.ProviderId,
            decision.ProcessId,
            decision.SemanticVersion,
            decision.ImplementationVersion,
            decision.ProviderPolicyVersion);
        if (!_routes.TryGetValue(routeKey, out var registration))
        {
            return Refused(job, "capability-unavailable",
                "No raster provider executor exposes the exact capability pinned by this attempt.");
        }

        if (registration.Capability.Availability != RasterProviderAvailability.Available)
        {
            return Refused(job, "capability-unavailable",
                registration.Capability.UnavailabilityReason
                ?? "The pinned raster provider capability is unavailable.");
        }

        var request = new RasterProviderExecutionRequest
        {
            OperationId = job.OperationId,
            Attempt = Math.Max(job.AttemptCount, 1),
            TenantId = tenantId,
            Decision = decision,
            Parameters = job.Spec.Parameters.ToFrozenDictionary(StringComparer.Ordinal),
        };
        var result = await registration.Executor
            .ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return Refused(job, "provider-result-missing",
                "The raster provider returned no execution result.");
        }

        if (result.Status != RasterProviderExecutionStatus.Succeeded)
        {
            var errorCode = string.IsNullOrWhiteSpace(result.ErrorCode)
                ? result.Status == RasterProviderExecutionStatus.CapabilityUnavailable
                    ? "capability-unavailable"
                    : "provider-execution-failed"
                : result.ErrorCode;
            var errorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "The raster provider could not complete the pinned execution attempt."
                : result.ErrorMessage;
            return Refused(job, errorCode, errorMessage);
        }

        var outputError = ValidateOutputs(result.Outputs);
        if (outputError is not null)
        {
            return Refused(job, "provider-result-invalid", outputError);
        }

        foreach (var output in result.Outputs)
        {
            await context.PublishArtifactAsync(output.Reference, cancellationToken).ConfigureAwait(false);
        }

        return JobExecutionResult.Succeeded();
    }

    private JobExecutionResult Refused(ExecutionJobRecord job, string reasonCode, string reason)
    {
        Log.Refused(_logger, job.OperationId, reasonCode);
        return JobExecutionResult.Failed($"Raster provider execution refused ({reasonCode}): {reason}");
    }

    private static string? ValidateOutputs(IReadOnlyList<RasterProviderResultReference>? outputs)
    {
        if (outputs is null || outputs.Count == 0)
        {
            return "A successful raster provider result must contain at least one immutable output reference.";
        }

        foreach (var output in outputs)
        {
            if (output is null
                || string.IsNullOrWhiteSpace(output.Reference)
                || string.IsNullOrWhiteSpace(output.MediaType)
                || output.Length is < 0
                || output.Sha256 is not null
                    && (output.Sha256.Length != 64 || !output.Sha256.All(Uri.IsHexDigit)))
            {
                return "The raster provider returned an invalid immutable output reference.";
            }
        }

        return null;
    }

    private static partial class Log
    {
        [LoggerMessage(
            7562,
            LogLevel.Warning,
            "PostGIS raster dispatcher refused job {OperationId}: reason={ReasonCode}")]
        public static partial void Refused(ILogger logger, string operationId, string reasonCode);
    }
}
