// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Immutable built-in raster engine and cost registry. It describes capability only; engine and
/// placement selection remain the responsibility of the raster execution planner.
/// </summary>
public sealed partial class RasterEngineCapabilityRegistry : IRasterEngineCapabilityRegistry
{
    private const string SemanticVersion = "1.0.0";
    private const string PostgisUnavailableReason =
        "No canonical PostGIS raster IProcessExecutor is registered for this process; "
        + "PostGIS GP execution is tracked by honua-server#3095 and #3096.";
    private const string KrigingUnavailableReason =
        "No kriging-capable numerical backend is registered; the bundled GDAL worker does not "
        + "implement kriging and PostGIS has no canonical kriging executor.";

    private static readonly IReadOnlyList<string> GeoTiff = ReadOnly("image/tiff");
    private static readonly IReadOnlyList<string> GeoJson = ReadOnly("application/geo+json");
    private static readonly IReadOnlyList<string> Json = ReadOnly("application/json");
    private static readonly IReadOnlyList<string> RasterExportFormats =
        ReadOnly("image/tiff", "image/png", "image/jpeg");

    private readonly FrozenDictionary<string, RasterProcessCapability> _byProcessId;

    /// <summary>Creates the registry from the built-in capability roster.</summary>
    public RasterEngineCapabilityRegistry()
        : this(BuildBuiltIns())
    {
    }

    /// <summary>
    /// Creates a registry from an explicit descriptor set. Primarily useful for composition and
    /// contract tests; malformed or duplicate descriptors fail immediately.
    /// </summary>
    /// <param name="capabilities">Raster process capability descriptors.</param>
    public RasterEngineCapabilityRegistry(IEnumerable<RasterProcessCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var ordered = capabilities
            .OrderBy(capability => capability.ProcessId, StringComparer.Ordinal)
            .ToArray();
        ValidateCapabilities(ordered);

        Processes = Array.AsReadOnly(ordered);
        _byProcessId = ordered.ToFrozenDictionary(
            capability => capability.ProcessId,
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyList<RasterProcessCapability> Processes { get; }

    /// <inheritdoc />
    public RasterProcessCapability? Find(string processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        return _byProcessId.GetValueOrDefault(processId);
    }

    /// <inheritdoc />
    public RasterCostEstimate Estimate(
        string processId,
        RasterEngine engine,
        RasterCostEstimatorInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(input);

        var process = Find(processId)
            ?? throw new ArgumentException(
                $"Process '{processId}' has no raster engine capability metadata.",
                nameof(processId));
        var engineCapability = process.Engines.SingleOrDefault(candidate => candidate.Engine == engine)
            ?? throw new ArgumentException(
                $"Process '{processId}' has no capability metadata for engine '{engine}'.",
                nameof(engine));

        ValidateNonNegative(input.SourceCount, nameof(input.SourceCount), input);
        ValidateNonNegative(input.BandCount, nameof(input.BandCount), input);
        ValidateNonNegative(input.ZoneCount, nameof(input.ZoneCount), input);
        ValidateNonNegative(input.InputPixels, nameof(input.InputPixels), input);
        ValidateNonNegative(input.OutputPixels, nameof(input.OutputPixels), input);
        ValidateNonNegative(input.DecodedBytes, nameof(input.DecodedBytes), input);
        ValidateNonNegative(input.ExpectedScratchBytes, nameof(input.ExpectedScratchBytes), input);
        ValidateNonNegative(input.ExpectedDatabaseWork, nameof(input.ExpectedDatabaseWork), input);

        var unknown = new List<string>(capacity: 8);
        var sourceCount = Normalize(input.SourceCount, "sourceCount", unknown);
        var bandCount = Normalize(input.BandCount, "bandCount", unknown);
        var zoneCount = Normalize(input.ZoneCount, "zoneCount", unknown);
        var inputPixels = Normalize(input.InputPixels, "inputPixels", unknown);
        var outputPixels = Normalize(input.OutputPixels, "outputPixels", unknown);
        var decodedBytes = Normalize(input.DecodedBytes, "decodedBytes", unknown);
        var scratchBytes = Normalize(input.ExpectedScratchBytes, "expectedScratchBytes", unknown);
        var databaseWork = Normalize(input.ExpectedDatabaseWork, "expectedDatabaseWork", unknown);

        string? requestUnavailableReason = null;
        if (unknown.Count > 0)
        {
            requestUnavailableReason =
                "Raster metadata is incomplete; unknown cost inputs are conservatively saturated "
                + "and request execution is not eligible.";
        }
        else if (!engineCapability.IsAvailable)
        {
            requestUnavailableReason = engineCapability.UnavailabilityReason;
        }
        else if (!engineCapability.RequestExecutionAllowed)
        {
            requestUnavailableReason =
                $"Engine '{engine}' requires durable execution for process '{processId}'.";
        }

        return new RasterCostEstimate
        {
            ProcessId = processId,
            Engine = engine,
            SourceCount = sourceCount,
            BandCount = bandCount,
            ZoneCount = zoneCount,
            InputPixels = inputPixels,
            OutputPixels = outputPixels,
            DecodedBytes = decodedBytes,
            ExpectedScratchBytes = scratchBytes,
            ExpectedDatabaseWork = databaseWork,
            UnknownInputs = unknown.AsReadOnly(),
            RequestExecutionAllowed = requestUnavailableReason is null,
            RequestExecutionUnavailabilityReason = requestUnavailableReason,
        };
    }

    private static RasterProcessCapability[] BuildBuiltIns() =>
    [
        // Raster-bearing conversion processes. Broad external format conversion stays GDAL-first.
        Create("conversion.polygonize", "raster.polygonize", GeoTiff, GeoJson),
        Create("conversion.raster-format", "raster.format-convert", GeoTiff, RasterExportFormats),
        Create(
            "conversion.raster-reproject",
            "raster.reproject",
            GeoTiff,
            GeoTiff,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create("conversion.rasterize", "raster.rasterize", GeoJson, GeoTiff),

        // Legacy native warp remains a first-class raster catalog entry.
        Create("gdal.gdalwarp", "raster.reproject", GeoTiff, GeoTiff),

        // Raster proximity products.
        Create("proximity.euclidean-allocation", "raster.euclidean-allocation", GeoTiff, GeoTiff),
        Create("proximity.euclidean-distance", "raster.euclidean-distance", GeoTiff, GeoTiff),

        // Canonical raster processes.
        Create(
            "raster.clip",
            "raster.clip",
            GeoTiff,
            GeoTiff,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create(
            "raster.histogram",
            "raster.histogram",
            GeoTiff,
            Json,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create("raster.interpolate-idw", "raster.interpolate-idw", GeoJson, GeoTiff),
        Create(
            "raster.interpolate-kriging",
            "raster.interpolate-kriging",
            GeoJson,
            GeoTiff,
            gdalAvailable: false,
            gdalUnavailableReason: KrigingUnavailableReason),
        Create("raster.map-algebra", "raster.map-algebra", GeoTiff, GeoTiff),
        Create(
            "raster.mosaic",
            "raster.mosaic",
            GeoTiff,
            GeoTiff,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create("raster.reclassify", "raster.reclassify", GeoTiff, GeoTiff),
        Create(
            "raster.reproject",
            "raster.reproject",
            GeoTiff,
            GeoTiff,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create(
            "raster.resample",
            "raster.resample",
            GeoTiff,
            GeoTiff,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create("raster.spectral-index", "raster.spectral-index", GeoTiff, GeoTiff),
        Create(
            "raster.statistics",
            "raster.statistics",
            GeoTiff,
            Json,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create(
            "raster.zonal-statistics",
            "raster.zonal-statistics",
            ReadOnly("image/tiff", "application/geo+json"),
            Json,
            postgisPreferred: true,
            postgisRequestAllowed: true),

        // Surface analysis. The existing PostGIS primitives make the bounded core PostGIS-first;
        // executor availability remains false until the canonical GP lane lands.
        Create(
            "surface.aspect",
            "surface.aspect",
            GeoTiff,
            GeoTiff,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create("surface.contour", "surface.contour", GeoTiff, GeoJson),
        Create(
            "surface.hillshade",
            "surface.hillshade",
            GeoTiff,
            GeoTiff,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create("surface.roughness", "surface.roughness", GeoTiff, GeoTiff),
        Create("surface.rugosity-tpi", "surface.rugosity-tpi", GeoTiff, GeoTiff),
        Create("surface.rugosity-tri", "surface.rugosity-tri", GeoTiff, GeoTiff),
        Create(
            "surface.slope",
            "surface.slope",
            GeoTiff,
            GeoTiff,
            postgisPreferred: true,
            postgisRequestAllowed: true),
        Create("surface.viewshed", "surface.viewshed", GeoTiff, GeoTiff),
    ];

    private static RasterProcessCapability Create(
        string processId,
        string requiredCapability,
        IReadOnlyList<string> inputMediaTypes,
        IReadOnlyList<string> outputMediaTypes,
        bool postgisPreferred = false,
        bool postgisRequestAllowed = false,
        bool gdalAvailable = true,
        string? gdalUnavailableReason = null)
    {
        var postgisPreference = postgisPreferred
            ? RasterEngineDefaultPreference.Preferred
            : RasterEngineDefaultPreference.Fallback;
        var gdalPreference = postgisPreferred
            ? RasterEngineDefaultPreference.Fallback
            : RasterEngineDefaultPreference.Preferred;

        return new RasterProcessCapability
        {
            ProcessId = processId,
            SemanticVersion = SemanticVersion,
            Engines = ReadOnly(
                new RasterEngineCapability
                {
                    Engine = RasterEngine.Postgis,
                    ImplementationVersion = $"honua.postgis.{processId}@{SemanticVersion}",
                    RequiredCapabilities = ReadOnly(requiredCapability),
                    Formats = new RasterFormatRestrictions
                    {
                        InputMediaTypes = inputMediaTypes,
                        OutputMediaTypes = outputMediaTypes,
                    },
                    InputResidencies = ReadOnly(RasterInputResidency.Postgis),
                    OutputSinks = ReadOnly(RasterOutputSink.Postgis, RasterOutputSink.JobArtifact),
                    RequestExecutionAllowed = postgisRequestAllowed,
                    DefaultPreference = postgisPreference,
                    IsAvailable = false,
                    UnavailabilityReason = PostgisUnavailableReason,
                },
                new RasterEngineCapability
                {
                    Engine = RasterEngine.GdalNative,
                    ImplementationVersion = $"honua.gdal-native.{processId}@{SemanticVersion}",
                    RequiredCapabilities = ReadOnly(requiredCapability),
                    Formats = new RasterFormatRestrictions
                    {
                        InputMediaTypes = inputMediaTypes,
                        OutputMediaTypes = outputMediaTypes,
                    },
                    // Typed object/staged resolution is #3090. The current executor contract is
                    // deliberately honest: direct native reads support only bounded inline data.
                    InputResidencies = ReadOnly(RasterInputResidency.Inline),
                    OutputSinks = ReadOnly(RasterOutputSink.JobArtifact),
                    RequestExecutionAllowed = false,
                    DefaultPreference = gdalPreference,
                    IsAvailable = gdalAvailable,
                    UnavailabilityReason = gdalAvailable ? null : gdalUnavailableReason,
                }),
        };
    }

    private static void ValidateCapabilities(IReadOnlyList<RasterProcessCapability> capabilities)
    {
        var processIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in capabilities)
        {
            if (string.IsNullOrWhiteSpace(process.ProcessId))
            {
                throw new ArgumentException("Raster capability process IDs must be non-empty.", nameof(capabilities));
            }

            if (!processIds.Add(process.ProcessId))
            {
                throw new ArgumentException(
                    $"Duplicate raster capability process ID '{process.ProcessId}'.",
                    nameof(capabilities));
            }

            if (!SemanticVersionPattern().IsMatch(process.SemanticVersion))
            {
                throw new ArgumentException(
                    $"Raster process '{process.ProcessId}' has invalid semantic version "
                    + $"'{process.SemanticVersion}'.",
                    nameof(capabilities));
            }

            var engines = new HashSet<RasterEngine>();
            foreach (var engine in process.Engines)
            {
                if (!engines.Add(engine.Engine))
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' declares engine '{engine.Engine}' twice.",
                        nameof(capabilities));
                }

                if (string.IsNullOrWhiteSpace(engine.ImplementationVersion)
                    || engine.RequiredCapabilities.Count == 0
                    || engine.Formats.InputMediaTypes.Count == 0
                    || engine.Formats.OutputMediaTypes.Count == 0
                    || engine.InputResidencies.Count == 0
                    || engine.OutputSinks.Count == 0)
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' has incomplete '{engine.Engine}' metadata.",
                        nameof(capabilities));
                }

                if (engine.IsAvailable == !string.IsNullOrWhiteSpace(engine.UnavailabilityReason))
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' engine '{engine.Engine}' must carry an "
                        + "unavailability reason exactly when it is unavailable.",
                        nameof(capabilities));
                }
            }

            if (engines.Count != Enum.GetValues<RasterEngine>().Length)
            {
                throw new ArgumentException(
                    $"Raster process '{process.ProcessId}' must describe every known raster engine.",
                    nameof(capabilities));
            }
        }
    }

    private static long Normalize(long? value, string name, List<string> unknown)
    {
        if (value is { } known)
        {
            return known;
        }

        unknown.Add(name);
        return long.MaxValue;
    }

    private static void ValidateNonNegative(long? value, string fieldName, RasterCostEstimatorInput input)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                value,
                $"Raster estimator field '{fieldName}' cannot be negative.");
        }
    }

    private static ReadOnlyCollection<T> ReadOnly<T>(params T[] values)
        => Array.AsReadOnly(values);

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();
}
