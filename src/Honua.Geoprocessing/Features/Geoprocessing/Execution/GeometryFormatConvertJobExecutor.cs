// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IProcessExecutor"/> for the <c>conversion.geometry-format</c>
/// process (#3936). The catalog advertised the operation before any execution path
/// existed: it was classified protocol-only, and no owning protocol endpoint ever
/// implemented the conversion, so every caller reached a capability error.
///
/// The executor re-encodes one input geometry into an advertised interchange
/// encoding. The input is WKB or PostGIS EWKB; the SRID carried by an EWKB input is
/// the only SRID the process knows, because the catalog declares no <c>srid</c>
/// parameter. That SRID is preserved into the encodings that can carry it
/// (<c>ewkt</c>, and <c>wkb</c>, which round-trips as EWKB) and is reported on the
/// result envelope for the encodings that cannot (<c>wkt</c>, <c>geojson</c>).
/// Coordinates are never transformed — this is a re-encoding, not a reprojection.
/// </summary>
internal sealed partial class GeometryFormatConvertJobExecutor : IProcessExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog entry
    /// in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "conversion.geometry-format";

    private const string ScalarDataUriPrefix = "data:application/json;base64,";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryFormatConvertJobExecutor> _logger;

    public GeometryFormatConvertJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryFormatConvertJobExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        var processId = GeoprocessingDispatchHelper.ResolveProcessId(parameters);
        if (!string.Equals(processId, HandledProcessId, StringComparison.Ordinal))
        {
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the conversion.geometry-format executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing geometry conversion inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid geometry conversion inputs: {inputError}");
        }

        Geometry geometry;
        try
        {
            // HandleSRID accepts both plain WKB and PostGIS EWKB. A plain WKB input
            // leaves SRID 0, which the encodings below report as "no SRID" rather
            // than inventing a default.
            var reader = new WKBReader { HandleSRID = true };
            geometry = reader.Read(inputs.WkbBytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            Log.InvalidWkb(_logger, job.OperationId, ex.Message);
            return JobExecutionResult.Failed("Invalid geometry conversion inputs: WKB payload could not be decoded.");
        }

        if (geometry is null)
        {
            return JobExecutionResult.Failed("Invalid geometry conversion inputs: WKB payload decoded to no geometry.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Encoding geometry", cancellationToken).ConfigureAwait(false);

        string encoded;
        string encoding;
        try
        {
            (encoded, encoding) = Encode(geometry, inputs.Target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.EncodingFailed(_logger, job.OperationId, inputs.Target, ex);
            return JobExecutionResult.Failed(
                $"Geometry conversion to '{inputs.Target}' failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding conversion artifact", cancellationToken).ConfigureAwait(false);

        var payload = BuildResultPayload(geometry, inputs.Target, encoding, encoded);
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Geometry conversion artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        await context.PublishArtifactAsync(BuildDataUri(payload), cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Geometry conversion completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    /// <summary>
    /// Re-encodes <paramref name="geometry"/> into <paramref name="target"/> and reports
    /// whether the artifact carries the value as text or as base64 bytes.
    /// </summary>
    private static (string Value, string Encoding) Encode(Geometry geometry, string target)
    {
        switch (target)
        {
            case "wkt":
                // ISO WKT carries no SRID by definition; the envelope reports it.
                return (new WKTWriter(OutputOrdinates(geometry)).Write(geometry), "text");
            case "ewkt":
                // PostGIS EWKT: "SRID=<n>;<WKT>". SRID 0 means "unknown" in PostGIS
                // and is written as bare WKT rather than as a false SRID=0 claim.
                var wkt = new WKTWriter(OutputOrdinates(geometry)).Write(geometry);
                return (geometry.SRID > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"SRID={geometry.SRID};{wkt}")
                    : wkt, "text");
            case "geojson":
                // RFC 7946 geometry object. GeoJSON has no SRID member.
                return (new GeoJsonWriter().Write(geometry), "text");
            case "wkb":
                // handleSRID round-trips an EWKB input as EWKB and a plain WKB
                // input (SRID 0) as plain WKB.
                var writer = new WKBWriter(
                    ByteOrder.LittleEndian,
                    handleSRID: geometry.SRID > 0,
                    emitZ: HasOrdinate(geometry, Ordinate.Z),
                    emitM: HasOrdinate(geometry, Ordinate.M));
                return (Convert.ToBase64String(writer.Write(geometry)), "base64");
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported target encoding.");
        }
    }

    private static Ordinates OutputOrdinates(Geometry geometry)
    {
        var ordinates = Ordinates.XY;
        if (HasOrdinate(geometry, Ordinate.Z))
        {
            ordinates |= Ordinates.Z;
        }

        if (HasOrdinate(geometry, Ordinate.M))
        {
            ordinates |= Ordinates.M;
        }

        return ordinates;
    }

    private static bool HasOrdinate(Geometry geometry, Ordinate ordinate)
    {
        var value = ordinate == Ordinate.Z
            ? geometry.Coordinate?.Z
            : geometry.Coordinate?.M;
        return value.HasValue && !double.IsNaN(value.Value);
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out ConversionInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "geometry", out var geometry) || string.IsNullOrWhiteSpace(geometry))
        {
            error = "missing required input 'geometry'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "target", out var targetRaw) || string.IsNullOrWhiteSpace(targetRaw))
        {
            error = "missing required input 'target'";
            return false;
        }

        var target = targetRaw.Trim();
        if (!ProcessValueDomains.GeometryFormat.Contains(target))
        {
            error = $"input 'target' must be one of {string.Join(", ", ProcessValueDomains.GeometryFormat)}";
            return false;
        }

        byte[] wkbBytes;
        try
        {
            wkbBytes = Convert.FromBase64String(geometry);
        }
        catch (FormatException)
        {
            error = "input 'geometry' is not valid base64";
            return false;
        }

        if (wkbBytes.Length == 0)
        {
            error = "input 'geometry' decoded to zero bytes";
            return false;
        }

        inputs = new ConversionInputs(wkbBytes, target);
        error = "";
        return true;
    }

    private static byte[] BuildResultPayload(Geometry geometry, string target, string encoding, string value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "GeometryFormatResult");
            writer.WriteString("processId", HandledProcessId);
            writer.WriteString("target", target);
            writer.WriteString("valueEncoding", encoding);
            writer.WriteNumber("srid", geometry.SRID);
            writer.WriteString("geometryType", geometry.GeometryType);
            writer.WriteString("value", value);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string BuildDataUri(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var sb = new StringBuilder(payload.Length * 2 + ScalarDataUriPrefix.Length);
        sb.Append(ScalarDataUriPrefix);
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private readonly record struct ConversionInputs(byte[] WkbBytes, string Target);

    private static partial class Log
    {
        [LoggerMessage(9330, LogLevel.Warning,
            "Geometry format executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9331, LogLevel.Warning,
            "Geometry format executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9332, LogLevel.Warning,
            "Geometry format executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9333, LogLevel.Error,
            "Geometry format executor failed job {OperationId} encoding target '{Target}'")]
        public static partial void EncodingFailed(ILogger logger, string operationId, string target, Exception exception);

        [LoggerMessage(9334, LogLevel.Warning,
            "Geometry format executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
