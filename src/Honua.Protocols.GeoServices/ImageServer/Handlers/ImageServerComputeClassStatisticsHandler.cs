// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Computes the ImageServer <c>computeClassStatistics</c> operation: per-class statistical
/// signatures (pixel count, per-band mean vector, band-by-band covariance, and per-band summaries)
/// over the training AOIs supplied in <c>classDescriptions</c>. Reads the AOI pixels through the
/// shared <see cref="IRasterStore"/> analytics path (the same clip+merge primitive
/// <c>computeStatisticsHistograms</c> uses) rather than a parallel raster reader, and folds them
/// into signatures via the protocol-neutral <see cref="IRasterClassStatisticsAnalyzer"/>.
/// </summary>
internal sealed class ImageServerComputeClassStatisticsHandler
{
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly IRasterClassStatisticsAnalyzer _analyzer;
    private readonly ImageServerClassStatisticsOptions _options;
    private readonly ILogger<ImageServerComputeClassStatisticsHandler> _logger;

    public ImageServerComputeClassStatisticsHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        IRasterClassStatisticsAnalyzer analyzer,
        IOptions<ImageServerClassStatisticsOptions> options,
        ILogger<ImageServerComputeClassStatisticsHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Computes class signatures for the layer. The caller has already validated GET/POST request
    /// shape (<c>classDescriptions</c> present and well-formed) and merged query/body values.
    /// </summary>
    public async Task<IResult> ComputeAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "compute-class-statistics",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "compute-class-statistics");

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            // Class signatures describe raw source pixels; a renderingRule (stretch/colormap/band
            // math) would change the sampled values, so reject it explicitly rather than silently
            // reporting a rendered signature. A multidimensional slice selection is likewise not
            // supported by this operation.
            if (!string.IsNullOrWhiteSpace(GetString(values, "renderingRule")))
            {
                ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, "renderingRule not supported");
                return StandardErrorHelpers.CreateNotImplemented(
                    context,
                    "renderingRule is not supported for computeClassStatistics; signatures are computed on source pixels.");
            }

            if (!string.IsNullOrWhiteSpace(GetString(values, "multidimensionalDefinition")))
            {
                ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, "multidimensionalDefinition not supported");
                return StandardErrorHelpers.CreateNotImplemented(
                    context,
                    "multidimensionalDefinition is not supported for computeClassStatistics.");
            }

            var requestSrid = ImageServerGeometryHelpers.TryParseSrid(GetString(values, "sr"));
            if (!ImageServerClassDescriptions.TryParse(
                    GetString(values, "classDescriptions") ?? string.Empty, requestSrid, out var parsedClasses, out var classesError))
            {
                ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, classesError ?? "Invalid classDescriptions");
                return StandardErrorHelpers.CreateBadRequest(context, classesError ?? "Invalid classDescriptions.");
            }

            if (parsedClasses.Count > _options.MaxClasses)
            {
                ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, "too many classes");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"classDescriptions contains {parsedClasses.Count} classes, exceeding the limit of {_options.MaxClasses}.");
            }

            if (!TryParseBandIds(GetString(values, "bandIds"), out var bands, out var bandIdsError))
            {
                ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, bandIdsError ?? "Invalid bandIds");
                return StandardErrorHelpers.CreateBadRequest(context, bandIdsError ?? "Invalid bandIds.");
            }

            if (!TryParseRasterIds(GetString(values, "rasterIds"), out var requestedRasterIds, out var rasterIdsError))
            {
                ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, rasterIdsError ?? "Invalid rasterIds");
                return StandardErrorHelpers.CreateBadRequest(context, rasterIdsError ?? "Invalid rasterIds.");
            }

            if (!ImageServerMosaicHelpers.TryParseTime(GetString(values, "time"), out var timestamp, out var timeError))
            {
                ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, timeError ?? "Invalid time");
                return StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time.");
            }

            var editionError = ImageServerMosaicHelpers.RequireTemporalMosaicAccess(context, timestamp);
            if (editionError != null)
            {
                return editionError;
            }

            // Resolve the rasters analysed. Explicit rasterIds select catalog objects; otherwise the
            // whole layer mosaic at the requested instant is used. The AOI clip is applied per class
            // inside the store, so raster selection stays layer-scoped here.
            long[] rasterIds;
            if (requestedRasterIds is { Length: > 0 })
            {
                // codeql[cs/linq/missed-where] -- predicate binds state or awaits; retain imperative control flow.
                foreach (var id in requestedRasterIds)
                {
                    if (await _rasterStore.GetRasterInfoAsync(layerId, id, cancellationToken).ConfigureAwait(false) is null)
                    {
                        ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, $"rasterId {id} not found");
                        return StandardErrorHelpers.CreateBadRequest(context, $"rasterId '{id}' not found in layer.");
                    }
                }

                rasterIds = requestedRasterIds;
            }
            else
            {
                var selected = await _rasterStore.QueryRastersAsync(
                    layerId,
                    new RasterSelectionQuery { Timestamp = timestamp },
                    cancellationToken).ConfigureAwait(false);
                if (selected.Length == 0)
                {
                    ImageServerLog.NoRastersFound(_logger, layerId);
                    return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
                }

                rasterIds = selected.Select(r => r.Id).ToArray();
            }

            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, GetString(values, "mosaicRule"));

            var request = new RasterClassStatisticsRequest
            {
                LayerId = layerId,
                RasterIds = rasterIds,
                MergeStrategy = mergeStrategy,
                Bands = bands,
                MaxPixelsPerClass = _options.MaxPixelsPerClass,
                Classes = parsedClasses
                    .Select(c => new RasterClassAoi
                    {
                        ClassId = c.ClassId,
                        Name = c.Name,
                        ClipGeometry = c.ClipGeometry,
                        ClipSrid = c.ClipSrid,
                    })
                    .ToArray(),
            };

            RasterClassStatisticsResult result;
            try
            {
                result = await _analyzer.ComputeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (RasterClassStatisticsAoiTooLargeException tooLarge)
            {
                ImageServerLog.InvalidClassStatisticsParameters(_logger, layerId, tooLarge.Message);
                return StandardErrorHelpers.CreateBadRequest(context, tooLarge.Message);
            }

            var response = new ComputeClassStatisticsResponse
            {
                ClassStatistics = result.Signatures.Select(ToEntry).ToArray(),
            };

            ImageServerLog.ClassStatisticsComputed(_logger, layerId, response.ClassStatistics.Length);
            scope.SetSuccess(response.ClassStatistics.Length);
            return Results.Json(response, ImageServerJsonContext.Default.ComputeClassStatisticsResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImageServerLog.ClassStatisticsFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while computing class statistics.");
        }
    }

    private static ClassStatisticEntry ToEntry(RasterClassSignature signature)
    {
        var bandCount = signature.Bands.Length;
        var min = new double[bandCount];
        var max = new double[bandCount];
        var stdDev = new double[bandCount];
        for (var i = 0; i < bandCount; i++)
        {
            min[i] = signature.BandSummaries[i].Min;
            max[i] = signature.BandSummaries[i].Max;
            stdDev[i] = signature.BandSummaries[i].StandardDeviation;
        }

        return new ClassStatisticEntry
        {
            ClassId = signature.ClassId,
            Name = signature.Name,
            Count = signature.PixelCount,
            Bands = signature.Bands,
            Mean = signature.Mean,
            Min = min,
            Max = max,
            StandardDeviation = stdDev,
            CovarianceMatrix = signature.Covariance,
        };
    }

    /// <summary>Parses the optional <c>bandIds</c> parameter as 1-based band indices (CSV or JSON array).</summary>
    private static bool TryParseBandIds(string? raw, out int[]? bands, out string? error)
    {
        bands = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    error = "bandIds must be a JSON array of 1-based band indices.";
                    return false;
                }

                var values = new List<int>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Number || !element.TryGetInt32(out var band) || band <= 0)
                    {
                        error = "bandIds entries must be positive integers.";
                        return false;
                    }

                    values.Add(band);
                }

                bands = values.Count == 0 ? null : values.ToArray();
                return true;
            }
            catch (System.Text.Json.JsonException)
            {
                error = "bandIds must be valid JSON.";
                return false;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var band) || band <= 0)
            {
                error = "bandIds entries must be positive integers.";
                return false;
            }

            parsed.Add(band);
        }

        bands = parsed.Count == 0 ? null : parsed.ToArray();
        return true;
    }

    /// <summary>Parses the optional <c>rasterIds</c> parameter as catalog object ids (CSV or JSON array).</summary>
    private static bool TryParseRasterIds(string? raw, out long[]? rasterIds, out string? error)
    {
        rasterIds = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    error = "rasterIds must be a JSON array of catalog object IDs.";
                    return false;
                }

                var values = new List<long>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Number || !element.TryGetInt64(out var id) || id <= 0)
                    {
                        error = "rasterIds entries must be positive catalog object IDs.";
                        return false;
                    }

                    values.Add(id);
                }

                rasterIds = values.Count == 0 ? null : values.ToArray();
                return true;
            }
            catch (System.Text.Json.JsonException)
            {
                error = "rasterIds must be valid JSON.";
                return false;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                error = "rasterIds entries must be positive catalog object IDs.";
                return false;
            }

            parsed.Add(id);
        }

        rasterIds = parsed.Count == 0 ? null : parsed.ToArray();
        return true;
    }

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
