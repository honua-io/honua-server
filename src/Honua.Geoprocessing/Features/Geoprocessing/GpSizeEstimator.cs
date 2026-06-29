// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Best-effort, deliberately-honest output-size / cost heuristic for
/// <c>honua gp plan</c> (GP Devkit P5, issue #2126). Given the caller's input
/// payload size and the process's declared category / output kind, it estimates
/// the output magnitude so the operator can be WARNED before submit when a run
/// is likely to blow the 50 MiB <see cref="GeoprocessingExecutorOptions.MaxArtifactBytes"/>
/// cap (the real failure mode — the job FAILS at that cap today).
///
/// <para>
/// HONEST LIMITS: this is a rough multiplier on the input bytes, NOT a guarantee.
/// It cannot know geometry vertex complexity, output compression (e.g. a COG /
/// GeoTIFF that compresses well, or a GeoJSON that balloons), the operation's
/// real selectivity (a tight clip vs. a wide buffer), or the live feature count
/// behind a layer reference. A scalar result (area/length/count) is always tiny;
/// a feature/raster op roughly tracks its input. Treat the number as an order of
/// magnitude, surfaced precisely so a serverless sizing step can budget Batch.
/// </para>
/// </summary>
internal static class GpSizeEstimator
{
    /// <summary>
    /// Produces the heuristic estimate for <paramref name="definition"/> against a
    /// caller input of <paramref name="callerInputBytes"/> raw bytes, judged
    /// against <paramref name="maxArtifactBytes"/>.
    /// </summary>
    /// <param name="definition">The process whose output magnitude is estimated.</param>
    /// <param name="callerInputBytes">
    /// Raw size, in bytes, of the file-like input the caller supplied (the decoded
    /// length of <c>--input</c>). Zero when no file-like input was bound.
    /// </param>
    /// <param name="maxArtifactBytes">The configured per-artifact byte cap.</param>
    /// <returns>The heuristic size / cost estimate.</returns>
    public static GpSizeEstimate Estimate(
        ProcessDefinition definition,
        long callerInputBytes,
        long maxArtifactBytes)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var primaryOutput = definition.OutputArtifactKinds.Count > 0
            ? definition.OutputArtifactKinds[0]
            : ArtifactKind.File;

        var isRasterClass = IsRasterClass(definition);
        var longRunning = isRasterClass;

        // A scalar/report output (area, length, count, statistics) is a handful of
        // bytes regardless of input size — never a cap risk.
        if (primaryOutput is ArtifactKind.Scalar or ArtifactKind.Report)
        {
            return new GpSizeEstimate
            {
                InputBytes = callerInputBytes,
                EstimatedOutputBytes = callerInputBytes > 0 ? ScalarOutputBytes : null,
                MaxArtifactBytes = maxArtifactBytes,
                Basis = "scalar/report output is constant-size regardless of input",
                LongRunning = longRunning,
            };
        }

        // With no file-like input to anchor on (e.g. a layer-scoped analytics op
        // whose live feature count is unknown offline), we cannot size the output
        // honestly — report no estimate rather than a fabricated one.
        if (callerInputBytes <= 0)
        {
            return new GpSizeEstimate
            {
                InputBytes = 0,
                EstimatedOutputBytes = null,
                MaxArtifactBytes = maxArtifactBytes,
                Basis = "no file-like input supplied; output magnitude cannot be estimated offline",
                LongRunning = longRunning,
            };
        }

        var (multiplier, basis) = OutputMultiplier(definition, primaryOutput, isRasterClass);

        // Guard the multiply against overflow on absurd inputs.
        var scaled = (double)callerInputBytes * multiplier;
        var estimatedOutput = scaled >= long.MaxValue ? long.MaxValue : (long)scaled;

        return new GpSizeEstimate
        {
            InputBytes = callerInputBytes,
            EstimatedOutputBytes = estimatedOutput,
            MaxArtifactBytes = maxArtifactBytes,
            Basis = basis,
            LongRunning = longRunning,
        };
    }

    // A scalar artifact (a GeoJSON Feature carrying one number, or a small JSON
    // stats document) is on the order of a few hundred bytes.
    private const long ScalarOutputBytes = 512;

    /// <summary>
    /// True when <paramref name="definition"/> is a raster/surface-class process — heavier and
    /// potentially long-running, so both the size estimate and the serverless
    /// <see cref="GpResourceProfile"/> default treat it as the largest tier. Shared so the size
    /// heuristic and the resource-profile derivation classify a process identically.
    /// </summary>
    internal static bool IsRasterClass(ProcessDefinition definition)
    {
        if (definition.OutputArtifactKinds.Contains(ArtifactKind.Raster))
        {
            return true;
        }

        return definition.Category is "raster" or "surface"
            || definition.ProcessId.StartsWith("raster.", StringComparison.Ordinal)
            || definition.ProcessId.StartsWith("surface.", StringComparison.Ordinal)
            || definition.ProcessId.StartsWith("conversion.raster", StringComparison.Ordinal);
    }

    /// <summary>
    /// Picks the per-operation output multiplier and a one-line basis. The
    /// multipliers are coarse by design: shrinking ops (simplify, dissolve, dedup,
    /// clip) come in below 1x; growing ops (buffer densification, WKB→GeoJSON
    /// text expansion) come in above 1x; format conversions hover near 1x.
    /// </summary>
    private static (double Multiplier, string Basis) OutputMultiplier(
        ProcessDefinition definition,
        ArtifactKind primaryOutput,
        bool isRasterClass)
    {
        var id = definition.ProcessId;

        // Raster/surface: output pixel count tracks the input raster; reprojection
        // and resampling can inflate, derived surfaces stay ~1x the source grid.
        if (isRasterClass)
        {
            if (id.Contains("reproject", StringComparison.Ordinal)
                || id.Contains("resample", StringComparison.Ordinal))
            {
                return (1.5, "raster reproject/resample roughly tracks input pixels, with headroom for grid inflation");
            }

            return (1.0, "raster/surface output roughly tracks the input pixel count");
        }

        // Geometry / feature text expansion: a WKB→GeoJSON or feature-layer output
        // is text and runs noticeably larger than compact binary input.
        var producesFeatureText = primaryOutput is ArtifactKind.FeatureLayer or ArtifactKind.File;

        // Shrinking operations: fewer/​smaller features than the input.
        if (id.Contains("simplify", StringComparison.Ordinal)
            || id.Contains("dissolve", StringComparison.Ordinal)
            || id.Contains("dedup", StringComparison.Ordinal)
            || id.Contains("clip", StringComparison.Ordinal)
            || id.Contains("union", StringComparison.Ordinal)
            || id.Contains("difference", StringComparison.Ordinal)
            || id.Contains("filter", StringComparison.Ordinal))
        {
            // Still text-expanded relative to WKB input, but the op removes data,
            // so net it sits around parity rather than growing.
            return (1.0, "generalization/filter op removes data but text-expands geometry; ~parity with input");
        }

        // Buffer densifies geometry (more vertices) and emits polygons from points/
        // lines — the canonical growth case.
        if (id.Contains("buffer", StringComparison.Ordinal))
        {
            return (5.0, "buffer densifies geometry and emits polygon output; estimate ~5x the input bytes (heuristic)");
        }

        if (producesFeatureText)
        {
            return (4.0, "feature/geometry text output (GeoJSON) typically expands compact binary input ~3-5x (heuristic)");
        }

        // Tables / other file outputs: assume near parity.
        return (1.5, "tabular/file output assumed near input size with modest formatting overhead (heuristic)");
    }
}
