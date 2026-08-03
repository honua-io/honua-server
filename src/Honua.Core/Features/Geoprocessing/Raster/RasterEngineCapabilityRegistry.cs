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
        "No available PostGIS raster provider capability is registered for this process.";
    private const string KrigingUnavailableReason =
        "No kriging-capable numerical backend is registered; the bundled GDAL worker does not "
        + "implement kriging and PostGIS has no canonical kriging executor.";

    private static readonly IReadOnlyList<string> GeoTiff = ReadOnly("image/tiff");
    private static readonly IReadOnlyList<string> GeoTiffAndWkb =
        ReadOnly("image/tiff", "application/wkb");
    private static readonly IReadOnlyList<string> GeoJson = ReadOnly("application/geo+json");
    private static readonly IReadOnlyList<string> Json = ReadOnly("application/json");
    private static readonly IReadOnlyList<string> DefaultRasterFormats =
        ReadOnly("image/tiff", "image/png", "image/jpeg");

    /// <summary>
    /// Default worker format names shared by worker option binding and serving-catalog
    /// projection so their no-override contracts cannot drift.
    /// </summary>
    public static IReadOnlyList<string> DefaultGdalRasterInputFormatNames { get; } =
        ReadOnly("TIFF", "PNG", "JPEG");

    /// <summary>
    /// Default GDAL driver denials shared by worker hardening and capability projection.
    /// This keeps every composition root honest about formats the isolated worker can open.
    /// </summary>
    public static IReadOnlyList<string> DefaultGdalSkippedDriverNames { get; } = ReadOnly(
        "VRT", "GTI", "DERIVED", "GDALG", "MRF",
        "WMS", "WMTS", "WCS", "HTTP", "STACIT", "STACTA",
        "OGR_VRT", "WFS", "OAPIF", "NGW", "PLMOSAIC", "EEDA", "EEDAI",
        "JP2OpenJPEG", "JP2ECW", "JP2KAK", "JP2MrSID", "JP2Lura", "JPEG2000",
        "GIF", "BIGGIF", "BMP", "HFA", "NITF", "ENVI", "RMF");

    private readonly FrozenDictionary<string, RasterProcessCapability> _byProcessId;

    /// <summary>Creates the registry from the built-in capability roster.</summary>
    public RasterEngineCapabilityRegistry()
        : this(BuildConfiguredBuiltIns(
            DefaultGdalRasterInputFormatNames,
            DefaultGdalSkippedDriverNames))
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

        var supplied = capabilities.ToArray();
        ValidateCapabilities(supplied);
        var ordered = supplied
            .Select(SnapshotCapability)
            .OrderBy(capability => capability.ProcessId, StringComparer.Ordinal)
            .ToArray();

        Processes = Array.AsReadOnly(ordered);
        _byProcessId = ordered.ToFrozenDictionary(
            capability => capability.ProcessId,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates the built-in registry with every native raster input projected from the
    /// worker's effective format allowlist and driver-denial policy.
    /// </summary>
    /// <param name="allowedRasterInputFormats">
    /// GDAL format names admitted by the worker, such as <c>TIFF</c>, <c>PNG</c>, or
    /// <c>JPEG2000</c>.
    /// </param>
    /// <param name="skippedGdalDrivers">
    /// GDAL driver short names disabled by worker hardening. When omitted, the restrictive
    /// default denial set is used.
    /// </param>
    /// <returns>A registry whose native raster-input metadata matches the effective policy.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the allowlist contains no recognized raster format, or when every bundled
    /// driver for an allowed format remains disabled by the hardening policy.
    /// </exception>
    public static RasterEngineCapabilityRegistry CreateForGdalRasterInputFormats(
        IEnumerable<string> allowedRasterInputFormats,
        IEnumerable<string>? skippedGdalDrivers = null)
    {
        ArgumentNullException.ThrowIfNull(allowedRasterInputFormats);
        return new RasterEngineCapabilityRegistry(BuildConfiguredBuiltIns(
            allowedRasterInputFormats,
            skippedGdalDrivers ?? DefaultGdalSkippedDriverNames));
    }

    /// <summary>
    /// Creates the built-in registry with PostGIS availability projected from provider-neutral
    /// executor discovery. Unknown processes, semantic drift, malformed declarations, and two
    /// providers claiming the same current semantic variant fail composition.
    /// </summary>
    /// <param name="providerCapabilities">Capabilities discovered from provider executors.</param>
    /// <param name="allowedRasterInputFormats">Effective native-worker raster input formats.</param>
    /// <param name="skippedGdalDrivers">Effective native-worker driver denial set.</param>
    /// <returns>A registry whose advertised PostGIS availability is backed by a real executor.</returns>
    public static RasterEngineCapabilityRegistry CreateForProviderCapabilities(
        IEnumerable<RasterProviderCapability> providerCapabilities,
        IEnumerable<string> allowedRasterInputFormats,
        IEnumerable<string>? skippedGdalDrivers = null)
    {
        ArgumentNullException.ThrowIfNull(providerCapabilities);
        ArgumentNullException.ThrowIfNull(allowedRasterInputFormats);

        var builtIns = BuildConfiguredBuiltIns(
            allowedRasterInputFormats,
            skippedGdalDrivers ?? DefaultGdalSkippedDriverNames);
        var discovered = providerCapabilities.ToArray();
        ValidateProviderCapabilities(discovered, builtIns);

        return new RasterEngineCapabilityRegistry(builtIns.Select(process => process with
        {
            Engines = process.Engines
                .Select(engine => ProjectProviderCapability(process, engine, discovered))
                .ToArray(),
        }));
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
        Create(
            "conversion.raster-format",
            "raster.format-convert",
            DefaultRasterFormats,
            DefaultRasterFormats),
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
            GeoTiffAndWkb,
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

    private static RasterProcessCapability[] BuildConfiguredBuiltIns(
        IEnumerable<string> allowedRasterInputFormats,
        IEnumerable<string> skippedGdalDrivers)
    {
        var formats = allowedRasterInputFormats
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .Select(ToGdalRasterInputFormat)
            .Where(format => format is not null)
            .Cast<GdalRasterInputFormat>()
            .DistinctBy(format => format.Name, StringComparer.Ordinal)
            .ToArray();
        if (formats.Length == 0)
        {
            throw new InvalidOperationException(
                "GdalWorker:AllowedRasterInputFormats must contain at least one recognized "
                + "raster format.");
        }

        var skipped = skippedGdalDrivers
            .Where(driver => !string.IsNullOrWhiteSpace(driver))
            .Select(driver => driver.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabledFormats = formats
            .Where(format => format.DriverNames.All(skipped.Contains))
            .Select(format => format.Name)
            .ToArray();
        if (disabledFormats.Length > 0)
        {
            throw new InvalidOperationException(
                $"GdalWorker:AllowedRasterInputFormats enables format(s) {string.Join(", ", disabledFormats)} "
                + "whose GDAL drivers are all disabled by GdalWorker:Hardening:SkipDrivers.");
        }

        var builtIns = BuildBuiltIns();
        var disabledOutputDrivers = builtIns
            .SelectMany(process => process.Engines
                .Where(engine => engine.Engine == RasterEngine.GdalNative && engine.IsAvailable)
                .SelectMany(engine => engine.Formats.OutputMediaTypes
                    .Select(ToGdalRasterOutputDriver)
                    .Where(driver => driver is not null)
                    .Cast<string>()
                    .Concat(AdditionalRequiredGdalOutputDrivers(process.ProcessId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(driver => (process.ProcessId, Driver: driver))))
            .Where(requirement => skipped.Contains(requirement.Driver))
            .OrderBy(requirement => requirement.Driver, StringComparer.Ordinal)
            .ThenBy(requirement => requirement.ProcessId, StringComparer.Ordinal)
            .ToArray();
        if (disabledOutputDrivers.Length > 0)
        {
            var detail = string.Join(
                ", ",
                disabledOutputDrivers.Select(requirement =>
                    $"{requirement.Driver} ({requirement.ProcessId})"));
            throw new InvalidOperationException(
                "GdalWorker:Hardening:SkipDrivers disables required raster output driver(s) "
                + $"{detail}; the affected executor capabilities cannot be advertised.");
        }

        var inputMediaTypes = formats
            .Select(format => format.MediaType)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return builtIns
            .Select(process => process with
            {
                Engines = process.Engines
                    .Select(engine => ProjectGdalRasterInputFormats(engine, inputMediaTypes))
                    .ToArray(),
            })
            .ToArray();
    }

    private static RasterEngineCapability ProjectGdalRasterInputFormats(
        RasterEngineCapability engine,
        IReadOnlyList<string> inputMediaTypes)
    {
        if (engine.Engine != RasterEngine.GdalNative
            || !engine.Formats.InputMediaTypes.Any(IsGdalRasterMediaType))
        {
            return engine;
        }

        var projected = inputMediaTypes
            .Concat(engine.Formats.InputMediaTypes.Where(mediaType => !IsGdalRasterMediaType(mediaType)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return engine with
        {
            Formats = engine.Formats with
            {
                InputMediaTypes = projected,
            },
        };
    }

    private static RasterEngineCapability ProjectProviderCapability(
        RasterProcessCapability process,
        RasterEngineCapability engine,
        IReadOnlyList<RasterProviderCapability> discovered)
    {
        if (engine.Engine != RasterEngine.Postgis)
        {
            return engine;
        }

        var capability = discovered.SingleOrDefault(candidate =>
            candidate.Engine == engine.Engine
            && string.Equals(candidate.Variant.ProcessId, process.ProcessId, StringComparison.Ordinal)
            && string.Equals(candidate.Variant.SemanticVersion, process.SemanticVersion, StringComparison.Ordinal));
        if (capability is null)
        {
            return engine;
        }

        var available = capability.Availability == RasterProviderAvailability.Available;
        return engine with
        {
            ProviderId = capability.ProviderId,
            ProviderPolicyVersion = capability.PolicyVersion,
            ImplementationVersion = capability.Variant.ImplementationVersion,
            IsAvailable = available,
            UnavailabilityReason = available ? null : capability.UnavailabilityReason,
            UnavailabilityIsRetryable = capability.Availability == RasterProviderAvailability.Unhealthy,
        };
    }

    private static void ValidateProviderCapabilities(
        IReadOnlyList<RasterProviderCapability> discovered,
        IReadOnlyList<RasterProcessCapability> builtIns)
    {
        var known = builtIns.ToDictionary(process => process.ProcessId, StringComparer.Ordinal);
        var routes = new HashSet<(RasterEngine Engine, string ProcessId, string SemanticVersion)>();
        foreach (var capability in discovered)
        {
            if (capability is null
                || capability.Variant is null
                || capability.Engine != RasterEngine.Postgis
                || !Enum.IsDefined(capability.Availability)
                || string.IsNullOrWhiteSpace(capability.ProviderId)
                || string.IsNullOrWhiteSpace(capability.PolicyVersion)
                || string.IsNullOrWhiteSpace(capability.Variant.ProcessId)
                || string.IsNullOrWhiteSpace(capability.Variant.SemanticVersion)
                || string.IsNullOrWhiteSpace(capability.Variant.ImplementationVersion))
            {
                throw new InvalidOperationException(
                    "PostGIS raster provider capability discovery returned an invalid declaration.");
            }

            if ((capability.Availability == RasterProviderAvailability.Available)
                == !string.IsNullOrWhiteSpace(capability.UnavailabilityReason))
            {
                throw new InvalidOperationException(
                    $"PostGIS raster capability '{capability.Variant.ProcessId}' must carry an "
                    + "unavailability reason exactly when it is unhealthy or unavailable.");
            }

            if (!known.TryGetValue(capability.Variant.ProcessId, out var process))
            {
                throw new InvalidOperationException(
                    $"PostGIS raster provider advertises unknown process "
                    + $"'{capability.Variant.ProcessId}'.");
            }

            if (!string.Equals(
                    capability.Variant.SemanticVersion,
                    process.SemanticVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"PostGIS raster provider semantic version "
                    + $"'{capability.Variant.SemanticVersion}' for '{capability.Variant.ProcessId}' "
                    + $"does not match canonical version '{process.SemanticVersion}'.");
            }

            var route = (capability.Engine, capability.Variant.ProcessId, capability.Variant.SemanticVersion);
            if (!routes.Add(route))
            {
                throw new InvalidOperationException(
                    $"More than one PostGIS raster provider claims current semantic variant "
                    + $"'{capability.Variant.ProcessId}@{capability.Variant.SemanticVersion}'.");
            }
        }
    }

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
                    ProviderId = "postgis",
                    ProviderPolicyVersion = "postgis-raster-v1",
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
                    ProviderId = "gdal-native",
                    ProviderPolicyVersion = "gdal-native-v1",
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
            if (process is null)
            {
                throw new ArgumentException("Raster capability descriptors must be non-null.", nameof(capabilities));
            }

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

            if (string.IsNullOrWhiteSpace(process.SemanticVersion)
                || !SemanticVersionPattern().IsMatch(process.SemanticVersion))
            {
                throw new ArgumentException(
                    $"Raster process '{process.ProcessId}' has invalid semantic version "
                    + $"'{process.SemanticVersion}'.",
                    nameof(capabilities));
            }

            if (process.Engines is null || process.Engines.Count == 0)
            {
                throw new ArgumentException(
                    $"Raster process '{process.ProcessId}' must describe every known raster engine.",
                    nameof(capabilities));
            }

            var engines = new HashSet<RasterEngine>();
            foreach (var engine in process.Engines)
            {
                if (engine is null
                    || engine.Formats is null
                    || engine.RequiredCapabilities is null
                    || engine.Formats.InputMediaTypes is null
                    || engine.Formats.OutputMediaTypes is null
                    || engine.InputResidencies is null
                    || engine.OutputSinks is null)
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' has incomplete engine metadata.",
                        nameof(capabilities));
                }

                if (!Enum.IsDefined(engine.Engine))
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' declares undefined engine value "
                        + $"'{(int)engine.Engine}'.",
                        nameof(capabilities));
                }

                if (!engines.Add(engine.Engine))
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' declares engine '{engine.Engine}' twice.",
                        nameof(capabilities));
                }

                if (!Enum.IsDefined(engine.DefaultPreference))
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' engine '{engine.Engine}' declares "
                        + $"undefined default preference value '{(int)engine.DefaultPreference}'.",
                        nameof(capabilities));
                }

                var undefinedResidency = engine.InputResidencies
                    .FirstOrDefault(residency => !Enum.IsDefined(residency));
                if (!Enum.IsDefined(undefinedResidency))
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' engine '{engine.Engine}' declares "
                        + $"undefined input residency value '{(int)undefinedResidency}'.",
                        nameof(capabilities));
                }

                var undefinedSink = engine.OutputSinks
                    .FirstOrDefault(sink => !Enum.IsDefined(sink));
                if (!Enum.IsDefined(undefinedSink))
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' engine '{engine.Engine}' declares "
                        + $"undefined output sink value '{(int)undefinedSink}'.",
                        nameof(capabilities));
                }

                if (string.IsNullOrWhiteSpace(engine.ImplementationVersion)
                    || engine.RequiredCapabilities.Count == 0
                    || engine.RequiredCapabilities.Any(string.IsNullOrWhiteSpace)
                    || engine.Formats.InputMediaTypes.Count == 0
                    || engine.Formats.InputMediaTypes.Any(string.IsNullOrWhiteSpace)
                    || engine.Formats.OutputMediaTypes.Count == 0
                    || engine.Formats.OutputMediaTypes.Any(string.IsNullOrWhiteSpace)
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

                if (engine.IsAvailable && engine.UnavailabilityIsRetryable)
                {
                    throw new ArgumentException(
                        $"Raster process '{process.ProcessId}' engine '{engine.Engine}' cannot mark an available capability as retryable-unavailable.",
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

    private static RasterProcessCapability SnapshotCapability(RasterProcessCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return capability with
        {
            Engines = ReadOnly(capability.Engines.Select(SnapshotEngine).ToArray()),
        };
    }

    private static RasterEngineCapability SnapshotEngine(RasterEngineCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return capability with
        {
            RequiredCapabilities = ReadOnly(capability.RequiredCapabilities.ToArray()),
            Formats = capability.Formats with
            {
                InputMediaTypes = ReadOnly(capability.Formats.InputMediaTypes.ToArray()),
                OutputMediaTypes = ReadOnly(capability.Formats.OutputMediaTypes.ToArray()),
            },
            InputResidencies = ReadOnly(capability.InputResidencies.ToArray()),
            OutputSinks = ReadOnly(capability.OutputSinks.ToArray()),
        };
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

    private static bool IsGdalRasterMediaType(string mediaType) => mediaType is
        "image/tiff" or "image/png" or "image/jpeg" or "image/jp2" or "image/gif"
        or "image/bmp" or "application/vnd.nitf" or "application/x-erdas-hfa";

    private static string? ToGdalRasterOutputDriver(string mediaType) => mediaType switch
    {
        "image/tiff" => "GTiff",
        "image/png" => "PNG",
        "image/jpeg" => "JPEG",
        "image/jp2" => "JP2OpenJPEG",
        "image/gif" => "GIF",
        "image/bmp" => "BMP",
        "application/vnd.nitf" => "NITF",
        "application/x-erdas-hfa" => "HFA",
        "application/geo+json" => "GeoJSON",
        _ => null,
    };

    private static IReadOnlyList<string> AdditionalRequiredGdalOutputDrivers(string processId) =>
        processId switch
        {
            // The conversion process advertises TIFF as a media type, but targetFormat can
            // explicitly select the distinct COG driver as well as GTiff. Both must remain
            // registered for the process capability to be truthful.
            "conversion.raster-format" => ReadOnly("COG"),
            _ => Array.Empty<string>(),
        };

    private static GdalRasterInputFormat? ToGdalRasterInputFormat(string format) =>
        format.Trim().ToUpperInvariant() switch
        {
            "TIFF" => new("TIFF", "image/tiff", ReadOnly("GTiff")),
            "PNG" => new("PNG", "image/png", ReadOnly("PNG")),
            "JPEG" => new("JPEG", "image/jpeg", ReadOnly("JPEG")),
            "JPEG2000" => new("JPEG2000", "image/jp2", ReadOnly("JP2OpenJPEG")),
            "GIF" => new("GIF", "image/gif", ReadOnly("GIF")),
            "BMP" => new("BMP", "image/bmp", ReadOnly("BMP")),
            "NITF" => new("NITF", "application/vnd.nitf", ReadOnly("NITF")),
            "HFA" => new("HFA", "application/x-erdas-hfa", ReadOnly("HFA")),
            _ => null,
        };

    private static ReadOnlyCollection<T> ReadOnly<T>(params T[] values)
        => Array.AsReadOnly(values);

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();

    private sealed record GdalRasterInputFormat(
        string Name,
        string MediaType,
        IReadOnlyList<string> DriverNames);
}
