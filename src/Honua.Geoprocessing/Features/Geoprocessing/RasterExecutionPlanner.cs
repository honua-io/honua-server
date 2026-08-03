// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.ServiceDefaults;

namespace Honua.Geoprocessing;

/// <summary>
/// Cost-aware raster planner that applies capability, locality, health, budget, and operator
/// policy gates before a durable job is created.
/// </summary>
internal sealed partial class RasterExecutionPlanner : IRasterExecutionPlanner
{
    private readonly IRasterEngineCapabilityRegistry _capabilities;
    private readonly ILogger<RasterExecutionPlanner> _logger;
    private readonly Counter<long> _decisions;

    public RasterExecutionPlanner(
        IRasterEngineCapabilityRegistry capabilities,
        ILogger<RasterExecutionPlanner> logger)
    {
        _capabilities = capabilities;
        _logger = logger;
        _decisions = HonuaTelemetry.Meter.CreateCounter<long>(
            "raster.execution.planning.decisions",
            unit: "{decision}",
            description: "Raster execution planning decisions and refusals.");
    }

    /// <inheritdoc />
    public RasterExecutionDecision Plan(RasterExecutionPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        if (request.MutatingAttemptStarted)
        {
            if (request.ExistingDecision is null)
            {
                throw Refuse(
                    request,
                    "mutation-decision-missing",
                    "Raster execution cannot be replanned after a mutating attempt starts without its pinned decision.");
            }

            if (!string.Equals(request.ExistingDecision.ProcessId, request.ProcessId, StringComparison.Ordinal))
            {
                throw Refuse(
                    request,
                    "mutation-decision-mismatch",
                    "The pinned raster decision belongs to a different process and cannot be reused.");
            }

            if (request.ExistingDecision.OutputSink != request.OutputSink
                || !request.ExistingDecision.InputResidencies.SequenceEqual(request.InputResidencies))
            {
                throw Refuse(
                    request,
                    "mutation-decision-mismatch",
                    "The pinned raster decision belongs to different input residency or output sink metadata and cannot be reused.");
            }

            _decisions.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "reused"),
                new KeyValuePair<string, object?>("engine", request.ExistingDecision.Engine.ToString()),
                new KeyValuePair<string, object?>("placement", request.ExistingDecision.Placement.ToString()),
                new KeyValuePair<string, object?>("reason", request.ExistingDecision.ReasonCode));
            Log.Decision(
                _logger,
                request.ProcessId,
                request.ExistingDecision.Engine,
                request.ExistingDecision.Placement,
                request.ExistingDecision.ReasonCode,
                request.ExistingDecision.PolicyRef);
            return request.ExistingDecision;
        }

        var process = _capabilities.Find(request.ProcessId)
            ?? throw Refuse(
                request,
                "capability-missing",
                $"Raster process '{request.ProcessId}' has no engine capability metadata.");

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "raster.execution.plan",
            ActivityKind.Internal);
        activity?.SetTag("honua.raster.process_id", request.ProcessId);
        activity?.SetTag("honua.raster.policy_ref", request.Policy.PolicyRef);
        activity?.SetTag("honua.raster.health_version", request.Health.Version);

        var candidates = new List<Candidate>();
        var eliminations = new List<string>();
        var hasRetryableBlocker = false;
        foreach (var engine in process.Engines)
        {
            EvaluateEngine(request, engine, candidates, eliminations, ref hasRetryableBlocker);
        }

        if (candidates.Count == 0)
        {
            var detail = eliminations.Count == 0
                ? "No engine produced an eligible placement."
                : string.Join(" ", eliminations);
            activity?.SetStatus(ActivityStatusCode.Error, "refused");
            throw Refuse(
                request,
                "no-eligible-raster-placement",
                $"Raster execution was refused for '{request.ProcessId}'. {detail}",
                hasRetryableBlocker);
        }

        var selected = candidates
            .OrderBy(candidate => RankEngine(request, candidate.Capability))
            .ThenBy(candidate => RankPlacement(request, candidate))
            .ThenBy(candidate => candidate.Capability.Engine)
            .First();

        var decision = new RasterExecutionDecision
        {
            ProcessId = request.ProcessId,
            Engine = selected.Capability.Engine,
            Placement = selected.Placement,
            InputResidencies = Array.AsReadOnly(request.InputResidencies.ToArray()),
            OutputSink = request.OutputSink,
            Cost = selected.Cost,
            SemanticVersion = process.SemanticVersion,
            ImplementationVersion = selected.Capability.ImplementationVersion,
            ReasonCode = selected.ReasonCode,
            Reason = selected.Reason,
            PolicyRef = request.Policy.PolicyRef,
            ConfigurationVersion = request.Budgets.Version,
            HealthVersion = request.Health.Version,
            Backend = selected.Placement == RasterExecutionPlacement.RemoteBackend
                ? request.Health.RemoteBackend
                : null,
        };

        activity?.SetTag("honua.raster.engine", decision.Engine.ToString());
        activity?.SetTag("honua.raster.placement", decision.Placement.ToString());
        activity?.SetTag("honua.raster.reason_code", decision.ReasonCode);
        _decisions.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "selected"),
            new KeyValuePair<string, object?>("engine", decision.Engine.ToString()),
            new KeyValuePair<string, object?>("placement", decision.Placement.ToString()),
            new KeyValuePair<string, object?>("reason", decision.ReasonCode));
        Log.Decision(
            _logger,
            request.ProcessId,
            decision.Engine,
            decision.Placement,
            decision.ReasonCode,
            request.Policy.PolicyRef);

        return decision;
    }

    private void EvaluateEngine(
        RasterExecutionPlanningRequest request,
        RasterEngineCapability capability,
        List<Candidate> candidates,
        List<string> eliminations,
        ref bool hasRetryableBlocker)
    {
        var engineName = capability.Engine.ToString();
        if (!capability.IsAvailable)
        {
            eliminations.Add($"{engineName}: {capability.UnavailabilityReason}");
            return;
        }

        if (!request.Policy.AllowedEngines.Contains(capability.Engine)
            || request.Policy.RequiredEngine is { } requiredEngine && requiredEngine != capability.Engine)
        {
            eliminations.Add($"{engineName}: disabled by operator policy '{request.Policy.PolicyRef}'.");
            return;
        }

        var unsupportedResidency = request.InputResidencies
            .FirstOrDefault(residency => !capability.InputResidencies.Contains(residency));
        if (request.InputResidencies.Any(residency => !capability.InputResidencies.Contains(residency)))
        {
            eliminations.Add($"{engineName}: input residency '{unsupportedResidency}' is not supported.");
            return;
        }

        if (!capability.OutputSinks.Contains(request.OutputSink))
        {
            eliminations.Add($"{engineName}: output sink '{request.OutputSink}' is not supported.");
            return;
        }

        var unsupportedMediaType = request.InputMediaTypes.FirstOrDefault(mediaType =>
            !capability.Formats.InputMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase));
        if (unsupportedMediaType is not null)
        {
            eliminations.Add($"{engineName}: input media type '{unsupportedMediaType}' is not supported.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.OutputMediaType)
            && !capability.Formats.OutputMediaTypes.Contains(
                request.OutputMediaType,
                StringComparer.OrdinalIgnoreCase))
        {
            eliminations.Add($"{engineName}: output media type '{request.OutputMediaType}' is not supported.");
            return;
        }

        var cost = _capabilities.Estimate(request.ProcessId, capability.Engine, request.Cost);
        if (capability.Engine == RasterEngine.Postgis)
        {
            EvaluatePostgis(
                request,
                capability,
                cost,
                candidates,
                eliminations,
                ref hasRetryableBlocker);
        }
        else
        {
            EvaluateNative(
                request,
                capability,
                cost,
                candidates,
                eliminations,
                ref hasRetryableBlocker);
        }
    }

    private static void EvaluatePostgis(
        RasterExecutionPlanningRequest request,
        RasterEngineCapability capability,
        RasterCostEstimate cost,
        List<Candidate> candidates,
        List<string> eliminations,
        ref bool hasRetryableBlocker)
    {
        if (request.Health.Database != RasterDatabaseHealth.Healthy)
        {
            hasRetryableBlocker = true;
            eliminations.Add(
                $"Postgis: database raster budget is '{request.Health.Database}' in health snapshot '{request.Health.Version}'.");
            return;
        }

        var withinRequest = cost.RequestExecutionAllowed
            && cost.DecodedBytes <= request.Budgets.MaxRequestDecodedBytes
            && cost.ExpectedScratchBytes <= request.Budgets.MaxRequestScratchBytes
            && cost.ExpectedDatabaseWork <= request.Budgets.MaxRequestDatabaseWork;
        var requestCandidateAdded = request.AllowRequestExecution
            && request.Policy.AllowedPlacements.Contains(RasterExecutionPlacement.Request)
            && PlacementMatchesPolicy(request, RasterExecutionPlacement.Request)
            && withinRequest;
        if (requestCandidateAdded)
        {
            candidates.Add(new Candidate(
                capability,
                RasterExecutionPlacement.Request,
                cost,
                "postgis-request-budget",
                "PostGIS is capable and source-local, and the complete estimate fits the request and database budgets."));
        }

        var withinDatabase = !cost.UsesConservativeValues
            && cost.DecodedBytes <= request.Budgets.MaxDatabaseDecodedBytes
            && cost.ExpectedScratchBytes <= request.Budgets.MaxDatabaseScratchBytes
            && cost.ExpectedDatabaseWork <= request.Budgets.MaxDatabaseWork;
        var durableCandidateAdded = request.Policy.AllowedPlacements.Contains(RasterExecutionPlacement.DurablePostgis)
            && PlacementMatchesPolicy(request, RasterExecutionPlacement.DurablePostgis)
            && withinDatabase;
        if (durableCandidateAdded)
        {
            candidates.Add(new Candidate(
                capability,
                RasterExecutionPlacement.DurablePostgis,
                cost,
                "postgis-source-local",
                "PostGIS is capable and source-local, and the estimate fits the governed durable database budget."));
        }

        if (!requestCandidateAdded && !durableCandidateAdded)
        {
            eliminations.Add(cost.UsesConservativeValues
                ? "Postgis: cost metadata is incomplete, so database admission fails closed."
                : !withinDatabase
                    ? "Postgis: the estimate exceeds the configured durable database raster budget."
                    : "Postgis: no allowed placement matches the operator policy and execution envelope.");
        }
    }

    private static void EvaluateNative(
        RasterExecutionPlanningRequest request,
        RasterEngineCapability capability,
        RasterCostEstimate cost,
        List<Candidate> candidates,
        List<string> eliminations,
        ref bool hasRetryableBlocker)
    {
        var localAllowed = request.Policy.AllowedPlacements.Contains(RasterExecutionPlacement.LocalNativeWorker)
            && PlacementMatchesPolicy(request, RasterExecutionPlacement.LocalNativeWorker);
        var remoteAllowed = request.Policy.AllowedPlacements.Contains(RasterExecutionPlacement.RemoteBackend)
            && PlacementMatchesPolicy(request, RasterExecutionPlacement.RemoteBackend);
        var withinLocal = !cost.UsesConservativeValues
            && cost.DecodedBytes <= request.Budgets.MaxLocalDecodedBytes
            && cost.ExpectedScratchBytes <= request.Budgets.MaxLocalScratchBytes;
        var external = request.InputResidencies.Any(residency =>
            residency is RasterInputResidency.ObjectStoreCog
                or RasterInputResidency.ObjectStoreZarr
                or RasterInputResidency.StagedArtifact);

        if (localAllowed && !request.Health.LocalNativeWorkerAvailable
            || remoteAllowed && !request.Health.RemoteNativeBackendAvailable)
        {
            hasRetryableBlocker = true;
        }

        if (localAllowed && request.Health.LocalNativeWorkerAvailable && withinLocal && !external)
        {
            candidates.Add(new Candidate(
                capability,
                RasterExecutionPlacement.LocalNativeWorker,
                cost,
                "native-local-budget",
                "Native GDAL is capable and the trusted estimate fits the isolated local-worker budget."));
        }

        if (remoteAllowed && request.Health.RemoteNativeBackendAvailable)
        {
            var reasonCode = cost.UsesConservativeValues
                ? "native-remote-conservative"
                : external
                    ? "native-remote-source-local"
                    : "native-remote-burst-isolation";
            var reason = cost.UsesConservativeValues
                ? "Cost metadata is incomplete, so native execution is conservatively isolated on the configured remote backend."
                : external
                    ? "The external immutable source is local to the configured remote native backend."
                    : "The estimate exceeds the local-worker budget and is isolated on the configured remote native backend.";
            candidates.Add(new Candidate(capability, RasterExecutionPlacement.RemoteBackend, cost, reasonCode, reason));
        }

        if (external && (!request.Health.RemoteNativeBackendAvailable || !remoteAllowed))
        {
            eliminations.Add(
                "GdalNative: external raster sources require an allowed available remote native backend.");
        }
        else if ((!request.Health.LocalNativeWorkerAvailable || !withinLocal || !localAllowed)
            && (!request.Health.RemoteNativeBackendAvailable || !remoteAllowed))
        {
            eliminations.Add(cost.UsesConservativeValues
                ? "GdalNative: cost metadata is incomplete and no allowed remote backend is available."
                : "GdalNative: no allowed available placement fits the local-worker budget.");
        }
    }

    private static int RankEngine(
        RasterExecutionPlanningRequest request,
        RasterEngineCapability capability)
    {
        if (request.Policy.RequiredEngine == capability.Engine)
        {
            return 0;
        }

        if (request.Policy.PreferredEngine == capability.Engine)
        {
            return 1;
        }

        var postgisLocal = request.InputResidencies.Count > 0
            && request.InputResidencies.All(residency => residency == RasterInputResidency.Postgis);
        if (postgisLocal == (capability.Engine == RasterEngine.Postgis))
        {
            return 2;
        }

        return capability.DefaultPreference == RasterEngineDefaultPreference.Preferred ? 3 : 4;
    }

    private static int RankPlacement(RasterExecutionPlanningRequest request, Candidate candidate)
    {
        if (request.Policy.RequiredPlacement == candidate.Placement)
        {
            return 0;
        }

        return candidate.Placement switch
        {
            RasterExecutionPlacement.Request => 1,
            RasterExecutionPlacement.DurablePostgis => 2,
            RasterExecutionPlacement.LocalNativeWorker => 3,
            RasterExecutionPlacement.RemoteBackend => 4,
            _ => int.MaxValue,
        };
    }

    private static bool PlacementMatchesPolicy(
        RasterExecutionPlanningRequest request,
        RasterExecutionPlacement placement)
        => request.Policy.RequiredPlacement is not { } required || required == placement;

    private static void ValidateRequest(RasterExecutionPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.InputResidencies);
        ArgumentNullException.ThrowIfNull(request.InputMediaTypes);
        ArgumentNullException.ThrowIfNull(request.Cost);
        ArgumentNullException.ThrowIfNull(request.Budgets);
        ArgumentNullException.ThrowIfNull(request.Health);
        ArgumentNullException.ThrowIfNull(request.Policy);
        ArgumentNullException.ThrowIfNull(request.Policy.AllowedEngines);
        ArgumentNullException.ThrowIfNull(request.Policy.AllowedPlacements);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProcessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Budgets.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Health.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Policy.PolicyRef);
        if (request.InputResidencies.Count == 0)
        {
            throw new ArgumentException("At least one raster input residency is required.", nameof(request));
        }

        if (request.InputMediaTypes.Count != request.InputResidencies.Count
            || request.InputMediaTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Raster input media types must be non-empty and align with input residencies.",
                nameof(request));
        }
        if (request.InputResidencies.Any(residency => !Enum.IsDefined(residency))
            || !Enum.IsDefined(request.OutputSink)
            || !Enum.IsDefined(request.Health.Database)
            || request.Policy.AllowedEngines.Any(engine => !Enum.IsDefined(engine))
            || request.Policy.AllowedPlacements.Any(placement => !Enum.IsDefined(placement))
            || request.Policy.RequiredEngine is { } requiredEngine && !Enum.IsDefined(requiredEngine)
            || request.Policy.PreferredEngine is { } preferredEngine && !Enum.IsDefined(preferredEngine)
            || request.Policy.RequiredPlacement is { } requiredPlacement && !Enum.IsDefined(requiredPlacement))
        {
            throw new ArgumentException("Raster planning snapshots contain an undefined enum value.", nameof(request));
        }
        if (request.Budgets.MaxRequestDecodedBytes <= 0
            || request.Budgets.MaxRequestScratchBytes <= 0
            || request.Budgets.MaxRequestDatabaseWork <= 0
            || request.Budgets.MaxDatabaseDecodedBytes <= 0
            || request.Budgets.MaxDatabaseScratchBytes <= 0
            || request.Budgets.MaxDatabaseWork <= 0
            || request.Budgets.MaxLocalDecodedBytes <= 0
            || request.Budgets.MaxLocalScratchBytes <= 0)
        {
            throw new ArgumentException("Raster planning budgets must be positive.", nameof(request));
        }

        if (request.Health.RemoteNativeBackendAvailable && string.IsNullOrWhiteSpace(request.Health.RemoteBackend))
        {
            throw new ArgumentException(
                "An available remote native backend requires a stable backend identifier.",
                nameof(request));
        }

        if (request.ExistingDecision is not null)
        {
            ValidateExistingDecision(request.ExistingDecision, request);
        }
    }

    private static void ValidateExistingDecision(
        RasterExecutionDecision decision,
        RasterExecutionPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(decision.InputResidencies);
        ArgumentNullException.ThrowIfNull(decision.Cost);
        ArgumentNullException.ThrowIfNull(decision.Cost.UnknownInputs);
        if (decision.DecisionVersion != 1
            || !Enum.IsDefined(decision.Engine)
            || !Enum.IsDefined(decision.Placement)
            || !Enum.IsDefined(decision.OutputSink)
            || decision.InputResidencies.Count == 0
            || decision.InputResidencies.Any(residency => !Enum.IsDefined(residency))
            || decision.Cost.Engine != decision.Engine
            || !Enum.IsDefined(decision.Cost.Engine))
        {
            throw new ArgumentException("The pinned raster decision has an invalid schema or enum value.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(decision.ProcessId)
            || string.IsNullOrWhiteSpace(decision.SemanticVersion)
            || string.IsNullOrWhiteSpace(decision.ImplementationVersion)
            || string.IsNullOrWhiteSpace(decision.ReasonCode)
            || string.IsNullOrWhiteSpace(decision.Reason)
            || string.IsNullOrWhiteSpace(decision.PolicyRef)
            || string.IsNullOrWhiteSpace(decision.ConfigurationVersion)
            || string.IsNullOrWhiteSpace(decision.HealthVersion)
            || !string.Equals(decision.Cost.ProcessId, decision.ProcessId, StringComparison.Ordinal)
            || decision.Cost.SourceCount < 0
            || decision.Cost.BandCount < 0
            || decision.Cost.ZoneCount < 0
            || decision.Cost.InputPixels < 0
            || decision.Cost.OutputPixels < 0
            || decision.Cost.DecodedBytes < 0
            || decision.Cost.ExpectedScratchBytes < 0
            || decision.Cost.ExpectedDatabaseWork < 0)
        {
            throw new ArgumentException("The pinned raster decision has invalid identity or cost metadata.", nameof(request));
        }

        var placementMatchesEngine = decision.Engine switch
        {
            RasterEngine.Postgis => decision.Placement is RasterExecutionPlacement.Request
                or RasterExecutionPlacement.DurablePostgis,
            RasterEngine.GdalNative => decision.Placement is RasterExecutionPlacement.LocalNativeWorker
                or RasterExecutionPlacement.RemoteBackend,
            _ => false,
        };
        var backendMatchesPlacement = decision.Placement == RasterExecutionPlacement.RemoteBackend
            ? !string.IsNullOrWhiteSpace(decision.Backend)
            : decision.Backend is null;
        if (!placementMatchesEngine || !backendMatchesPlacement)
        {
            throw new ArgumentException("The pinned raster decision has an invalid engine/placement binding.", nameof(request));
        }
    }

    private RasterExecutionPlanningException Refuse(
        RasterExecutionPlanningRequest request,
        string reasonCode,
        string message,
        bool isRetryable = false)
    {
        _decisions.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "refused"),
            new KeyValuePair<string, object?>("engine", "none"),
            new KeyValuePair<string, object?>("placement", "none"),
            new KeyValuePair<string, object?>("reason", reasonCode));
        Log.Refused(_logger, request.ProcessId, reasonCode, request.Policy.PolicyRef, message);
        return new RasterExecutionPlanningException(reasonCode, message, isRetryable);
    }

    private sealed record Candidate(
        RasterEngineCapability Capability,
        RasterExecutionPlacement Placement,
        RasterCostEstimate Cost,
        string ReasonCode,
        string Reason);

    private static partial class Log
    {
        [LoggerMessage(
            7560,
            LogLevel.Information,
            "Raster execution planned for {ProcessId}: engine={Engine}, placement={Placement}, reason={ReasonCode}, policy={PolicyRef}")]
        public static partial void Decision(
            ILogger logger,
            string processId,
            RasterEngine engine,
            RasterExecutionPlacement placement,
            string reasonCode,
            string policyRef);

        [LoggerMessage(
            7561,
            LogLevel.Warning,
            "Raster execution refused for {ProcessId}: reason={ReasonCode}, policy={PolicyRef}, detail={Detail}")]
        public static partial void Refused(
            ILogger logger,
            string processId,
            string reasonCode,
            string policyRef,
            string detail);
    }
}
