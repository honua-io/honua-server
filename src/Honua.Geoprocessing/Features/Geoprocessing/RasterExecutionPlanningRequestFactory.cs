// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Geoprocessing;

/// <summary>Builds metadata-only planner inputs from a validated single-step analysis plan.</summary>
internal static class RasterExecutionPlanningRequestFactory
{
    private const long InlineDecodedExpansionFactor = 64;
    private const long BytesPerSample = 8;
    private const long ScratchExpansionFactor = 2;

    public static RasterExecutionPlanningRequest Create(
        AnalysisPlan plan,
        RasterProcessCapability process,
        RasterExecutionPlannerOptions options,
        bool remoteBackendAvailable,
        string? remoteBackend)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);

        var step = plan.Steps.Single();
        var sources = step.RasterSources
            .OrderBy(source => source.Key, StringComparer.Ordinal)
            .Select(source => source.Value)
            .ToArray();

        var legacyResidency = ResolveLegacyResidency(step);
        var residencies = sources.Length == 0
            ? new[] { legacyResidency }
            : sources.Select(ToResidency).ToArray();
        var mediaTypes = sources.Length == 0
            ? new[] { ResolveLegacyInputMediaType(process) }
            : sources.Select(source => source.Content.MediaType).ToArray();

        return new RasterExecutionPlanningRequest
        {
            ProcessId = process.ProcessId,
            InputResidencies = residencies,
            InputMediaTypes = mediaTypes,
            OutputSink = RasterOutputSink.JobArtifact,
            Cost = BuildCost(step, sources),
            Budgets = options.ToBudgetSnapshot(),
            Health = new RasterExecutionHealthSnapshot
            {
                Version = options.HealthSnapshotVersion,
                Database = options.DatabaseHealth,
                LocalNativeWorkerAvailable = options.LocalNativeWorkerAvailable,
                RemoteNativeBackendAvailable = options.RemoteNativeBackendEnabled && remoteBackendAvailable,
                RemoteBackend = options.RemoteNativeBackendEnabled && remoteBackendAvailable
                    ? remoteBackend
                    : null,
            },
            Policy = options.ToPolicySnapshot(),
            // The durable job submission path never executes native work in the web request.
            // Request-envelope callers can construct their own request with this flag enabled.
            AllowRequestExecution = false,
        };
    }

    private static RasterCostEstimatorInput BuildCost(
        AnalysisPlanStep step,
        RasterSourceDescriptor[] sources)
    {
        var sourceCount = Math.Max(sources.Length, 1);
        var bandCount = TrySumBands(sources);
        var inputPixels = TrySumSelectedPixels(sources);
        long? decodedBytes;

        if (inputPixels is { } pixels && bandCount is { } bands)
        {
            decodedBytes = SaturatingMultiply(SaturatingMultiply(pixels, bands), BytesPerSample);
        }
        else if (TryGetBoundedInlineBytes(step, sources) is { } inlineBytes)
        {
            bandCount ??= 1;
            decodedBytes = SaturatingMultiply(inlineBytes, InlineDecodedExpansionFactor);
            inputPixels ??= Math.Max(decodedBytes.Value / BytesPerSample, 1);
        }
        else
        {
            decodedBytes = null;
        }

        var outputPixels = TryReadOutputPixels(step) ?? inputPixels;
        long? scratchBytes = decodedBytes is { } decoded
            ? SaturatingMultiply(decoded, ScratchExpansionFactor)
            : null;
        long? databaseWork = inputPixels is { } input && bandCount is { } bandTotal
            ? SaturatingMultiply(input, bandTotal)
            : null;

        return new RasterCostEstimatorInput
        {
            SourceCount = sourceCount,
            BandCount = bandCount,
            ZoneCount = ResolveZoneCount(step),
            InputPixels = inputPixels,
            OutputPixels = outputPixels,
            DecodedBytes = decodedBytes,
            ExpectedScratchBytes = scratchBytes,
            ExpectedDatabaseWork = databaseWork,
        };
    }

    private static RasterInputResidency ToResidency(RasterSourceDescriptor source) => source switch
    {
        PostgisRasterSourceDescriptor => RasterInputResidency.Postgis,
        ObjectStoreCogRasterSourceDescriptor => RasterInputResidency.ObjectStoreCog,
        ObjectStoreZarrRasterSourceDescriptor => RasterInputResidency.ObjectStoreZarr,
        StagedArtifactRasterSourceDescriptor => RasterInputResidency.StagedArtifact,
        InlineRasterSourceDescriptor => RasterInputResidency.Inline,
        _ => throw new ArgumentException(
            $"Unsupported raster source descriptor '{source.GetType().Name}'.",
            nameof(source)),
    };

    private static string ResolveLegacyInputMediaType(RasterProcessCapability process)
        => process.Engines
            .SelectMany(engine => engine.Formats.InputMediaTypes)
            .First();

    private static RasterInputResidency ResolveLegacyResidency(AnalysisPlanStep step)
    {
        if (step.Inputs.TryGetValue("source", out var source) && !string.IsNullOrWhiteSpace(source))
        {
            return RasterInputResidency.Inline;
        }

        // The legacy layerId/rasterId path resolves through the registered COG catalog.
        // Classify the reference from request metadata before that compatibility path reads
        // object bytes into the eventual native-worker spec.
        if (HasPositiveLong(step, "rasterId") || HasNonNegativeInt(step, "layerId"))
        {
            return RasterInputResidency.ObjectStoreCog;
        }

        return RasterInputResidency.Inline;
    }

    private static long? TrySumBands(RasterSourceDescriptor[] sources)
    {
        if (sources.Length == 0)
        {
            return 1;
        }

        long total = 0;
        foreach (var source in sources)
        {
            if (source is InlineRasterSourceDescriptor && source.Selection?.Bands.Count is not > 0)
            {
                total = SaturatingAdd(total, 1);
                continue;
            }

            if (source.Selection?.Bands.Count is not > 0)
            {
                return null;
            }

            total = SaturatingAdd(total, source.Selection.Bands.Count);
        }

        return total;
    }

    private static long? TrySumSelectedPixels(RasterSourceDescriptor[] sources)
    {
        if (sources.Length == 0)
        {
            return null;
        }

        long total = 0;
        foreach (var source in sources)
        {
            if (source.Selection?.PixelWindow is not { } window)
            {
                return null;
            }

            total = SaturatingAdd(total, SaturatingMultiply(window.Width, window.Height));
        }

        return total;
    }

    private static long? TryGetBoundedInlineBytes(
        AnalysisPlanStep step,
        RasterSourceDescriptor[] sources)
    {
        if (sources.Length > 0)
        {
            return sources.All(source => source is InlineRasterSourceDescriptor)
                ? sources.Aggregate(
                    0L,
                    static (total, source) => SaturatingAdd(total, source.Content.SizeBytes))
                : null;
        }

        if (!step.Inputs.TryGetValue("source", out var encoded) || string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        // Metadata-only upper bound for legacy base64. Validation/worker decoding remains
        // authoritative; the planner never allocates or decodes the payload.
        return SaturatingMultiply(encoded.Length, 3) / 4;
    }

    private static long? TryReadOutputPixels(AnalysisPlanStep step)
    {
        if (TryReadPositiveLong(step, "width", out var width)
            && TryReadPositiveLong(step, "height", out var height))
        {
            return SaturatingMultiply(width, height);
        }

        if (TryReadPositiveLong(step, "targetWidth", out width)
            && TryReadPositiveLong(step, "targetHeight", out height))
        {
            return SaturatingMultiply(width, height);
        }

        return null;
    }

    private static long? ResolveZoneCount(AnalysisPlanStep step)
    {
        if (!string.Equals(step.ProcessId, "raster.zonal-statistics", StringComparison.Ordinal))
        {
            return 0;
        }

        return TryReadPositiveLong(step, "zoneCount", out var zoneCount) ? zoneCount : null;
    }

    private static bool TryReadPositiveLong(AnalysisPlanStep step, string key, out long value)
    {
        value = 0;
        return step.Inputs.TryGetValue(key, out var raw)
            && long.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value)
            && value > 0;
    }

    private static bool HasPositiveLong(AnalysisPlanStep step, string key)
        => TryReadPositiveLong(step, key, out _);

    private static bool HasNonNegativeInt(AnalysisPlanStep step, string key)
        => step.Inputs.TryGetValue(key, out var raw)
            && int.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            && value >= 0;

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long left, long right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }
}
