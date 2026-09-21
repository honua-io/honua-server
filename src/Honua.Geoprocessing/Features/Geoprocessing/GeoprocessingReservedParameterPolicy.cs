// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Geoprocessing.CustomCode;

namespace Honua.Geoprocessing;

/// <summary>
/// Defines which job-spec parameter namespaces are owned by the server, the operator's workload
/// definition, or a compute backend, and therefore are not part of the request contract of the
/// shared geoprocessing submit path.
/// </summary>
/// <remarks>
/// <para>
/// Protocol metadata handed to <see cref="GeoprocessingJobService"/> is copied onto the durable
/// <c>ExecutionJobSpec.Parameters</c> bag, which is also where the operator's workload definition,
/// the per-job resource projection, the admission stamps, the custom-code gate and the compute
/// backends keep their own keys. The shared submit path therefore refuses request metadata in
/// those namespaces for every adapter, and stamps its own keys only after that check. Adapters
/// that forward caller-named values must carry them under an adapter-owned prefix
/// (<c>analysis.content.runtime_parameter.*</c>, <c>workflow.parameter.*</c>, ...).
/// </para>
/// <para>
/// The narrow exceptions are keys that ARE the declared request contract of the shared path:
/// the adapter-stamped <c>process.output.&lt;n&gt;</c> output bindings, the typed per-job sizing
/// request keys, the custom-code submit inputs (validated by the custom-code gate), the legacy
/// declared-scope key (which can only attenuate), and the orchestration engine's step metadata on
/// the inherited-submitter lane.
/// </para>
/// </remarks>
internal static class GeoprocessingReservedParameterPolicy
{
    private const string OrchestrationPrefix = "orchestration.";

    /// <summary>
    /// Namespaces that request metadata may not use. Matched case-insensitively so a differently
    /// cased spelling is refused rather than carried.
    /// </summary>
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

    /// <summary>Exact request keys inside a reserved namespace that the shared path accepts.</summary>
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

    /// <summary>
    /// Returns the first request metadata key that uses a reserved namespace, or <c>null</c> when
    /// the metadata is acceptable.
    /// </summary>
    /// <param name="protocolMetadata">The adapter-supplied protocol metadata.</param>
    /// <param name="inheritedSubmitterLane">
    /// <c>true</c> on the lane that inherits a persisted submitter security context (the
    /// orchestration engine's step submissions and approved-proposal resumes), the only lane whose
    /// metadata is built by the server and may carry <c>orchestration.*</c> step keys.
    /// </param>
    public static string? FindReservedKey(
        IReadOnlyDictionary<string, string>? protocolMetadata,
        bool inheritedSubmitterLane)
    {
        if (protocolMetadata is null)
        {
            return null;
        }

        foreach (var key in protocolMetadata.Keys)
        {
            if (IsReserved(key, inheritedSubmitterLane))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="key"/> is in a reserved namespace and is not one of
    /// the accepted request keys.
    /// </summary>
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
