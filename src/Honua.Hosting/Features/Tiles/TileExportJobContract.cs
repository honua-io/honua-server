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
    Tpkx
}

internal abstract record TileExportSourceDescriptor(long MetadataRevision);

internal sealed record TileExportMapLayerSelection(
    string LayerId,
    string StyleId,
    int StyleVersion);

internal sealed record TileExportMapSourceDescriptor(
    long MetadataRevision,
    ImmutableArray<TileExportMapLayerSelection> Layers,
    string? DataWatermark,
    string? SubmissionReuseScope) : TileExportSourceDescriptor(MetadataRevision);

internal sealed record TileExportRasterSourceDescriptor(
    long MetadataRevision,
    string LayerId,
    string MosaicRule,
    string? TimeSelection,
    string RasterSelectionFingerprint) : TileExportSourceDescriptor(MetadataRevision);

internal sealed record TileExportJobPlan
{
    public required TileExportSourceKind SourceKind { get; init; }
    public required string ResourceId { get; init; }
    public required TileExportSourceDescriptor Source { get; init; }
    public required ImmutableArray<int> ZoomLevels { get; init; }
    public required double West { get; init; }
    public required double South { get; init; }
    public required double East { get; init; }
    public required double North { get; init; }
    public required string TileImageFormat { get; init; }
    public required TileExportPackageFormat PackageFormat { get; init; }
    public required long MaxTiles { get; init; }
    public required long MaxArtifactBytes { get; init; }
    public required int RetentionSeconds { get; init; }
}

internal static class TileExportJobParameterKeys
{
    internal const string Prefix = "honua.tile_export.";
    internal const string ContractVersion = Prefix + "contract_version";
    internal const string SourceKind = Prefix + "source_kind";
    internal const string ResourceId = Prefix + "resource_id";
    internal const string SourceDescriptor = Prefix + "source_descriptor";
    internal const string ZoomLevels = Prefix + "zoom_levels";
    internal const string West = Prefix + "west";
    internal const string South = Prefix + "south";
    internal const string East = Prefix + "east";
    internal const string North = Prefix + "north";
    internal const string TileImageFormat = Prefix + "tile_image_format";
    internal const string PackageFormat = Prefix + "package_format";
    internal const string MaxTiles = Prefix + "max_tiles";
    internal const string MaxArtifactBytes = Prefix + "max_artifact_bytes";
    internal const string RetentionSeconds = Prefix + "retention_seconds";
    internal const string ContentIdentity = Prefix + "content_identity";
}

internal static class TileExportArtifactIdentity
{
    internal const string IdentityMetadataKey = "honua-tile-export-identity";

    // Version 2 content-addresses every input that can affect package bytes, including the
    // pinned source descriptor. Backend, target, retention and artifact-size admission are
    // operational controls and intentionally remain outside the content identity.
    internal static string Compute(TileExportJobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var canonical = new MemoryStream(capacity: 512);
        using (var writer = new BinaryWriter(canonical, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(TileExportExecutionSpecBuilder.ContractVersion);
            writer.Write((int)plan.SourceKind);
            WriteString(writer, plan.ResourceId);
            TileExportSourceDescriptorCodec.Write(writer, plan.Source);
            writer.Write(plan.ZoomLevels.Length);
            foreach (var level in plan.ZoomLevels)
                writer.Write(level);
            writer.Write(BitConverter.DoubleToInt64Bits(plan.West));
            writer.Write(BitConverter.DoubleToInt64Bits(plan.South));
            writer.Write(BitConverter.DoubleToInt64Bits(plan.East));
            writer.Write(BitConverter.DoubleToInt64Bits(plan.North));
            WriteString(writer, plan.TileImageFormat);
            writer.Write((int)plan.PackageFormat);
            writer.Write(plan.MaxTiles);
        }

        return Convert.ToHexStringLower(SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length))));
    }

    internal static string BuildObjectKey(TileExportJobPlan plan)
        => $"tile-exports/{Compute(plan)}.{GetExtension(plan.PackageFormat)}";

    internal static string GetExtension(TileExportPackageFormat format)
        => format switch
        {
            TileExportPackageFormat.Zip => "zip",
            TileExportPackageFormat.Tpkx => "tpkx",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported tile package format.")
        };

    internal static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}

internal static class TileExportSourceDescriptorCodec
{
    private const byte MapKind = 1;
    private const byte RasterKind = 2;

    internal static string Encode(TileExportSourceDescriptor descriptor)
    {
        using var stream = new MemoryStream(capacity: 256);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            Write(writer, descriptor);
        return Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    internal static void Write(BinaryWriter writer, TileExportSourceDescriptor descriptor)
    {
        switch (descriptor)
        {
            case TileExportMapSourceDescriptor map:
                writer.Write(MapKind);
                writer.Write(map.MetadataRevision);
                writer.Write(map.Layers.Length);
                foreach (var layer in map.Layers)
                {
                    TileExportArtifactIdentity.WriteString(writer, layer.LayerId);
                    TileExportArtifactIdentity.WriteString(writer, layer.StyleId);
                    writer.Write(layer.StyleVersion);
                }
                TileExportArtifactIdentity.WriteString(writer, map.DataWatermark ?? string.Empty);
                TileExportArtifactIdentity.WriteString(writer, map.SubmissionReuseScope ?? string.Empty);
                break;
            case TileExportRasterSourceDescriptor raster:
                writer.Write(RasterKind);
                writer.Write(raster.MetadataRevision);
                TileExportArtifactIdentity.WriteString(writer, raster.LayerId);
                TileExportArtifactIdentity.WriteString(writer, raster.MosaicRule);
                TileExportArtifactIdentity.WriteString(writer, raster.TimeSelection ?? string.Empty);
                TileExportArtifactIdentity.WriteString(writer, raster.RasterSelectionFingerprint);
                break;
            default:
                throw new ArgumentException("Tile-export source descriptor is unsupported.", nameof(descriptor));
        }
    }

    internal static bool TryDecode(string encoded, out TileExportSourceDescriptor? descriptor)
    {
        descriptor = null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            descriptor = reader.ReadByte() switch
            {
                MapKind => ReadMap(reader),
                RasterKind => ReadRaster(reader),
                _ => null
            };
            return descriptor is not null && stream.Position == stream.Length;
        }
        catch (Exception exception) when (exception is FormatException or EndOfStreamException or IOException)
        {
            return false;
        }
    }

    private static TileExportMapSourceDescriptor ReadMap(BinaryReader reader)
    {
        var revision = reader.ReadInt64();
        var count = reader.ReadInt32();
        if (count is < 0 or > TileExportExecutionSpecBuilder.MaximumMapLayers)
            throw new FormatException("Invalid map layer count.");
        var layers = ImmutableArray.CreateBuilder<TileExportMapLayerSelection>(count);
        for (var index = 0; index < count; index++)
            layers.Add(new(ReadString(reader), ReadString(reader), reader.ReadInt32()));
        return new(revision, layers.MoveToImmutable(), NullIfEmpty(ReadString(reader)), NullIfEmpty(ReadString(reader)));
    }

    private static TileExportRasterSourceDescriptor ReadRaster(BinaryReader reader)
        => new(reader.ReadInt64(), ReadString(reader), ReadString(reader), NullIfEmpty(ReadString(reader)), ReadString(reader));

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length is < 0 or > TileExportExecutionSpecBuilder.MaximumDescriptorBytes)
            throw new FormatException("Invalid descriptor string length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

internal static class TileExportExecutionSpecBuilder
{
    internal const int ContractVersion = 2;
    internal const string RuntimeProfile = "tile-export-v2";
    internal const int MaximumMapLayers = 64;
    internal const int MaximumDescriptorBytes = 768;
    private const int MaximumZoom = 30;
    private const long MaximumTiles = 100_000_000;
    private const long MaximumArtifactBytes = 1024L * 1024 * 1024;
    private const int MaximumRetentionSeconds = 7 * 24 * 60 * 60;
    private static readonly ImmutableHashSet<string> ExpectedParameterKeys = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        TileExportJobParameterKeys.ContractVersion,
        TileExportJobParameterKeys.SourceKind,
        TileExportJobParameterKeys.ResourceId,
        TileExportJobParameterKeys.SourceDescriptor,
        TileExportJobParameterKeys.ZoomLevels,
        TileExportJobParameterKeys.West,
        TileExportJobParameterKeys.South,
        TileExportJobParameterKeys.East,
        TileExportJobParameterKeys.North,
        TileExportJobParameterKeys.TileImageFormat,
        TileExportJobParameterKeys.PackageFormat,
        TileExportJobParameterKeys.MaxTiles,
        TileExportJobParameterKeys.MaxArtifactBytes,
        TileExportJobParameterKeys.RetentionSeconds,
        TileExportJobParameterKeys.ContentIdentity);

    internal static ExecutionJobSpec Build(
        TileExportJobPlan plan,
        BatchComputeTargetKind targetKind = BatchComputeTargetKind.LocalProcess,
        string backend = "local")
    {
        Validate(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        var descriptor = TileExportSourceDescriptorCodec.Encode(plan.Source);
        if (descriptor.Length >= 1024)
            throw new ArgumentException("Tile-export source descriptor exceeds the bounded contract size.", nameof(plan));

        return new ExecutionJobSpec
        {
            TargetKind = targetKind,
            Backend = backend,
            Kind = ExecutionJobKind.TileExport,
            WorkloadName = $"tile-export:{plan.SourceKind.ToString().ToLowerInvariant()}:{plan.ResourceId}",
            RuntimeProfile = RuntimeProfile,
            ContractVersion = ContractVersion,
            Parameters = ImmutableDictionary.CreateRange(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TileExportJobParameterKeys.ContractVersion] = ContractVersion.ToString(CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.SourceKind] = plan.SourceKind.ToString(),
                [TileExportJobParameterKeys.ResourceId] = plan.ResourceId,
                [TileExportJobParameterKeys.SourceDescriptor] = descriptor,
                [TileExportJobParameterKeys.ZoomLevels] = string.Join(',', plan.ZoomLevels),
                [TileExportJobParameterKeys.West] = plan.West.ToString("R", CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.South] = plan.South.ToString("R", CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.East] = plan.East.ToString("R", CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.North] = plan.North.ToString("R", CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.TileImageFormat] = plan.TileImageFormat,
                [TileExportJobParameterKeys.PackageFormat] = plan.PackageFormat.ToString(),
                [TileExportJobParameterKeys.MaxTiles] = plan.MaxTiles.ToString(CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.MaxArtifactBytes] = plan.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture),
                [TileExportJobParameterKeys.RetentionSeconds] = plan.RetentionSeconds.ToString(CultureInfo.InvariantCulture),
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
            if (!TryGet(parameters, TileExportJobParameterKeys.ContractVersion, out var version) ||
                !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVersion) ||
                parsedVersion != ContractVersion)
            {
                error = "Unsupported tile-export contract version; replay-unsafe v1 jobs require resubmission.";
                return false;
            }
            if (!ExpectedParameterKeys.SetEquals(parameters.Keys))
            {
                error = "Tile-export parameters do not match the exact versioned contract key set.";
                return false;
            }
            if (parameters.Values.Any(static value => value is null || value.Length >= 1024))
            {
                error = "Tile-export parameters exceed the bounded contract size.";
                return false;
            }
            if (!TryGetEnum(parameters, TileExportJobParameterKeys.SourceKind, out TileExportSourceKind sourceKind) ||
                !TryGetEnum(parameters, TileExportJobParameterKeys.PackageFormat, out TileExportPackageFormat packageFormat) ||
                !TryGetLong(parameters, TileExportJobParameterKeys.MaxTiles, out var maxTiles) ||
                !TryGetDouble(parameters, TileExportJobParameterKeys.West, out var west) ||
                !TryGetDouble(parameters, TileExportJobParameterKeys.South, out var south) ||
                !TryGetDouble(parameters, TileExportJobParameterKeys.East, out var east) ||
                !TryGetDouble(parameters, TileExportJobParameterKeys.North, out var north) ||
                !TryGetLong(parameters, TileExportJobParameterKeys.MaxArtifactBytes, out var maxArtifactBytes) ||
                !TryGetInt(parameters, TileExportJobParameterKeys.RetentionSeconds, out var retentionSeconds) ||
                !TryGet(parameters, TileExportJobParameterKeys.ResourceId, out var resourceId) ||
                !TryGet(parameters, TileExportJobParameterKeys.TileImageFormat, out var tileImageFormat) ||
                !TryGet(parameters, TileExportJobParameterKeys.ZoomLevels, out var zoomLevelsRaw) ||
                !TryGet(parameters, TileExportJobParameterKeys.SourceDescriptor, out var descriptorRaw) ||
                !TileExportSourceDescriptorCodec.TryDecode(descriptorRaw, out var source) ||
                !TryGet(parameters, TileExportJobParameterKeys.ContentIdentity, out var suppliedIdentity))
            {
                error = "Tile-export parameters are incomplete or malformed.";
                return false;
            }

            var zoomLevels = ParseZoomLevels(zoomLevelsRaw);
            var candidate = new TileExportJobPlan
            {
                SourceKind = sourceKind,
                ResourceId = resourceId,
                Source = source!,
                ZoomLevels = zoomLevels,
                West = west,
                South = south,
                East = east,
                North = north,
                TileImageFormat = tileImageFormat,
                PackageFormat = packageFormat,
                MaxTiles = maxTiles,
                MaxArtifactBytes = maxArtifactBytes,
                RetentionSeconds = retentionSeconds
            };

            Validate(candidate);
            if (!string.Equals(suppliedIdentity, TileExportArtifactIdentity.Compute(candidate), StringComparison.Ordinal))
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

    internal static void Validate(TileExportJobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(plan.SourceKind) || !Enum.IsDefined(plan.PackageFormat))
            throw new ArgumentException("Tile-export source or package format is unsupported.", nameof(plan));
        if (string.IsNullOrWhiteSpace(plan.ResourceId) || plan.ResourceId.Length > 256 || ContainsControlCharacter(plan.ResourceId))
            throw new ArgumentException("Tile-export resource id must contain 1 to 256 non-control characters.", nameof(plan));
        if (plan.ZoomLevels.IsDefaultOrEmpty || plan.ZoomLevels.Length > MaximumZoom + 1 ||
            plan.ZoomLevels[0] < 0 || plan.ZoomLevels[^1] > MaximumZoom ||
            plan.ZoomLevels.Zip(plan.ZoomLevels.Skip(1), static (left, right) => left >= right).Any(static invalid => invalid))
            throw new ArgumentException("Tile-export zoom levels must be unique and strictly ordered from 0 to 30.", nameof(plan));
        if (!double.IsFinite(plan.West) || !double.IsFinite(plan.South) ||
            !double.IsFinite(plan.East) || !double.IsFinite(plan.North) ||
            plan.West < -180 || plan.East > 180 || plan.South < -90 || plan.North > 90 ||
            plan.West >= plan.East || plan.South >= plan.North)
            throw new ArgumentException("Tile-export bounds must be finite, ordered WGS 84 coordinates.", nameof(plan));
        if (plan.TileImageFormat is not ("PNG" or "PNG8" or "PNG24" or "PNG32" or "JPEG" or "MIXED"))
            throw new ArgumentException("Tile-export image format is unsupported.", nameof(plan));
        if (plan.MaxTiles <= 0 || plan.MaxTiles > MaximumTiles)
            throw new ArgumentException("Tile-export tile limit must be between 1 and 100,000,000.", nameof(plan));
        if (plan.MaxArtifactBytes <= 0 || plan.MaxArtifactBytes > MaximumArtifactBytes)
            throw new ArgumentException("Tile-export artifact limit must be between 1 byte and 1 GiB.", nameof(plan));
        if (plan.RetentionSeconds < 60 || plan.RetentionSeconds > MaximumRetentionSeconds)
            throw new ArgumentException("Tile-export retention must be between 60 seconds and 7 days.", nameof(plan));
        ValidateSource(plan.SourceKind, plan.Source);
    }

    private static void ValidateSource(TileExportSourceKind sourceKind, TileExportSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.MetadataRevision <= 0)
            throw new ArgumentException("Tile-export metadata revision must be positive.", nameof(source));
        switch (sourceKind, source)
        {
            case (TileExportSourceKind.Map, TileExportMapSourceDescriptor map):
                if (map.Layers.IsDefaultOrEmpty || map.Layers.Length > MaximumMapLayers)
                    throw new ArgumentException("Tile-export Map descriptor must select 1 to 64 layers.", nameof(source));
                foreach (var layer in map.Layers)
                {
                    ValidateDescriptorValue(layer.LayerId, 128, "map layer id");
                    ValidateDescriptorValue(layer.StyleId, 256, "map style id");
                    if (layer.StyleVersion < 0)
                        throw new ArgumentException("Tile-export style versions must not be negative.", nameof(source));
                }
                if (string.IsNullOrWhiteSpace(map.DataWatermark) == string.IsNullOrWhiteSpace(map.SubmissionReuseScope))
                    throw new ArgumentException("Tile-export Map descriptor requires exactly one data watermark or submission reuse scope.", nameof(source));
                ValidateOptionalDescriptorValue(map.DataWatermark, 256, "data watermark");
                ValidateOptionalDescriptorValue(map.SubmissionReuseScope, 128, "submission reuse scope");
                break;
            case (TileExportSourceKind.Raster, TileExportRasterSourceDescriptor raster):
                ValidateDescriptorValue(raster.LayerId, 128, "raster layer id");
                ValidateDescriptorValue(raster.MosaicRule, 256, "mosaic rule");
                ValidateOptionalDescriptorValue(raster.TimeSelection, 128, "time selection");
                ValidateDescriptorValue(raster.RasterSelectionFingerprint, 128, "raster fingerprint");
                break;
            default:
                throw new ArgumentException("Tile-export source kind does not match its descriptor.", nameof(source));
        }
    }

    private static ImmutableArray<int> ParseZoomLevels(string value)
    {
        if (string.IsNullOrEmpty(value))
            return [];
        var builder = ImmutableArray.CreateBuilder<int>();
        foreach (var part in value.Split(','))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var level))
                throw new ArgumentException("Tile-export zoom levels are malformed.", nameof(value));
            builder.Add(level);
        }
        return builder.ToImmutable();
    }

    private static void ValidateDescriptorValue(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || ContainsControlCharacter(value))
            throw new ArgumentException($"Tile-export {name} is invalid.", nameof(value));
    }

    private static void ValidateOptionalDescriptorValue(string? value, int maximumLength, string name)
    {
        if (value is not null)
            ValidateDescriptorValue(value, maximumLength, name);
    }

    private static bool ContainsControlCharacter(string? value) => value?.Any(char.IsControl) == true;

    private static bool TryGet(IReadOnlyDictionary<string, string> values, string key, out string value)
        => values.TryGetValue(key, out value!) && value is not null;

    private static bool TryGetInt(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        value = default;
        return TryGet(values, key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetLong(IReadOnlyDictionary<string, string> values, string key, out long value)
    {
        value = default;
        return TryGet(values, key, out var raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> values, string key, out double value)
    {
        value = default;
        return TryGet(values, key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetEnum<TEnum>(IReadOnlyDictionary<string, string> values, string key, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return TryGet(values, key, out var raw) && Enum.TryParse(raw, ignoreCase: false, out value) && Enum.IsDefined(value);
    }
}
