// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Geoprocessing;

/// <summary>A workload plus the durable decision that selected it.</summary>
internal sealed record GpWorkloadPlacementResult(
    ExecutionJobDefinition? Workload,
    ExecutionPlacementDecision? Decision);

/// <summary>
/// Pure per-job workload selection policy. It never reads raster bytes or invokes a compute
/// backend; it evaluates the already-resolved runtime/resource request against declarative
/// workload envelopes and returns the target that may be persisted before submission.
/// </summary>
internal static class GpWorkloadPlacementPlanner
{
    private const string LocalClass = "local";
    private const string RemoteClass = "remote";
    private const string HealthyCapacity = "healthy";
    private const string PressuredCapacity = "pressured";
    private const string UnavailableCapacity = "unavailable";

    public static GpWorkloadPlacementResult Select(
        IReadOnlyList<ExecutionJobDefinition> workloads,
        IReadOnlyList<IBatchComputeBackend> backends,
        bool localQueueAvailable,
        string? requiredRuntimeProfile,
        GpResourceProfile resources,
        IReadOnlyDictionary<string, string> requestParameters,
        RasterExecutionDecision? rasterDecision,
        GpWorkloadPlacementOptions options)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(requestParameters);
        ArgumentNullException.ThrowIfNull(options);

        var runtimeProfile = RuntimeProfiles.Normalize(requiredRuntimeProfile);
        var candidates = workloads
            .Where(workload => workload.Kind == ExecutionJobKind.Geoprocessing)
            .Where(workload => !IsCustomCode(workload))
            .Select(workload => EvaluateCandidate(
                workload,
                backends,
                localQueueAvailable,
                runtimeProfile,
                resources,
                options))
            .ToArray();

        var intent = ResolveIntent(requestParameters, rasterDecision, resources, options);
        var compatible = candidates.Where(candidate => candidate.IsCompatible).ToArray();
        var selected = SelectCandidate(compatible, intent, requestParameters, options);

        if (selected is null)
        {
            throw NoCompatibleWorkload(intent, candidates);
        }

        var fallback = intent.RequiredClass is null
            && string.IsNullOrWhiteSpace(intent.RequiredBackend)
            && selected.IsLocal != intent.PreferLocal;
        var reasonCode = fallback
            ? selected.IsLocal ? "gp:local-fallback" : "gp:remote-fallback"
            : intent.ReasonCode;
        var reason = fallback
            ? $"{intent.Reason} Preferred execution was unavailable; policy selected compatible "
                + $"workload '{selected.Workload.WorkloadId}' on backend '{selected.Workload.Backend}'."
            : $"{intent.Reason} Selected compatible workload '{selected.Workload.WorkloadId}' "
                + $"on backend '{selected.Workload.Backend}'.";

        return new GpWorkloadPlacementResult(
            selected.Workload,
            BuildDecision(selected.Workload, runtimeProfile, resources, options, reasonCode, reason, fallback));
    }

    /// <summary>
    /// Evaluates the zero-configuration local queue through the same intent, availability, and
    /// fallback policy used for catalog workloads, then removes its synthetic workload identity
    /// from the durable decision.
    /// </summary>
    public static GpWorkloadPlacementResult SelectImplicitLocal(
        string? requiredRuntimeProfile,
        GpResourceProfile resources,
        IReadOnlyDictionary<string, string> requestParameters,
        RasterExecutionDecision? rasterDecision,
        GpWorkloadPlacementOptions options)
    {
        var implicitWorkload = new ExecutionJobDefinition
        {
            WorkloadId = "implicit-local",
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = LocalBatchComputeBackend.BackendId,
            WorkloadName = "Implicit local geoprocessing queue",
            RuntimeProfile = requiredRuntimeProfile,
            Parameters = new Dictionary<string, string>
            {
                [GpWorkloadPlacementParameterKeys.ExecutionClass] = LocalClass,
            },
        };
        var selected = Select(
            [implicitWorkload],
            [],
            localQueueAvailable: true,
            requiredRuntimeProfile,
            resources,
            requestParameters,
            rasterDecision,
            options);

        return new GpWorkloadPlacementResult(
            null,
            selected.Decision! with
            {
                WorkloadId = null,
                Reason = selected.Decision.Reason.Replace(
                    "workload 'implicit-local'",
                    "the implicit local queue",
                    StringComparison.Ordinal),
            });
    }

    private static PlacementCandidate EvaluateCandidate(
        ExecutionJobDefinition workload,
        IReadOnlyList<IBatchComputeBackend> backends,
        bool localQueueAvailable,
        string runtimeProfile,
        GpResourceProfile resources,
        GpWorkloadPlacementOptions options)
    {
        var isLocal = IsLocal(workload);
        var incompatibilities = new List<string>();

        var declaredClass = Read(workload.Parameters, GpWorkloadPlacementParameterKeys.ExecutionClass);
        if (declaredClass is not null and not (LocalClass or RemoteClass))
        {
            incompatibilities.Add($"execution class declaration '{declaredClass}' is invalid");
        }

        if (workload.Parameters.TryGetValue(GpWorkloadPlacementParameterKeys.Enabled, out var enabledRaw))
        {
            if (!bool.TryParse(enabledRaw, out var enabled))
            {
                incompatibilities.Add("enabled declaration is not a Boolean");
            }
            else if (!enabled)
            {
                incompatibilities.Add("disabled by workload declaration");
            }
        }

        if (isLocal ? !options.LocalExecutionEnabled : !options.RemoteExecutionEnabled)
        {
            incompatibilities.Add(isLocal
                ? "local execution is disabled by policy"
                : "remote execution is disabled by policy");
        }

        if (!BackendAvailable(workload, backends, localQueueAvailable))
        {
            incompatibilities.Add($"backend '{workload.Backend}' is not available");
        }

        if (!AcceptsRuntimeProfile(workload, runtimeProfile))
        {
            incompatibilities.Add($"runtime profile '{runtimeProfile}' is not declared");
        }

        AddMaximumViolation(incompatibilities, workload.Parameters, GpWorkloadPlacementParameterKeys.MaxVcpus, "vCPU", resources.Vcpus);
        AddMaximumViolation(incompatibilities, workload.Parameters, GpWorkloadPlacementParameterKeys.MaxMemoryMib, "memory MiB", resources.MemoryMib);
        AddMaximumViolation(incompatibilities, workload.Parameters, GpWorkloadPlacementParameterKeys.MaxGpuCount, "GPU", resources.GpuCount);
        AddMaximumViolation(incompatibilities, workload.Parameters, GpWorkloadPlacementParameterKeys.MaxTimeoutSeconds, "timeout seconds", resources.TimeoutSeconds);
        AddMaximumViolation(incompatibilities, workload.Parameters, GpWorkloadPlacementParameterKeys.MaxRetryAttempts, "retry attempts", resources.RetryAttempts);
        AddMaximumViolation(incompatibilities, workload.Parameters, GpWorkloadPlacementParameterKeys.MaxEphemeralGib, "ephemeral GiB", resources.EphemeralGib);

        if (!AcceptsArchitecture(workload, resources.Arch))
        {
            incompatibilities.Add($"architecture '{resources.Arch}' is not declared");
        }

        if (!isLocal
            && workload.TargetKind == BatchComputeTargetKind.KubernetesJob
            && resources.GpuCount is > 0)
        {
            incompatibilities.Add(
                "the Kubernetes execution backend cannot materialize a positive GPU resource request");
        }

        ValidateAwsTier(workload, resources, incompatibilities);

        var capacity = ReadCapacity(workload, isLocal, options);
        if (capacity is not (HealthyCapacity or PressuredCapacity or UnavailableCapacity))
        {
            incompatibilities.Add($"capacity declaration '{capacity}' is invalid");
        }
        else if (string.Equals(capacity, UnavailableCapacity, StringComparison.Ordinal))
        {
            incompatibilities.Add("capacity is unavailable");
        }

        return new PlacementCandidate(
            workload,
            isLocal,
            string.Equals(capacity, PressuredCapacity, StringComparison.Ordinal),
            ReadPriority(workload),
            incompatibilities);
    }

    private static PlacementIntent ResolveIntent(
        IReadOnlyDictionary<string, string> requestParameters,
        RasterExecutionDecision? rasterDecision,
        GpResourceProfile resources,
        GpWorkloadPlacementOptions options)
    {
        var requestedMode = Read(requestParameters, GpWorkloadPlacementParameterKeys.Mode);
        var requestedBackend = Read(requestParameters, GpWorkloadPlacementParameterKeys.Backend);

        if (!string.IsNullOrWhiteSpace(requestedMode)
            && requestedMode is not ("auto" or LocalClass or RemoteClass))
        {
            throw new GeoprocessingValidationException(
                $"Parameter '{GpWorkloadPlacementParameterKeys.Mode}' must be 'auto', 'local', or 'remote'.");
        }

        if (rasterDecision is not null)
        {
            var remote = rasterDecision.Placement == RasterExecutionPlacement.RemoteBackend;
            if (string.Equals(requestedMode, remote ? LocalClass : RemoteClass, StringComparison.Ordinal))
            {
                throw new GeoprocessingValidationException(
                    $"Parameter '{GpWorkloadPlacementParameterKeys.Mode}' conflicts with the pinned "
                    + $"raster placement '{rasterDecision.Placement}'.");
            }

            return new PlacementIntent(
                PreferLocal: !remote,
                RequiredClass: remote ? RemoteClass : LocalClass,
                RequiredBackend: requestedBackend,
                ReasonCode: remote ? "gp:raster-pinned-remote" : "gp:raster-pinned-local",
                Reason: $"Raster engine planning pinned '{rasterDecision.Placement}' ({rasterDecision.ReasonCode}).");
        }

        if (options.ForceRemoteIsolation)
        {
            if (string.Equals(requestedMode, LocalClass, StringComparison.Ordinal))
            {
                throw new GeoprocessingValidationException(
                    $"Parameter '{GpWorkloadPlacementParameterKeys.Mode}' conflicts with the operator's forced remote isolation policy.");
            }

            return new PlacementIntent(
                false,
                RemoteClass,
                requestedBackend,
                "gp:forced-remote-isolation",
                "Remote isolation was forced by operator policy.");
        }

        if (string.Equals(requestedMode, LocalClass, StringComparison.Ordinal))
        {
            return new PlacementIntent(true, LocalClass, requestedBackend, "gp:forced-local", "The job request forced local execution.");
        }

        if (string.Equals(requestedMode, RemoteClass, StringComparison.Ordinal))
        {
            return new PlacementIntent(
                false,
                RemoteClass,
                requestedBackend,
                "gp:forced-remote-isolation",
                "Remote isolation was forced by the job request.");
        }

        if (!string.IsNullOrWhiteSpace(requestedBackend))
        {
            return new PlacementIntent(
                PreferLocal: false,
                RequiredClass: null,
                RequiredBackend: requestedBackend,
                ReasonCode: "gp:forced-backend",
                Reason: $"The job request pinned backend '{requestedBackend}'.");
        }

        if (ExceedsLocalThreshold(resources, options))
        {
            return new PlacementIntent(
                false,
                null,
                null,
                "gp:resource-threshold-offload",
                "The declared per-job resource profile exceeds the low-latency local envelope.");
        }

        var affinity = Read(requestParameters, GpWorkloadPlacementParameterKeys.Affinity);
        if (!string.IsNullOrWhiteSpace(affinity))
        {
            return new PlacementIntent(
                false,
                null,
                null,
                "gp:object-store-affinity",
                $"The job declared '{affinity}' data affinity, so colocated remote execution is preferred.");
        }

        if (options.LocalCapacity == GpLocalCapacityState.Pressured)
        {
            return new PlacementIntent(
                false,
                null,
                null,
                "gp:local-capacity-offload",
                "The configured local execution lane is under capacity pressure.");
        }

        if (options.LocalCapacity == GpLocalCapacityState.Unavailable)
        {
            return new PlacementIntent(
                false,
                null,
                null,
                "gp:local-unavailable-offload",
                "The configured local execution lane is unavailable.");
        }

        return new PlacementIntent(
            true,
            null,
            null,
            "gp:low-latency-local",
            "The job fits the low-latency local resource envelope.");
    }

    private static PlacementCandidate? SelectCandidate(
        IReadOnlyList<PlacementCandidate> candidates,
        PlacementIntent intent,
        IReadOnlyDictionary<string, string> requestParameters,
        GpWorkloadPlacementOptions options)
    {
        var scoped = candidates
            .Where(candidate => intent.RequiredClass is null
                || string.Equals(intent.RequiredClass, candidate.IsLocal ? LocalClass : RemoteClass, StringComparison.Ordinal))
            .Where(candidate => string.IsNullOrWhiteSpace(intent.RequiredBackend)
                || string.Equals(candidate.Workload.Backend, intent.RequiredBackend, StringComparison.Ordinal))
            .ToArray();

        if (intent.RequiredClass is not null || !string.IsNullOrWhiteSpace(intent.RequiredBackend))
        {
            return Rank(scoped.Where(candidate => !candidate.IsPressured), requestParameters).FirstOrDefault()
                ?? (intent.PreferLocal && options.AllowPressuredLocalFallback
                    ? Rank(scoped.Where(candidate => candidate.IsLocal), requestParameters).FirstOrDefault()
                    : null);
        }

        var preferred = Rank(
                scoped.Where(candidate => candidate.IsLocal == intent.PreferLocal && !candidate.IsPressured),
                requestParameters)
            .FirstOrDefault();
        if (preferred is not null)
        {
            return preferred;
        }

        var allowOpposite = intent.PreferLocal ? options.AllowRemoteFallback : options.AllowLocalFallback;
        if (allowOpposite)
        {
            var fallback = Rank(
                    scoped.Where(candidate => candidate.IsLocal != intent.PreferLocal && !candidate.IsPressured),
                    requestParameters)
                .FirstOrDefault();
            if (fallback is not null)
            {
                return fallback;
            }
        }

        return options.AllowPressuredLocalFallback
            ? Rank(scoped.Where(candidate => candidate.IsLocal), requestParameters).FirstOrDefault()
            : null;
    }

    private static IOrderedEnumerable<PlacementCandidate> Rank(
        IEnumerable<PlacementCandidate> candidates,
        IReadOnlyDictionary<string, string> requestParameters)
    {
        var affinity = Read(requestParameters, GpWorkloadPlacementParameterKeys.Affinity);
        return candidates
            .OrderBy(candidate => AffinityRank(candidate.Workload, affinity))
            .ThenBy(candidate => candidate.Priority)
            .ThenBy(candidate => TargetRank(candidate.Workload.TargetKind))
            .ThenBy(candidate => candidate.Workload.Backend, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Workload.WorkloadId, StringComparer.Ordinal);
    }

    private static int AffinityRank(ExecutionJobDefinition workload, string? affinity)
    {
        if (string.IsNullOrWhiteSpace(affinity))
        {
            return 0;
        }

        if (ReadSet(workload.Parameters, GpWorkloadPlacementParameterKeys.Affinities).Contains(affinity))
        {
            return 0;
        }

        return string.Equals(affinity, "s3", StringComparison.Ordinal)
            && workload.TargetKind == BatchComputeTargetKind.AwsBatch
                ? 0
                : 1;
    }

    private static int TargetRank(BatchComputeTargetKind targetKind) => targetKind switch
    {
        BatchComputeTargetKind.LocalProcess => 0,
        BatchComputeTargetKind.AwsBatch => 1,
        BatchComputeTargetKind.KubernetesJob => 2,
        BatchComputeTargetKind.AzureBatch => 3,
        _ => 4,
    };

    private static bool IsLocal(ExecutionJobDefinition workload)
    {
        var declaredClass = Read(workload.Parameters, GpWorkloadPlacementParameterKeys.ExecutionClass);
        if (string.Equals(declaredClass, LocalClass, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(declaredClass, RemoteClass, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(workload.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
            || workload.TargetKind == BatchComputeTargetKind.LocalProcess;
    }

    private static bool BackendAvailable(
        ExecutionJobDefinition workload,
        IReadOnlyList<IBatchComputeBackend> backends,
        bool localQueueAvailable)
    {
        if (string.Equals(workload.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            return localQueueAvailable;
        }

        return backends.Any(backend =>
            string.Equals(backend.BackendName, workload.Backend, StringComparison.Ordinal)
            && backend.TargetKind == workload.TargetKind);
    }

    private static bool AcceptsRuntimeProfile(ExecutionJobDefinition workload, string required)
    {
        var declared = ReadSet(workload.Parameters, GpWorkloadPlacementParameterKeys.RuntimeProfiles);
        if (declared.Count > 0)
        {
            return declared.Contains(required);
        }

        // Match the durable claim-fence contract: an unspecified workload profile is
        // managed-only, never an implicit wildcard that may claim native GDAL work.
        return string.Equals(
            RuntimeProfiles.Normalize(workload.RuntimeProfile),
            required,
            StringComparison.Ordinal);
    }

    private static bool AcceptsArchitecture(ExecutionJobDefinition workload, string? required)
    {
        if (string.IsNullOrWhiteSpace(required))
        {
            return true;
        }

        var declared = ReadSet(workload.Parameters, GpWorkloadPlacementParameterKeys.Architectures);
        return declared.Count == 0 || declared.Contains(required);
    }

    private static string ReadCapacity(
        ExecutionJobDefinition workload,
        bool isLocal,
        GpWorkloadPlacementOptions options)
    {
        var declared = Read(workload.Parameters, GpWorkloadPlacementParameterKeys.Capacity);
        if (!string.IsNullOrWhiteSpace(declared))
        {
            return declared;
        }

        if (!isLocal)
        {
            return HealthyCapacity;
        }

        return options.LocalCapacity switch
        {
            GpLocalCapacityState.Healthy => HealthyCapacity,
            GpLocalCapacityState.Pressured => PressuredCapacity,
            GpLocalCapacityState.Unavailable => UnavailableCapacity,
            _ => UnavailableCapacity,
        };
    }

    private static void ValidateAwsTier(
        ExecutionJobDefinition workload,
        GpResourceProfile resources,
        List<string> incompatibilities)
    {
        if (workload.TargetKind != BatchComputeTargetKind.AwsBatch
            || resources.EphemeralGib is not { } ephemeral
            || !workload.Parameters.Keys.Any(key => key.StartsWith("batch.job_definition_arn.", StringComparison.Ordinal)))
        {
            return;
        }

        var tier = ephemeral switch
        {
            <= 20 => "s",
            <= 50 => "m",
            <= 100 => "l",
            <= 200 => "xl",
            _ => null,
        };

        if (tier is null)
        {
            incompatibilities.Add($"ephemeral GiB request {ephemeral} exceeds the AWS Batch tier ceiling of 200");
            return;
        }

        var key = "batch.job_definition_arn." + tier;
        if (!workload.Parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            incompatibilities.Add($"required AWS Batch job-definition tier '{tier}' is not configured");
        }
    }

    private static void AddMaximumViolation(
        List<string> incompatibilities,
        IReadOnlyDictionary<string, string> parameters,
        string key,
        string label,
        int? requested)
    {
        if (!parameters.TryGetValue(key, out var raw))
        {
            return;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum)
            || maximum < 0)
        {
            incompatibilities.Add($"declaration '{key}' must be a non-negative integer");
            return;
        }

        if (requested is { } value && value > maximum)
        {
            incompatibilities.Add($"requested {label} {value} exceeds declared maximum {maximum}");
        }
    }

    private static bool ExceedsLocalThreshold(GpResourceProfile resources, GpWorkloadPlacementOptions options)
        => resources.Vcpus > options.MaxLowLatencyLocalVcpus
            || resources.MemoryMib > options.MaxLowLatencyLocalMemoryMib
            || resources.GpuCount > options.MaxLowLatencyLocalGpuCount
            || resources.TimeoutSeconds > options.MaxLowLatencyLocalTimeoutSeconds
            || resources.RetryAttempts > options.MaxLowLatencyLocalRetryAttempts
            || resources.EphemeralGib > options.MaxLowLatencyLocalEphemeralGib;

    private static bool IsCustomCode(ExecutionJobDefinition workload)
        => string.Equals(workload.RuntimeProfile, CustomCode.CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal);

    private static int ReadPriority(ExecutionJobDefinition workload)
        => workload.Parameters.TryGetValue(GpWorkloadPlacementParameterKeys.Priority, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority)
                ? priority
                : 100;

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim().ToLowerInvariant()
            : null;

    private static HashSet<string> ReadSet(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static ExecutionPlacementDecision BuildDecision(
        ExecutionJobDefinition workload,
        string runtimeProfile,
        GpResourceProfile resources,
        GpWorkloadPlacementOptions options,
        string reasonCode,
        string reason,
        bool fallback)
        => new()
        {
            PolicyVersion = options.PolicyVersion,
            WorkloadId = workload.WorkloadId,
            Backend = workload.Backend,
            TargetKind = workload.TargetKind,
            RuntimeProfile = runtimeProfile,
            ReasonCode = reasonCode,
            Reason = reason,
            FallbackApplied = fallback,
            Resources = resources.ToExecutionRequirements(),
        };

    private static GeoprocessingAdmissionException NoCompatibleWorkload(
        PlacementIntent intent,
        IReadOnlyList<PlacementCandidate> candidates)
    {
        var details = candidates.Count == 0
            ? "No ordinary geoprocessing workloads are configured."
            : string.Join(
                "; ",
                candidates.Select(candidate => $"{candidate.Workload.WorkloadId}: "
                    + (candidate.Incompatibilities.Count == 0
                        ? "compatible but excluded by the required placement"
                        : string.Join(", ", candidate.Incompatibilities))));

        return new GeoprocessingAdmissionException(
            ExecutionAdmissionOutcome.Denied,
            ExecutionAdmissionDimension.Backpressure,
            "gp:no-compatible-workload",
            $"No execution workload can satisfy the per-job placement/runtime/resource request. {intent.Reason} {details}",
            retryAfterSeconds: 30);
    }

    private sealed record PlacementCandidate(
        ExecutionJobDefinition Workload,
        bool IsLocal,
        bool IsPressured,
        int Priority,
        IReadOnlyList<string> Incompatibilities)
    {
        public bool IsCompatible => Incompatibilities.Count == 0;
    }

    private sealed record PlacementIntent(
        bool PreferLocal,
        string? RequiredClass,
        string? RequiredBackend,
        string ReasonCode,
        string Reason);
}
