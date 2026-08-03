// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Public durable-parameter keys shared by raster submission and provider packages.
/// </summary>
/// <remarks>
/// Provider packages depend on Core and must not take a dependency on the Jobs implementation
/// assembly merely to decode an already-authorized durable raster request.
/// </remarks>
public static class RasterProcessExecutionParameterKeys
{
    /// <summary>Prefix for scalar process inputs, followed by the step ordinal and parameter.</summary>
    public const string StepInputPrefix = "honua.geoprocessing.step.";

    /// <summary>Prefix for typed raster-source JSON, followed by the step ordinal and parameter.</summary>
    public const string StepRasterSourcePrefix = "honua.geoprocessing.raster_source.";

    /// <summary>Builds the durable key for one scalar process input.</summary>
    public static string StepInput(int stepIndex, string parameterName)
    {
        ValidateStepAndParameter(stepIndex, parameterName);
        return $"{StepInputPrefix}{stepIndex}.{parameterName}";
    }

    /// <summary>Builds the durable key for one typed raster source.</summary>
    public static string StepRasterSource(int stepIndex, string parameterName)
    {
        ValidateStepAndParameter(stepIndex, parameterName);
        return $"{StepRasterSourcePrefix}{stepIndex}.{parameterName}";
    }

    private static void ValidateStepAndParameter(int stepIndex, string parameterName)
    {
        if (stepIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index must be non-negative.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        if (!(char.IsAsciiLetter(parameterName[0]) || parameterName[0] == '_')
            || parameterName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
        {
            throw new ArgumentException(
                "Raster process parameter names must be safe bounded identifiers.",
                nameof(parameterName));
        }
    }
}

/// <summary>Stable binding-failure codes returned before PostGIS is invoked.</summary>
public static class PostgisSurfaceZonalBindingCodes
{
    /// <summary>The process is not one of the four RAST-011 operations.</summary>
    public const string UnsupportedProcess = "unsupported-process";

    /// <summary>The durable typed source is missing or malformed.</summary>
    public const string InvalidSource = "invalid-source";

    /// <summary>The source is valid but is not resident in PostGIS.</summary>
    public const string UnsupportedSourceResidency = "unsupported-source-residency";

    /// <summary>The typed source tenant hint conflicts with the server-pinned job tenant.</summary>
    public const string TenantMismatch = "tenant-mismatch";

    /// <summary>Legacy and typed source identities were supplied together.</summary>
    public const string AmbiguousSource = "ambiguous-source";

    /// <summary>A source sub-selection would be ignored by the current PostGIS primitive.</summary>
    public const string UnsupportedSelection = "unsupported-selection";

    /// <summary>A required operation parameter is missing.</summary>
    public const string MissingParameter = "missing-parameter";

    /// <summary>An operation parameter is malformed or outside the canonical range.</summary>
    public const string InvalidParameter = "invalid-parameter";

    /// <summary>An input belongs to a different engine variant.</summary>
    public const string UnsupportedInputVariant = "unsupported-input-variant";
}

/// <summary>Provider-safe failure while binding a durable RAST-011 request.</summary>
public sealed class PostgisSurfaceZonalBindingException : ArgumentException
{
    /// <summary>Creates a binding failure with a stable machine-readable code.</summary>
    public PostgisSurfaceZonalBindingException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Stable machine-readable failure code.</summary>
    public string Code { get; }
}

/// <summary>Canonical immutable input shared by the four PostGIS operations in RAST-011.</summary>
public abstract record PostgisSurfaceZonalBinding
{
    /// <summary>Canonical public process identifier.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Validated PostGIS-resident source descriptor.</summary>
    public required PostgisRasterSourceDescriptor Source { get; init; }
}

/// <summary>Bound parameters for <c>surface.slope</c>.</summary>
public sealed record PostgisSlopeBinding : PostgisSurfaceZonalBinding
{
    /// <summary>Canonical slope output units.</summary>
    public required SlopeUnits Units { get; init; }

    /// <summary>Vertical-to-horizontal scale factor.</summary>
    public required double ZFactor { get; init; }
}

/// <summary>Bound parameters for <c>surface.aspect</c>.</summary>
public sealed record PostgisAspectBinding : PostgisSurfaceZonalBinding;

/// <summary>Bound parameters for <c>surface.hillshade</c>.</summary>
public sealed record PostgisHillshadeBinding : PostgisSurfaceZonalBinding
{
    /// <summary>Illumination azimuth in degrees clockwise from north.</summary>
    public required double AzimuthDegrees { get; init; }

    /// <summary>Illumination altitude in degrees above the horizon.</summary>
    public required double AltitudeDegrees { get; init; }

    /// <summary>Vertical-to-horizontal scale factor.</summary>
    public required double ZFactor { get; init; }
}

/// <summary>Bound parameters for <c>raster.zonal-statistics</c>.</summary>
public sealed record PostgisZonalStatisticsBinding : PostgisSurfaceZonalBinding
{
    /// <summary>
    /// Untrusted tenant-local layer identifier. The provider must authorize it through the
    /// execution-owned security snapshot before querying features.
    /// </summary>
    public required int ZonesLayerId { get; init; }

    /// <summary>One-based source band.</summary>
    public required int Band { get; init; }

    /// <summary>Canonical lowercase, de-duplicated statistics in caller order.</summary>
    public required IReadOnlyList<string> Statistics { get; init; }
}

/// <summary>
/// AOT-safe semantic binder for the existing PostGIS surface and zonal-statistics primitives.
/// </summary>
/// <remarks>
/// This binder validates immutable input identity and operation parameters only. A matching
/// caller-authored source tenant is a consistency fence, not authorization evidence. The provider
/// must still establish the execution-owned tenant/schema and resource-policy context, and output
/// publication must use the prepared attempt-fenced sink intent rather than this contract.
/// </remarks>
public static class PostgisSurfaceZonalExecutionContract
{
    /// <summary>Semantic version implemented by this binding contract.</summary>
    public const string SemanticVersion = "1.0.0";

    /// <summary>Canonical slope process identifier.</summary>
    public const string SlopeProcessId = "surface.slope";

    /// <summary>Canonical aspect process identifier.</summary>
    public const string AspectProcessId = "surface.aspect";

    /// <summary>Canonical hillshade process identifier.</summary>
    public const string HillshadeProcessId = "surface.hillshade";

    /// <summary>Canonical zonal-statistics process identifier.</summary>
    public const string ZonalStatisticsProcessId = "raster.zonal-statistics";

    private const int StepIndex = 0;
    private const string SourceParameter = "source";
    private static readonly string[] _allowedStatistics =
        ["count", "sum", "mean", "min", "max", "stddev", "variance"];
    private static readonly ReadOnlyCollection<string> _processIds = Array.AsReadOnly(
        new[] { SlopeProcessId, AspectProcessId, HillshadeProcessId, ZonalStatisticsProcessId });

    /// <summary>The exact process IDs understood by this contract.</summary>
    public static IReadOnlyList<string> ProcessIds => _processIds;

    /// <summary>
    /// Binds one single-step durable request without touching raster or zone bytes.
    /// </summary>
    /// <param name="processId">Pinned canonical process identifier.</param>
    /// <param name="tenantId">Server-owned tenant identity pinned at submission.</param>
    /// <param name="parameters">Immutable durable job parameters.</param>
    /// <returns>The exact typed operation binding.</returns>
    public static PostgisSurfaceZonalBinding Bind(
        string processId,
        string tenantId,
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(parameters);

        if (!_processIds.Contains(processId, StringComparer.Ordinal))
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.UnsupportedProcess,
                "The PostGIS surface/zonal provider does not implement the pinned process.");
        }

        var source = BindSource(tenantId, parameters);
        return processId switch
        {
            SlopeProcessId => BindSlope(source, parameters),
            AspectProcessId => new PostgisAspectBinding
            {
                ProcessId = AspectProcessId,
                Source = source,
            },
            HillshadeProcessId => BindHillshade(source, parameters),
            ZonalStatisticsProcessId => BindZonalStatistics(source, parameters),
            _ => throw Failure(
                PostgisSurfaceZonalBindingCodes.UnsupportedProcess,
                "The PostGIS surface/zonal provider does not implement the pinned process."),
        };
    }

    private static PostgisRasterSourceDescriptor BindSource(
        string tenantId,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (HasInput(parameters, "source")
            || HasInput(parameters, "layerId")
            || HasInput(parameters, "rasterId"))
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.AmbiguousSource,
                "PostGIS execution requires one typed source and rejects legacy source identities.");
        }

        var sourceKey = RasterProcessExecutionParameterKeys.StepRasterSource(
            StepIndex,
            SourceParameter);
        if (!parameters.TryGetValue(sourceKey, out var sourceJson)
            || string.IsNullOrWhiteSpace(sourceJson))
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidSource,
                "PostGIS execution requires a typed durable source descriptor.");
        }

        RasterSourceDescriptor descriptor;
        try
        {
            descriptor = RasterSourceJson.Deserialize(sourceJson);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidSource,
                "The typed durable raster source descriptor is malformed.");
        }

        var validation = RasterSourceDescriptorValidator.Validate(descriptor);
        if (!validation.IsValid)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidSource,
                "The typed durable raster source descriptor failed immutable identity validation.");
        }

        if (descriptor is not PostgisRasterSourceDescriptor postgis)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.UnsupportedSourceResidency,
                "The PostGIS surface/zonal provider accepts only PostGIS-resident raster sources.");
        }

        if (!string.Equals(
                postgis.SecurityContext.TenantId,
                tenantId,
                StringComparison.Ordinal))
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.TenantMismatch,
                "The source tenant hint does not match the execution-owned tenant fence.");
        }

        if (postgis.Selection is not null)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.UnsupportedSelection,
                "The current PostGIS surface/zonal semantic variant does not accept source sub-selections.");
        }

        return postgis;
    }

    private static PostgisSlopeBinding BindSlope(
        PostgisRasterSourceDescriptor source,
        IReadOnlyDictionary<string, string> parameters)
    {
        var units = ReadInput(parameters, "units")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "degrees" => SlopeUnits.Degrees,
            "percent" => SlopeUnits.Percent,
            // Radians exist in the primitive but are not in the current engine-neutral public
            // contract. Do not silently introduce an unproved GDAL-equivalent variant here.
            _ => throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidParameter,
                "Slope units must be 'degrees' or 'percent'."),
        };

        return new PostgisSlopeBinding
        {
            ProcessId = SlopeProcessId,
            Source = source,
            Units = units,
            ZFactor = ReadPositiveDouble(parameters, "zFactor", 1d),
        };
    }

    private static PostgisHillshadeBinding BindHillshade(
        PostgisRasterSourceDescriptor source,
        IReadOnlyDictionary<string, string> parameters) => new()
        {
            ProcessId = HillshadeProcessId,
            Source = source,
            AzimuthDegrees = ReadDoubleInRange(parameters, "azimuth", 315d, 0d, 360d),
            AltitudeDegrees = ReadDoubleInRange(parameters, "altitude", 45d, 0d, 90d),
            ZFactor = ReadPositiveDouble(parameters, "zFactor", 1d),
        };

    private static PostgisZonalStatisticsBinding BindZonalStatistics(
        PostgisRasterSourceDescriptor source,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (HasInput(parameters, "zones"))
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.UnsupportedInputVariant,
                "The PostGIS zonal-statistics variant requires a tenant-local zones layer, not inline zone bytes.");
        }

        var zonesLayerId = ReadNonNegativeInt(parameters, "zonesLayerId");
        var band = ReadPositiveInt(parameters, "band", 1);
        var statistics = ReadStatistics(parameters);
        return new PostgisZonalStatisticsBinding
        {
            ProcessId = ZonalStatisticsProcessId,
            Source = source,
            ZonesLayerId = zonesLayerId,
            Band = band,
            Statistics = statistics,
        };
    }

    private static ReadOnlyCollection<string> ReadStatistics(
        IReadOnlyDictionary<string, string> parameters)
    {
        var raw = ReadInput(parameters, "statistics");
        string[] values = string.IsNullOrWhiteSpace(raw)
            ? ["count", "mean", "stddev", "min", "max", "sum"]
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>(values.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalized = value.ToLowerInvariant();
            if (!_allowedStatistics.Contains(normalized, StringComparer.Ordinal))
            {
                throw Failure(
                    PostgisSurfaceZonalBindingCodes.InvalidParameter,
                    "Zonal statistics contain an unsupported aggregate name.");
            }

            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        if (result.Count == 0)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidParameter,
                "At least one zonal statistic is required.");
        }

        return result.AsReadOnly();
    }

    private static int ReadNonNegativeInt(
        IReadOnlyDictionary<string, string> parameters,
        string name)
    {
        var raw = ReadInput(parameters, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.MissingParameter,
                $"PostGIS zonal statistics require '{name}'.");
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidParameter,
                $"'{name}' must be a non-negative integer.");
        }

        return value;
    }

    private static int ReadPositiveInt(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        int defaultValue)
    {
        var raw = ReadInput(parameters, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidParameter,
                $"'{name}' must be a positive integer.");
        }

        return value;
    }

    private static double ReadPositiveDouble(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        double defaultValue)
    {
        var value = ReadDouble(parameters, name, defaultValue);
        if (value <= 0d)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidParameter,
                $"'{name}' must be a finite number greater than zero.");
        }

        return value;
    }

    private static double ReadDoubleInRange(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        double defaultValue,
        double minimum,
        double maximum)
    {
        var value = ReadDouble(parameters, name, defaultValue);
        if (value < minimum || value > maximum)
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidParameter,
                $"'{name}' must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static double ReadDouble(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        double defaultValue)
    {
        var raw = ReadInput(parameters, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw Failure(
                PostgisSurfaceZonalBindingCodes.InvalidParameter,
                $"'{name}' must be a finite invariant-culture number.");
        }

        return value;
    }

    private static bool HasInput(
        IReadOnlyDictionary<string, string> parameters,
        string name) => !string.IsNullOrWhiteSpace(ReadInput(parameters, name));

    private static string? ReadInput(
        IReadOnlyDictionary<string, string> parameters,
        string name) => parameters.GetValueOrDefault(
            RasterProcessExecutionParameterKeys.StepInput(StepIndex, name));

    private static PostgisSurfaceZonalBindingException Failure(string code, string message) =>
        new(code, message);
}
