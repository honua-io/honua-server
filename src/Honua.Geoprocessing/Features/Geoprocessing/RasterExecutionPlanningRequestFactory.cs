// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Geoprocessing;

/// <summary>Builds metadata-only planner inputs from a validated single-step analysis plan.</summary>
internal static class RasterExecutionPlanningRequestFactory
{
    private const long OpaquePayloadExpansionFactor = 64;
    private const long BytesPerSample = 8;
    private const long ScratchExpansionFactor = 2;
    private const long GdalGridDefaultDimension = 256;

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
        var legacy = sources.Length == 0 ? BuildLegacyMetadata(step) : null;
        var sourceCount = legacy?.SourceCount ?? sources.Length;
        var bandCount = legacy?.BandCount ?? TrySumBands(sources);
        var inputPixels = legacy?.InputPixels ?? TrySumSelectedPixels(sources);
        var sampleBytes = legacy?.SampleBytes ?? TryMaxInlineSampleBytes(sources) ?? BytesPerSample;
        var outputPixels = TryReadOutputPixels(step) ?? TryDeriveResampleOutputPixels(step, sources);
        if (outputPixels is null && !RequiresDerivedOutputGrid(step))
        {
            outputPixels = legacy?.DefaultOutputPixels ?? inputPixels;
        }

        long? decodedBytes = legacy?.DecodedBytes;

        if (decodedBytes is null && inputPixels is { } pixels && bandCount is { } bands)
        {
            decodedBytes = SaturatingMultiply(SaturatingMultiply(pixels, bands), sampleBytes);
        }

        if (decodedBytes is not null && outputPixels is { } output && bandCount is { } outputBands)
        {
            var outputBytes = SaturatingMultiply(
                SaturatingMultiply(output, outputBands),
                sampleBytes);
            decodedBytes = Math.Max(decodedBytes.Value, outputBytes);
        }

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

    private static LegacyRasterMetadata BuildLegacyMetadata(AnalysisPlanStep step)
    {
        var processId = step.ProcessId;
        if (string.Equals(processId, "raster.mosaic", StringComparison.Ordinal)
            || string.Equals(processId, "raster.map-algebra", StringComparison.Ordinal))
        {
            var payloads = ReadSeparatedPayloads(step, "sources");
            return BuildRasterPayloadMetadata(payloads);
        }

        if (string.Equals(processId, "raster.spectral-index", StringComparison.Ordinal))
        {
            var payloads = ResolveSpectralRoleNames(step)
                .Select(role => ReadInput(step, role))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
            return BuildRasterPayloadMetadata(payloads);
        }

        if (string.Equals(processId, "raster.interpolate-idw", StringComparison.Ordinal)
            || string.Equals(processId, "raster.interpolate-kriging", StringComparison.Ordinal))
        {
            var pointBytes = TryGetEncodedPayloadBytes(ReadInput(step, "points"));
            long? expandedBytes = pointBytes is { } bytes
                ? SaturatingMultiply(bytes, OpaquePayloadExpansionFactor)
                : null;
            long? pointUnits = pointBytes is { } payloadBytes ? Math.Max(payloadBytes, 1) : null;
            return new LegacyRasterMetadata(
                SourceCount: 1,
                BandCount: 1,
                InputPixels: pointUnits,
                DecodedBytes: expandedBytes,
                DefaultOutputPixels: SaturatingMultiply(GdalGridDefaultDimension, GdalGridDefaultDimension));
        }

        if (string.Equals(processId, "conversion.rasterize", StringComparison.Ordinal))
        {
            // The source is vector GeoJSON, not a compressed raster. Its bounded encoded length
            // can conservatively represent input work, but the cell-size branch still leaves
            // output dimensions incomplete until a worker derives them from the spatial envelope.
            var vectorBytes = TryGetEncodedPayloadBytes(ReadInput(step, "source"));
            long? expandedBytes = vectorBytes is { } bytes
                ? SaturatingMultiply(bytes, OpaquePayloadExpansionFactor)
                : null;
            long? inputUnits = expandedBytes is { } decoded
                ? Math.Max(decoded / BytesPerSample, 1)
                : null;
            return new LegacyRasterMetadata(1, 1, inputUnits, expandedBytes, null);
        }

        var source = ReadInput(step, "source");
        return BuildRasterPayloadMetadata(string.IsNullOrWhiteSpace(source) ? [] : [source]);
    }

    private static LegacyRasterMetadata BuildRasterPayloadMetadata(string[] payloads)
    {
        if (payloads.Length == 0)
        {
            return new LegacyRasterMetadata(1, 1, null, null, null);
        }

        long bandCount = 0;
        long inputPixels = 0;
        long decodedBytes = 0;
        long sampleBytes = BytesPerSample;
        foreach (var payload in payloads)
        {
            if (!InlineRasterMetadataReader.TryReadBase64(payload, out var metadata))
            {
                return new LegacyRasterMetadata(payloads.Length, null, null, null, null);
            }

            var pixels = SaturatingMultiply(metadata.Width, metadata.Height);
            bandCount = SaturatingAdd(bandCount, metadata.Bands);
            inputPixels = SaturatingAdd(inputPixels, pixels);
            sampleBytes = Math.Max(sampleBytes, metadata.SampleBytes);
            decodedBytes = SaturatingAdd(
                decodedBytes,
                SaturatingMultiply(SaturatingMultiply(pixels, metadata.Bands), metadata.SampleBytes));
        }

        var sourceCount = payloads.Length;
        return new LegacyRasterMetadata(
            SourceCount: sourceCount,
            BandCount: bandCount,
            InputPixels: inputPixels,
            DecodedBytes: decodedBytes,
            DefaultOutputPixels: null,
            SampleBytes: sampleBytes);
    }

    private static string[] ReadSeparatedPayloads(AnalysisPlanStep step, string key)
        => ReadInput(step, key)?.Split(
            '|',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

    private static string[] ResolveSpectralRoleNames(AnalysisPlanStep step)
    {
        var index = ReadInput(step, "index")?.Trim().ToUpperInvariant();
        return index switch
        {
            "NDVI" or "SAVI" => ["nir", "red"],
            "NDWI" => ["green", "nir"],
            "NDBI" => ["swir", "nir"],
            "EVI" => ["nir", "red", "blue"],
            _ => ["red", "nir", "green", "swir", "blue"],
        };
    }

    private static string? ReadInput(AnalysisPlanStep step, string key)
        => step.Inputs.TryGetValue(key, out var value) ? value : null;

    private static long? TryGetEncodedPayloadBytes(string? encoded)
        => string.IsNullOrWhiteSpace(encoded)
            ? null
            : SaturatingMultiply(encoded.Length, 3) / 4;

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

        // The legacy layerId/rasterId compatibility path is materialized onto the canonical
        // inline 'source' before the durable planner runs. Classify its execution residency,
        // not the backing catalog's storage implementation, so native local/remote placement
        // remains based on the payload the worker actually consumes.
        if (HasPositiveLong(step, "rasterId") || HasNonNegativeInt(step, "layerId"))
        {
            return RasterInputResidency.Inline;
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
            if (source is InlineRasterSourceDescriptor inline
                && source.Selection?.Bands.Count is not > 0)
            {
                if (!InlineRasterMetadataReader.TryRead(inline.Payload, out var metadata))
                {
                    return null;
                }

                total = SaturatingAdd(total, metadata.Bands);
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
            if (source.Selection?.PixelWindow is { } window)
            {
                total = SaturatingAdd(total, SaturatingMultiply(window.Width, window.Height));
                continue;
            }

            if (source is not InlineRasterSourceDescriptor inline
                || !InlineRasterMetadataReader.TryRead(inline.Payload, out var metadata))
            {
                return null;
            }

            total = SaturatingAdd(total, SaturatingMultiply(metadata.Width, metadata.Height));
        }

        return total;
    }

    private static long? TryMaxInlineSampleBytes(RasterSourceDescriptor[] sources)
    {
        if (sources.Length == 0)
        {
            return null;
        }

        long maximum = BytesPerSample;
        foreach (var source in sources)
        {
            if (source is not InlineRasterSourceDescriptor inline
                || !InlineRasterMetadataReader.TryRead(inline.Payload, out var metadata))
            {
                return null;
            }

            maximum = Math.Max(maximum, metadata.SampleBytes);
        }

        return maximum;
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

    private static bool RequiresDerivedOutputGrid(AnalysisPlanStep step)
        => string.Equals(step.ProcessId, "raster.mosaic", StringComparison.Ordinal)
            || string.Equals(step.ProcessId, "raster.resample", StringComparison.Ordinal)
            || (string.Equals(step.ProcessId, "conversion.rasterize", StringComparison.Ordinal)
                && step.Inputs.TryGetValue("cellSize", out var cellSize)
                && !string.IsNullOrWhiteSpace(cellSize));

    private static long? TryDeriveResampleOutputPixels(
        AnalysisPlanStep step,
        RasterSourceDescriptor[] sources)
    {
        if (!string.Equals(step.ProcessId, "raster.resample", StringComparison.Ordinal)
            || !TryReadPositiveDouble(step, "cellSize", out var targetScaleX))
        {
            return null;
        }

        var targetScaleY = targetScaleX;
        if (step.Inputs.ContainsKey("cellSizeY")
            && !TryReadPositiveDouble(step, "cellSizeY", out targetScaleY))
        {
            return null;
        }

        InlineRasterMetadata metadata;
        if (sources.Length == 0)
        {
            if (!InlineRasterMetadataReader.TryReadBase64(ReadInput(step, "source"), out metadata))
            {
                return null;
            }
        }
        else if (sources.Length == 1
            && sources[0] is InlineRasterSourceDescriptor inline
            && InlineRasterMetadataReader.TryRead(inline.Payload, out var typedMetadata))
        {
            metadata = typedMetadata;
        }
        else
        {
            return null;
        }

        if (metadata.PixelScaleX is not { } sourceScaleX
            || metadata.PixelScaleY is not { } sourceScaleY)
        {
            return null;
        }

        var outputWidth = TryScaleDimension(metadata.Width, sourceScaleX, targetScaleX);
        var outputHeight = TryScaleDimension(metadata.Height, sourceScaleY, targetScaleY);
        return outputWidth is { } width && outputHeight is { } height
            ? SaturatingMultiply(width, height)
            : null;
    }

    private static long? TryScaleDimension(long sourcePixels, double sourceScale, double targetScale)
    {
        var scaled = sourcePixels * sourceScale / targetScale;
        if (!double.IsFinite(scaled) || scaled <= 0 || scaled > long.MaxValue)
        {
            return null;
        }

        return Math.Max((long)Math.Ceiling(scaled), 1);
    }

    private static long? ResolveZoneCount(AnalysisPlanStep step)
    {
        if (!string.Equals(step.ProcessId, "raster.zonal-statistics", StringComparison.Ordinal))
        {
            return 0;
        }

        // The accepted contract carries base64 GeoJSON in 'zones', not a separate trusted
        // zoneCount. Without decoding in the web process, decoded payload bytes are a safe upper
        // bound on feature count (every feature occupies at least one byte). This completes the
        // cost vector without trusting a caller-supplied count or allocating the GeoJSON here;
        // the native worker still parses it and enforces exact feature/vertex caps.
        var zoneBytes = TryGetEncodedPayloadBytes(ReadInput(step, "zones"));
        return zoneBytes is { } bytes ? Math.Max(bytes, 1) : null;
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

    private static bool TryReadPositiveDouble(AnalysisPlanStep step, string key, out double value)
    {
        value = 0;
        return step.Inputs.TryGetValue(key, out var raw)
            && double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value)
            && double.IsFinite(value)
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

    private sealed record LegacyRasterMetadata(
        long SourceCount,
        long? BandCount,
        long? InputPixels,
        long? DecodedBytes,
        long? DefaultOutputPixels,
        long SampleBytes = RasterExecutionPlanningRequestFactory.BytesPerSample);
}
