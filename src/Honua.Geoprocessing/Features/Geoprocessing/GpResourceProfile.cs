// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Typed, per-job geoprocessing resource profile — vCPU, memory, GPU, timeout, retry,
/// ephemeral (scratch) storage, and CPU architecture — that the GP job spec carries so the
/// canonical job runtime can size a serverless backend (AWS Batch today) PER JOB.
/// </summary>
/// <remarks>
/// <para>
/// This is the server leg of the corrected serverless-GP design (honua-server#2165): per-job
/// sizing is RUNTIME and instant. The profile maps onto the durable
/// <see cref="ExecutionJobSpec.Parameters"/> bag under the <c>batch.*</c> contract keys that
/// <c>AwsBatchComputeBackend.StartAsync</c> already consumes — vCPU / memory / timeout / retry /
/// GPU become <c>SubmitJob</c> overrides, and the ephemeral need selects a job-definition TIER
/// from the pre-registered honua-iac pool (honua-iac#70). There is no per-job terraform and no
/// devops-agent call in the job path.
/// </para>
/// <para>
/// The effective profile is the heaviest catalog-derived default across a plan's steps
/// (<see cref="ForProcess"/> + <see cref="MergeMax"/>), then overridden field-by-field by any
/// explicit per-job request values (<see cref="FromRequestParameters"/> +
/// <see cref="OverrideWith"/>). Defaults are deliberately conservative HEURISTICS, mirroring the
/// honest tone of <see cref="GpSizeEstimator"/> — an operator can always pin exact values with the
/// <c>gp.resource.*</c> request keys.
/// </para>
/// <para>
/// The <c>batch.*</c> keys are written as literal contract strings so this type takes no hard
/// dependency on the optionally-excluded <c>Honua.Aws</c> assembly, mirroring the
/// <c>ExecutionWorkloadGate</c> pattern.
/// </para>
/// </remarks>
internal sealed record GpResourceProfile
{
    /// <summary>An empty profile that contributes no sizing (the no-hint baseline).</summary>
    public static readonly GpResourceProfile Empty = new();

    /// <summary>Requested vCPU count, or <see langword="null"/> to leave the backend/job-def default.</summary>
    public int? Vcpus { get; init; }

    /// <summary>Requested memory in MiB, or <see langword="null"/> for the backend/job-def default.</summary>
    public int? MemoryMib { get; init; }

    /// <summary>Requested GPU count, or <see langword="null"/>/0 for a CPU-only job.</summary>
    public int? GpuCount { get; init; }

    /// <summary>Per-job attempt timeout in seconds, or <see langword="null"/> for the default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Per-job retry attempts, or <see langword="null"/> for the default.</summary>
    public int? RetryAttempts { get; init; }

    /// <summary>
    /// Ephemeral (scratch) storage need in GiB. This is the one knob <c>SubmitJob</c> cannot
    /// override, so it selects a job-definition tier from the honua-iac pool rather than an override.
    /// </summary>
    public int? EphemeralGib { get; init; }

    /// <summary>
    /// CPU architecture hint (for example <c>x86_64</c> or <c>arm64</c>). Architecture is a
    /// job-definition/image property, not a <c>SubmitJob</c> override, so it is carried for the
    /// substrate's arch/image tier contract; the current backend selects tiers by ephemeral need
    /// only and ignores it until the iac pool exposes arch variants.
    /// </summary>
    public string? Arch { get; init; }

    /// <summary>True when the profile carries no sizing values at all.</summary>
    public bool IsEmpty =>
        Vcpus is null
        && MemoryMib is null
        && GpuCount is null
        && TimeoutSeconds is null
        && RetryAttempts is null
        && EphemeralGib is null
        && string.IsNullOrWhiteSpace(Arch);

    // Per-job request keys — the GP-job-facing contract the submitter/AI sets. Decoupled from the
    // AWS-Batch param keys so the same profile can target other serverless backends later.
    internal const string VcpusRequestKey = "gp.resource.vcpus";
    internal const string MemoryMibRequestKey = "gp.resource.memory_mib";
    internal const string GpuCountRequestKey = "gp.resource.gpu_count";
    internal const string TimeoutSecondsRequestKey = "gp.resource.timeout_seconds";
    internal const string RetryAttemptsRequestKey = "gp.resource.retry_attempts";
    internal const string EphemeralGibRequestKey = "gp.resource.ephemeral_gib";
    internal const string ArchRequestKey = "gp.resource.arch";

    // AWS Batch parameter contract keys (literal — see remarks). These mirror AwsBatchParameterKeys
    // in the Honua.Aws assembly, which the backend reads back.
    internal const string BatchVcpusKey = "batch.vcpus";
    internal const string BatchMemoryMibKey = "batch.memory_mib";
    internal const string BatchGpuCountKey = "batch.gpu_count";
    internal const string BatchTimeoutSecondsKey = "batch.timeout_seconds";
    internal const string BatchRetryAttemptsKey = "batch.retry_attempts";
    internal const string BatchEphemeralGibKey = "batch.ephemeral_gib";
    internal const string BatchArchKey = "batch.arch";

    // Provider-specific projections for the other provider-neutral compute targets. Fixed-pool
    // dimensions (for example Azure vCPU/memory and local-process host capacity) are validated
    // through placement.* workload declarations rather than invented as backend overrides.
    internal const string KubernetesCpuRequestKey = "k8s.cpu_request";
    internal const string KubernetesCpuLimitKey = "k8s.cpu_limit";
    internal const string KubernetesMemoryRequestKey = "k8s.memory_request";
    internal const string KubernetesMemoryLimitKey = "k8s.memory_limit";
    internal const string KubernetesEphemeralStorageRequestKey = "k8s.ephemeral_storage_request";
    internal const string KubernetesEphemeralStorageLimitKey = "k8s.ephemeral_storage_limit";
    internal const string KubernetesActiveDeadlineSecondsKey = "k8s.active_deadline_seconds";
    internal const string AzureRetryAttemptsKey = "azure.batch.max_task_retry_count";
    internal const string AzureTimeoutMinutesKey = "azure.batch.task_timeout_minutes";

    private static readonly (string BackendKey, string RequestKey)[] BackendResourceAliases =
    [
        (BatchVcpusKey, VcpusRequestKey),
        (BatchMemoryMibKey, MemoryMibRequestKey),
        (BatchGpuCountKey, GpuCountRequestKey),
        (BatchTimeoutSecondsKey, TimeoutSecondsRequestKey),
        (BatchRetryAttemptsKey, RetryAttemptsRequestKey),
        (BatchEphemeralGibKey, EphemeralGibRequestKey),
        (BatchArchKey, ArchRequestKey),
        (KubernetesCpuRequestKey, VcpusRequestKey),
        (KubernetesCpuLimitKey, VcpusRequestKey),
        (KubernetesMemoryRequestKey, MemoryMibRequestKey),
        (KubernetesMemoryLimitKey, MemoryMibRequestKey),
        (KubernetesEphemeralStorageRequestKey, EphemeralGibRequestKey),
        (KubernetesEphemeralStorageLimitKey, EphemeralGibRequestKey),
        (KubernetesActiveDeadlineSecondsKey, TimeoutSecondsRequestKey),
        (AzureRetryAttemptsKey, RetryAttemptsRequestKey),
        (AzureTimeoutMinutesKey, TimeoutSecondsRequestKey),
    ];

    // Conservative per-class default tiers (heuristic). Ephemeral GiB values line up with the
    // honua-iac job-definition pool ceilings (s=20, m=50, l=100).
    private const int ManagedVcpus = 1;
    private const int ManagedMemoryMib = 2048;
    private const int ManagedEphemeralGib = 20;

    private const int NativeVcpus = 2;
    private const int NativeMemoryMib = 4096;
    private const int NativeEphemeralGib = 50;

    private const int RasterVcpus = 4;
    private const int RasterMemoryMib = 8192;
    private const int RasterEphemeralGib = 100;
    private const int RasterTimeoutSeconds = 3600;

    /// <summary>
    /// Derives the conservative default profile for a single catalog process from its class:
    /// raster/surface processes (heavier, potentially long-running) get the largest default tier,
    /// native (out-of-process GDAL) processes a mid tier, and ordinary managed processes the
    /// smallest tier. Pure data-driven defaults; explicit request values always win via
    /// <see cref="OverrideWith"/>.
    /// </summary>
    public static GpResourceProfile ForProcess(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (GpSizeEstimator.IsRasterClass(definition))
        {
            return new GpResourceProfile
            {
                Vcpus = RasterVcpus,
                MemoryMib = RasterMemoryMib,
                EphemeralGib = RasterEphemeralGib,
                TimeoutSeconds = RasterTimeoutSeconds,
            };
        }

        var profile = RuntimeProfiles.Normalize(definition.RuntimeProfile);
        if (string.Equals(profile, RuntimeProfiles.Native, StringComparison.Ordinal))
        {
            return new GpResourceProfile
            {
                Vcpus = NativeVcpus,
                MemoryMib = NativeMemoryMib,
                EphemeralGib = NativeEphemeralGib,
            };
        }

        return new GpResourceProfile
        {
            Vcpus = ManagedVcpus,
            MemoryMib = ManagedMemoryMib,
            EphemeralGib = ManagedEphemeralGib,
        };
    }

    /// <summary>
    /// Reads an explicit per-job profile from the <c>gp.resource.*</c> request parameters. Absent,
    /// blank, non-numeric, or non-positive numeric values are ignored (leaving the field unset).
    /// </summary>
    public static GpResourceProfile FromRequestParameters(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return new GpResourceProfile
        {
            Vcpus = ReadPositiveInt(parameters, VcpusRequestKey),
            MemoryMib = ReadPositiveInt(parameters, MemoryMibRequestKey),
            GpuCount = ReadNonNegativeInt(parameters, GpuCountRequestKey),
            TimeoutSeconds = ReadPositiveInt(parameters, TimeoutSecondsRequestKey),
            RetryAttempts = ReadPositiveInt(parameters, RetryAttemptsRequestKey),
            EphemeralGib = ReadPositiveInt(parameters, EphemeralGibRequestKey),
            Arch = ReadString(parameters, ArchRequestKey),
        };
    }

    /// <summary>
    /// Rejects provider-specific sizing supplied by an ordinary GP request. Those values otherwise
    /// bypass the provider-neutral profile used for workload compatibility while still winning
    /// set-if-absent projection at submission time. Workload-owned provider defaults are merged
    /// later and remain valid; callers express per-job requirements through <c>gp.resource.*</c>.
    /// </summary>
    public static void RejectBackendResourceOverrides(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        foreach (var (backendKey, requestKey) in BackendResourceAliases)
        {
            if (parameters.ContainsKey(backendKey))
            {
                throw new GeoprocessingValidationException(
                    $"Backend resource override '{backendKey}' is not accepted in ordinary geoprocessing requests; use '{requestKey}'.");
            }
        }
    }

    /// <summary>
    /// Aggregates two catalog-derived profiles by taking the heavier value of each dimension, so a
    /// single heavy step in a multi-step plan sizes the whole job. Architecture takes the
    /// other profile's value when set (the later step in the fold).
    /// </summary>
    public GpResourceProfile MergeMax(GpResourceProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new GpResourceProfile
        {
            Vcpus = Max(Vcpus, other.Vcpus),
            MemoryMib = Max(MemoryMib, other.MemoryMib),
            GpuCount = Max(GpuCount, other.GpuCount),
            TimeoutSeconds = Max(TimeoutSeconds, other.TimeoutSeconds),
            RetryAttempts = Max(RetryAttempts, other.RetryAttempts),
            EphemeralGib = Max(EphemeralGib, other.EphemeralGib),
            Arch = !string.IsNullOrWhiteSpace(other.Arch) ? other.Arch : Arch,
        };
    }

    /// <summary>
    /// Overlays <paramref name="overrides"/> field-by-field: any value set on the override wins,
    /// otherwise the current value is kept. Used to let an explicit per-job request profile win over
    /// the catalog-derived defaults.
    /// </summary>
    public GpResourceProfile OverrideWith(GpResourceProfile overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        return new GpResourceProfile
        {
            Vcpus = overrides.Vcpus ?? Vcpus,
            MemoryMib = overrides.MemoryMib ?? MemoryMib,
            GpuCount = overrides.GpuCount ?? GpuCount,
            TimeoutSeconds = overrides.TimeoutSeconds ?? TimeoutSeconds,
            RetryAttempts = overrides.RetryAttempts ?? RetryAttempts,
            EphemeralGib = overrides.EphemeralGib ?? EphemeralGib,
            Arch = !string.IsNullOrWhiteSpace(overrides.Arch) ? overrides.Arch : Arch,
        };
    }

    /// <summary>
    /// Projects the set fields onto the durable spec parameter bag under the <c>batch.*</c> contract
    /// keys the AWS Batch backend reads. Uses set-if-absent semantics so an explicit raw
    /// <c>batch.*</c> value already on the bag (or a workload default merged earlier) is never
    /// clobbered; callers project the per-job profile BEFORE merging workload defaults so per-job
    /// sizing wins over the workload baseline.
    /// </summary>
    public void ProjectOnto(IDictionary<string, string> specParams)
    {
        ArgumentNullException.ThrowIfNull(specParams);

        Set(specParams, BatchVcpusKey, Vcpus);
        Set(specParams, BatchMemoryMibKey, MemoryMib);
        Set(specParams, BatchGpuCountKey, GpuCount);
        Set(specParams, BatchTimeoutSecondsKey, TimeoutSeconds);
        Set(specParams, BatchRetryAttemptsKey, RetryAttempts);
        Set(specParams, BatchEphemeralGibKey, EphemeralGib);

        if (!string.IsNullOrWhiteSpace(Arch))
        {
            specParams.TryAdd(BatchArchKey, Arch);
        }
    }

    /// <summary>
    /// Projects dynamic dimensions understood by the selected provider. Dimensions that cannot be
    /// overridden at submission time remain in <see cref="ExecutionPlacementDecision.Resources"/>
    /// and are admitted only when the workload's declared capacity can satisfy them.
    /// </summary>
    public void ProjectOnto(IDictionary<string, string> specParams, BatchComputeTargetKind targetKind)
    {
        ArgumentNullException.ThrowIfNull(specParams);

        switch (targetKind)
        {
            case BatchComputeTargetKind.AwsBatch:
                ProjectOnto(specParams);
                break;
            case BatchComputeTargetKind.KubernetesJob:
                if (Vcpus is { } vcpus)
                {
                    var cpu = vcpus.ToString(CultureInfo.InvariantCulture);
                    specParams.TryAdd(KubernetesCpuRequestKey, cpu);
                    specParams.TryAdd(KubernetesCpuLimitKey, cpu);
                }

                if (MemoryMib is { } memoryMib)
                {
                    var memory = memoryMib.ToString(CultureInfo.InvariantCulture) + "Mi";
                    specParams.TryAdd(KubernetesMemoryRequestKey, memory);
                    specParams.TryAdd(KubernetesMemoryLimitKey, memory);
                }

                if (EphemeralGib is { } ephemeralGib)
                {
                    var ephemeralStorage = ephemeralGib.ToString(CultureInfo.InvariantCulture) + "Gi";
                    specParams.TryAdd(KubernetesEphemeralStorageRequestKey, ephemeralStorage);
                    specParams.TryAdd(KubernetesEphemeralStorageLimitKey, ephemeralStorage);
                }

                Set(specParams, KubernetesActiveDeadlineSecondsKey, TimeoutSeconds);
                break;
            case BatchComputeTargetKind.AzureBatch:
                if (RetryAttempts is { } totalAttempts)
                {
                    // The canonical profile counts the initial attempt; Azure's property counts
                    // only retries after that first execution.
                    Set(specParams, AzureRetryAttemptsKey, Math.Max(0, totalAttempts - 1));
                }

                if (TimeoutSeconds is { } timeoutSeconds)
                {
                    var minutes = Math.Max(1, (int)Math.Ceiling(timeoutSeconds / 60d));
                    Set(specParams, AzureTimeoutMinutesKey, minutes);
                }

                break;
            case BatchComputeTargetKind.LocalProcess:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetKind), targetKind, "Unsupported batch target kind.");
        }
    }

    /// <summary>Converts the internal selection profile to its durable provider-neutral snapshot.</summary>
    public ExecutionResourceRequirements ToExecutionRequirements() => new()
    {
        Vcpus = Vcpus,
        MemoryMib = MemoryMib,
        GpuCount = GpuCount,
        TimeoutSeconds = TimeoutSeconds,
        RetryAttempts = RetryAttempts,
        EphemeralGib = EphemeralGib,
        Architecture = Arch,
    };

    private static void Set(IDictionary<string, string> specParams, string key, int? value)
    {
        if (value.HasValue)
        {
            specParams.TryAdd(key, value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int? Max(int? a, int? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return Math.Max(a.Value, b.Value);
    }

    private static int? ReadPositiveInt(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var value = ReadInt(parameters, key);
        return value is > 0 ? value : null;
    }

    private static int? ReadNonNegativeInt(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var value = ReadInt(parameters, key);
        return value is >= 0 ? value : null;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (parameters.TryGetValue(key, out var raw)
            && !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ReadString(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;
}
