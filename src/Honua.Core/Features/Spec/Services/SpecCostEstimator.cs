// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Best-effort, catalog-only cost estimator. The S1 implementation derives a
/// rough shape from upstream plan nodes plus operator heuristics; it never
/// invokes the compute backend.
/// </summary>
internal sealed class SpecCostEstimator : ISpecCostEstimator
{
    private const long DefaultBytesPerRow = 512;
    private const double DefaultMsPerThousandRows = 5.0;

    private readonly IProcessCatalog _processCatalog;
    private readonly SpecCostEstimatorOptions _options;

    public SpecCostEstimator(IProcessCatalog processCatalog, SpecCostEstimatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(processCatalog);
        _processCatalog = processCatalog;
        _options = options ?? new SpecCostEstimatorOptions();
    }

    public Task<SpecCostEstimationResult> EstimateAsync(
        CanonicalSpecNode node,
        IReadOnlyDictionary<string, SpecPlanNode> resolvedDependencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(resolvedDependencies);

        var warnings = new List<SpecWarning>();
        long? rows = null;
        long? bytes = null;

        foreach (var dep in resolvedDependencies.Values)
        {
            if (dep.Cost.EstimatedRows is long r)
            {
                rows = Math.Max(rows ?? 0, r);
            }

            if (dep.Cost.EstimatedBytes is long b)
            {
                bytes = Math.Max(bytes ?? 0, b);
            }
        }

        if (node.Op is not null)
        {
            var process = _processCatalog.GetProcess(node.Op);
            if (process is null)
            {
                // Not a catalog op — fall through to heuristic.
            }
            else
            {
                var (rowMultiplier, byteMultiplier) = EstimateProcessMultipliers(process);
                if (rows is long baseRows)
                {
                    rows = (long)(baseRows * rowMultiplier);
                }

                if (bytes is long baseBytes)
                {
                    bytes = (long)(baseBytes * byteMultiplier);
                }
            }
        }

        if (rows is null && bytes is null)
        {
            rows = _options.DefaultRowsWhenUnknown;
        }

        if (bytes is null && rows is long rowFallback)
        {
            bytes = rowFallback * DefaultBytesPerRow;
        }

        double? durationMs = null;
        if (rows is long rowsForDuration)
        {
            durationMs = rowsForDuration / 1000.0 * DefaultMsPerThousandRows;
        }

        if (bytes is long b2 && b2 > _options.OversizeThresholdBytes)
        {
            warnings.Add(new SpecWarning
            {
                Code = SpecDiagnosticCodes.EstimatedOversize,
                Message = $"Estimated output exceeds {_options.OversizeThresholdBytes:N0} bytes.",
                Severity = SpecDiagnosticSeverity.Warning,
                NodeId = node.Id,
                Remedy = "Narrow the spatial extent or sample the input to reduce the projected output."
            });
        }

        if (node.Nondeterministic)
        {
            warnings.Add(new SpecWarning
            {
                Code = SpecDiagnosticCodes.NondeterministicOp,
                Message = $"Node '{node.Id}' declares a non-deterministic operator; cached results remain addressable but reruns may diverge without a pin.",
                Severity = SpecDiagnosticSeverity.Info,
                NodeId = node.Id,
                Remedy = "Pin the sampling seed or source snapshot to stabilise reruns."
            });
        }

        return Task.FromResult(new SpecCostEstimationResult
        {
            Estimate = new SpecCostEstimate
            {
                EstimatedRows = rows,
                EstimatedBytes = bytes,
                EstimatedDurationMs = durationMs
            },
            Warnings = warnings
        });
    }

    private static (double RowMultiplier, double ByteMultiplier) EstimateProcessMultipliers(
        Honua.Core.Features.Geoprocessing.Domain.ProcessDefinition process)
    {
        // Category-level heuristics are intentionally coarse; we just want a
        // plausible shape, not a accurate prediction.
        return process.Category switch
        {
            "geometry" => (1.0, 1.2),
            "analytics" => (0.1, 0.3),
            "summary" => (0.05, 0.1),
            _ => (1.0, 1.0)
        };
    }
}

/// <summary>
/// Options controlling the S1 heuristic estimator.
/// </summary>
public sealed record SpecCostEstimatorOptions
{
    /// <summary>
    /// Row count assumed when no upstream provides one. Keeps downstream
    /// cost heuristics bounded rather than propagating nulls indefinitely.
    /// </summary>
    public long DefaultRowsWhenUnknown { get; init; } = 1_000;

    /// <summary>
    /// Output byte threshold above which the estimator emits an
    /// <c>estimated-oversize</c> warning. Defaults to 1 GiB.
    /// </summary>
    public long OversizeThresholdBytes { get; init; } = 1L * 1024 * 1024 * 1024;
}
