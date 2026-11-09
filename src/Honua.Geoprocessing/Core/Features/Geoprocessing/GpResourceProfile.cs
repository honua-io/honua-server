// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal sealed record GpResourceProfile
{
    public static readonly GpResourceProfile Empty = new();

    public int? Vcpus { get; init; }

    public int? MemoryMib { get; init; }

    public int? GpuCount { get; init; }

    public int? TimeoutSeconds { get; init; }

    public int? RetryAttempts { get; init; }

    // Ephemeral storage selects a job-definition tier because the submit call cannot override it.
    public int? EphemeralGib { get; init; }

    // Architecture is a job-definition property, not a submit override, and the current backend selects tiers by ephemeral storage instead.
    public string? Arch { get; init; }

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

    // The batch keys are literals so this type does not reference the optionally excluded Honua.Aws assembly.
    internal const string BatchVcpusKey = "batch.vcpus";
    internal const string BatchMemoryMibKey = "batch.memory_mib";
    internal const string BatchGpuCountKey = "batch.gpu_count";
    internal const string BatchTimeoutSecondsKey = "batch.timeout_seconds";
    internal const string BatchRetryAttemptsKey = "batch.retry_attempts";
    internal const string BatchEphemeralGibKey = "batch.ephemeral_gib";
    internal const string BatchArchKey = "batch.arch";

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

    internal static JobTimeoutPolicy? ResolveTimeoutPolicy(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var raw = parameters.GetValueOrDefault(BatchTimeoutSecondsKey)
            ?? parameters.GetValueOrDefault(TimeoutSecondsRequestKey);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
            ? new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(seconds) }
            : null;
    }

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

    // The per-job profile is written set-if-absent before workload defaults, so an explicit batch key is kept and per-job sizing wins.
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
