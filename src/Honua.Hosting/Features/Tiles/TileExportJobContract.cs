// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Infrastructure.Tiles;

internal enum TileExportSourceKind
{
    Map,
    Raster
}

internal enum TileExportPackageFormat
{
    Zip,
    Tpk,
    Tpkx
}

internal sealed record TileExportJobPlan
{
    public required TileExportSourceKind SourceKind { get; init; }
    public required string ResourceId { get; init; }
    public string? LayerId { get; init; }
    public required int MinZoom { get; init; }
    public required int MaxZoom { get; init; }
    public required double West { get; init; }
    public required double South { get; init; }
    public required double East { get; init; }
    public required double North { get; init; }
    public required string TileImageFormat { get; init; }
    public required TileExportPackageFormat PackageFormat { get; init; }
    public required long MaxArtifactBytes { get; init; }
    public required int RetentionSeconds { get; init; }
    public string? StyleId { get; init; }
}

internal static class TileExportJobParameterKeys
{
    internal const string Prefix = "honua.tile_export.";
    internal const string ContractVersion = Prefix + "contract_version";
    internal const string SourceKind = Prefix + "source_kind";
    internal const string ResourceId = Prefix + "resource_id";
    internal const string LayerId = Prefix + "layer_id";
    internal const string MinZoom = Prefix + "min_zoom";
    internal const string MaxZoom = Prefix + "max_zoom";
    internal const string West = Prefix + "west";
    internal const string South = Prefix + "south";
    internal const string East = Prefix + "east";
    internal const string North = Prefix + "north";
    internal const string TileImageFormat = Prefix + "tile_image_format";
    internal const string PackageFormat = Prefix + "package_format";
    internal const string MaxArtifactBytes = Prefix + "max_artifact_bytes";
    internal const string RetentionSeconds = Prefix + "retention_seconds";
    internal const string StyleId = Prefix + "style_id";
    internal const string ContentIdentity = Prefix + "content_identity";
}

internal static class TileExportArtifactIdentity
{
    internal const string IdentityMetadataKey = "honua-tile-export-identity";

    internal static string Compute(TileExportJobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = string.Join('\n',
            "1",
            plan.SourceKind.ToString(),
            plan.ResourceId,
            plan.LayerId ?? string.Empty,
            plan.MinZoom.ToString(CultureInfo.InvariantCulture),
            plan.MaxZoom.ToString(CultureInfo.InvariantCulture),
            plan.West.ToString("R", CultureInfo.InvariantCulture),
            plan.South.ToString("R", CultureInfo.InvariantCulture),
            plan.East.ToString("R", CultureInfo.InvariantCulture),
            plan.North.ToString("R", CultureInfo.InvariantCulture),
            plan.TileImageFormat,
            plan.PackageFormat.ToString(),
            plan.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture),
            plan.RetentionSeconds.ToString(CultureInfo.InvariantCulture),
            plan.StyleId ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static string BuildObjectKey(TileExportJobPlan plan)
        => $"tile-exports/{Compute(plan)}.{GetExtension(plan.PackageFormat)}";

    internal static string GetExtension(TileExportPackageFormat format)
        => format switch
        {
            TileExportPackageFormat.Zip => "zip",
            TileExportPackageFormat.Tpk => "tpk",
            TileExportPackageFormat.Tpkx => "tpkx",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported tile package format.")
        };
}

internal static class TileExportExecutionSpecBuilder
{
    private const int ContractVersion = 1;
    private const int MaximumZoom = 30;
    private const long MaximumArtifactBytes = 1024L * 1024 * 1024;
    private const int MaximumRetentionSeconds = 7 * 24 * 60 * 60;

    internal static ExecutionJobSpec Build(
        TileExportJobPlan plan,
        BatchComputeTargetKind targetKind = BatchComputeTargetKind.LocalProcess,
        string backend = "local")
    {
        Validate(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);

        return new ExecutionJobSpec
        {
            TargetKind = targetKind,
            Backend = backend,
            Kind = ExecutionJobKind.TileExport,
            WorkloadName = $"tile-export:{plan.SourceKind.ToString().ToLowerInvariant()}:{plan.ResourceId}",
            ContractVersion = ContractVersion,
            Parameters = ImmutableDictionary.CreateRange(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TileExportJobParameterKeys.ContractVersion] = ContractVersion.ToString(CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.SourceKind] = plan.SourceKind.ToString(),
                [TileExportJobParameterKeys.ResourceId] = plan.ResourceId,
                [TileExportJobParameterKeys.LayerId] = plan.LayerId ?? string.Empty,
                [TileExportJobParameterKeys.MinZoom] = plan.MinZoom.ToString(CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.MaxZoom] = plan.MaxZoom.ToString(CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.West] = plan.West.ToString("R", CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.South] = plan.South.ToString("R", CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.East] = plan.East.ToString("R", CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.North] = plan.North.ToString("R", CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.TileImageFormat] = plan.TileImageFormat,
                [TileExportJobParameterKeys.PackageFormat] = plan.PackageFormat.ToString(),
                [TileExportJobParameterKeys.MaxArtifactBytes] = plan.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.RetentionSeconds] = plan.RetentionSeconds.ToString(CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.StyleId] = plan.StyleId ?? string.Empty,
                [TileExportJobParameterKeys.ContentIdentity] = TileExportArtifactIdentity.Compute(plan)
            })
        };
    }

    internal static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out TileExportJobPlan? plan,
        out string? error)
    {
        plan = null;
        error = null;

        try
        {
            if (parameters.Count > 32 || parameters.Values.Any(static value => value is null || value.Length >= 1024))
            {
                error = "Tile-export parameters exceed the bounded contract size.";
                return false;
            }

            if (!TryGet(parameters, TileExportJobParameterKeys.ContractVersion, out var version) ||
                !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVersion) ||
                parsedVersion != ContractVersion)
            {
                error = "Unsupported or missing tile-export contract version.";
                return false;
            }

            if (!TryGetEnum(parameters, TileExportJobParameterKeys.SourceKind, out TileExportSourceKind sourceKind) ||
                !TryGetEnum(parameters, TileExportJobParameterKeys.PackageFormat, out TileExportPackageFormat packageFormat) ||
                !TryGetInt(parameters, TileExportJobParameterKeys.MinZoom, out var minZoom) ||
                !TryGetInt(parameters, TileExportJobParameterKeys.MaxZoom, out var maxZoom) ||
                !TryGetDouble(parameters, TileExportJobParameterKeys.West, out var west) ||
                !TryGetDouble(parameters, TileExportJobParameterKeys.South, out var south) ||
                !TryGetDouble(parameters, TileExportJobParameterKeys.East, out var east) ||
                !TryGetDouble(parameters, TileExportJobParameterKeys.North, out var north) ||
                !TryGetLong(parameters, TileExportJobParameterKeys.MaxArtifactBytes, out var maxArtifactBytes) ||
                !TryGetInt(parameters, TileExportJobParameterKeys.RetentionSeconds, out var retentionSeconds) ||
                !TryGet(parameters, TileExportJobParameterKeys.ResourceId, out var resourceId) ||
                !TryGet(parameters, TileExportJobParameterKeys.TileImageFormat, out var tileImageFormat) ||
                !TryGet(parameters, TileExportJobParameterKeys.ContentIdentity, out var suppliedIdentity))
            {
                error = "Tile-export parameters are incomplete or malformed.";
                return false;
            }

            parameters.TryGetValue(TileExportJobParameterKeys.LayerId, out var layerId);
            parameters.TryGetValue(TileExportJobParameterKeys.StyleId, out var styleId);
            var candidate = new TileExportJobPlan
            {
                SourceKind = sourceKind,
                ResourceId = resourceId,
                LayerId = NullIfEmpty(layerId),
                MinZoom = minZoom,
                MaxZoom = maxZoom,
                West = west,
                South = south,
                East = east,
                North = north,
                TileImageFormat = tileImageFormat,
                PackageFormat = packageFormat,
                MaxArtifactBytes = maxArtifactBytes,
                RetentionSeconds = retentionSeconds,
                StyleId = NullIfEmpty(styleId)
            };

            Validate(candidate);
            if (!string.Equals(
                    suppliedIdentity,
                    TileExportArtifactIdentity.Compute(candidate),
                    StringComparison.Ordinal))
            {
                error = "Tile-export content identity does not match the serialized plan.";
                return false;
            }

            plan = candidate;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void Validate(TileExportJobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(plan.SourceKind) || !Enum.IsDefined(plan.PackageFormat))
            throw new ArgumentException("Tile-export source or package format is unsupported.", nameof(plan));
        if (string.IsNullOrWhiteSpace(plan.ResourceId) || plan.ResourceId.Length > 256)
            throw new ArgumentException("Tile-export resource id must contain 1 to 256 characters.", nameof(plan));
        if (plan.LayerId?.Length > 128 || plan.StyleId?.Length > 256)
            throw new ArgumentException("Tile-export layer and style identifiers exceed their limits.", nameof(plan));
        if (plan.MinZoom < 0 || plan.MaxZoom > MaximumZoom || plan.MinZoom > plan.MaxZoom)
            throw new ArgumentException("Tile-export zoom levels must form an ordered range from 0 to 30.", nameof(plan));
        if (!double.IsFinite(plan.West) || !double.IsFinite(plan.South) ||
            !double.IsFinite(plan.East) || !double.IsFinite(plan.North) ||
            plan.West < -180 || plan.East > 180 || plan.South < -90 || plan.North > 90 ||
            plan.West >= plan.East || plan.South >= plan.North)
            throw new ArgumentException("Tile-export bounds must be finite, ordered WGS 84 coordinates.", nameof(plan));
        if (plan.TileImageFormat is not ("PNG" or "PNG8" or "PNG24" or "PNG32" or "JPEG" or "MIXED"))
            throw new ArgumentException("Tile-export image format is unsupported.", nameof(plan));
        if (plan.MaxArtifactBytes <= 0 || plan.MaxArtifactBytes > MaximumArtifactBytes)
            throw new ArgumentException("Tile-export artifact limit must be between 1 byte and 1 GiB.", nameof(plan));
        if (plan.RetentionSeconds < 60 || plan.RetentionSeconds > MaximumRetentionSeconds)
            throw new ArgumentException("Tile-export retention must be between 60 seconds and 7 days.", nameof(plan));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool TryGet(IReadOnlyDictionary<string, string> values, string key, out string value)
        => values.TryGetValue(key, out value!) && value is not null;

    private static bool TryGetInt(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        value = default;
        return TryGet(values, key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetLong(IReadOnlyDictionary<string, string> values, string key, out long value)
    {
        value = default;
        return TryGet(values, key, out var raw) &&
               long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> values, string key, out double value)
    {
        value = default;
        return TryGet(values, key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetEnum<TEnum>(IReadOnlyDictionary<string, string> values, string key, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return TryGet(values, key, out var raw) &&
               Enum.TryParse(raw, ignoreCase: false, out value) &&
               Enum.IsDefined(value);
    }
}
