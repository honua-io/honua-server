// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.ProcessRelease;

namespace Honua.Geoprocessing;

// Caller metadata in a server, workload, or backend namespace is refused so a request cannot set those keys.
internal static class GeoprocessingReservedParameterPolicy
{
    private const string OrchestrationPrefix = "orchestration.";

    private static readonly string[] ReservedPrefixes =
    [
        "process.",
        "env.",
        "batch.",
        "k8s.",
        "azure.",
        "admission.",
        "customcode.",
        "gp.resource.",
        OrchestrationPrefix,
        "honua."
    ];

    private static readonly FrozenSet<string> AcceptedRequestKeys = new[]
    {
        // Custom-code submit inputs; validated, clamped and authorized by the custom-code gate.
        CustomCodeJobContract.RuntimeParam,
        CustomCodeJobContract.RepoUrlParam,
        CustomCodeJobContract.GitRefParam,
        CustomCodeJobContract.EntrypointParam,
        CustomCodeJobContract.DepsManifestParam,
        CustomCodeJobContract.ParamsJsonParam,
        CustomCodeJobContract.DeclaredScopeParam,
        // Logical process whose live release binding admits the job (honua-server#6221).
        GpProcessReleaseAdmission.ProcessParam,
        // Legacy declared scope; validated to be within the submitter's own reach.
        CustomCodeOwnerScopeCapture.DeclaredScopeMetadataKey,
        // Typed per-job sizing request; parsed field-by-field by GpResourceProfile.
        GpResourceProfile.VcpusRequestKey,
        GpResourceProfile.MemoryMibRequestKey,
        GpResourceProfile.GpuCountRequestKey,
        GpResourceProfile.TimeoutSecondsRequestKey,
        GpResourceProfile.RetryAttemptsRequestKey,
        GpResourceProfile.EphemeralGibRequestKey,
        GpResourceProfile.ArchRequestKey
    }.ToFrozenSet(StringComparer.Ordinal);

    public static string? FindReservedKey(
        IReadOnlyDictionary<string, string>? protocolMetadata,
        bool inheritedSubmitterLane)
    {
        if (protocolMetadata is null)
        {
            return null;
        }

        return protocolMetadata.Keys.FirstOrDefault(key => IsReserved(key, inheritedSubmitterLane));
    }

    public static bool IsReserved(string key, bool inheritedSubmitterLane)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Spec parameter keys are matched verbatim downstream, so surrounding whitespace is never
        // meaningful; normalise it away here so a padded spelling cannot sidestep the prefix match.
        var candidate = key.Trim();
        var reservedPrefix = Array.Find(
            ReservedPrefixes,
            prefix => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (reservedPrefix is null)
        {
            return false;
        }

        if (AcceptedRequestKeys.Contains(key) || IsOutputBindingKey(key))
        {
            return false;
        }

        return !(inheritedSubmitterLane
            && key.StartsWith(OrchestrationPrefix, StringComparison.Ordinal));
    }

    private static bool IsOutputBindingKey(string key)
    {
        if (!key.StartsWith(GeoprocessingProtocolMetadataKeys.OutputNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var index = key.AsSpan(GeoprocessingProtocolMetadataKeys.OutputNamePrefix.Length);
        return !index.IsEmpty && !index.ContainsAnyExceptInRange('0', '9');
    }
}
