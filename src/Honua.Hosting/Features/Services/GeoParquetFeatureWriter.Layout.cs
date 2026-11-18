// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Apache.Arrow;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles.PMTiles;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using ParquetSharp.Arrow;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    public const string RowOrderMetadataKey = "honua.geoparquet.row_order";
    public const string RowsPerRowGroupMetadataKey = "honua.geoparquet.rows_per_row_group";
    public const string HilbertBboxCenterOrder = "hilbert-bbox-center";
    public const string SourceOrder = "source";

    private const int HilbertZoom = 16;

    private sealed class ArrowEncode
    {
        public required Schema Schema { get; init; }
        public required Action<FileWriter>? WriteBatches { get; init; }
        public required BoundedParquetStream Stream { get; init; }
        public GeoParquetLimits? Limits { get; init; }
    }

    private sealed class ArtifactNotes
    {
        public required IReadOnlyDictionary<string, string>? FileMetadata { get; init; }
        public required GeoParquetLimits Limits { get; init; }
        public required string RowOrder { get; init; }
    }

    private sealed class HilbertPass
    {
        public required int Srid { get; init; }
        public required GeometryLimits GeometryLimits { get; init; }
        public required bool ReturnZ { get; init; }
        public required bool ReturnM { get; init; }
        public required ILogger? Logger { get; init; }
    }

    private readonly record struct Axis(double Min, double Max, int Cells);

    private static BoundedParquetStream WriteEncoded(ArrowEncode encode)
    {
        var stream = encode.Stream;
        try
        {
            EncodeArrowParquet(encode);
            stream.Position = 0;
            return stream;
        }
        catch (Exception) when (stream.LimitExceeded)
        {
            // The native callback can wrap a managed Stream exception. Keep the shared
            // budget contract even when the refusal occurs while writing the footer.
            stream.Dispose();
            throw new GeoParquetLimitExceededException();
        }
        catch (Exception ex) when (stream.StorageFull || IsStorageFull(ex))
        {
            stream.Dispose();
            throw new ServiceUnavailableException(ScratchFullClientMessage, ScratchFullRetryAfterSeconds);
        }
        catch (Exception ex) when (ParquetRuntimeUnavailableException.IsNativeLoadFailure(ex))
        {
            stream.Dispose();
            throw new ParquetRuntimeUnavailableException(ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EncodeArrowParquet(ArrowEncode encode)
    {
        // Construct and dispose the builder itself explicitly (rather than chaining off
        // the fluent StoreSchema() return value) so the builder instance is always
        // disposed even if StoreSchema() were ever changed to return a different object.
        using var arrowWriterPropertiesBuilder = new ArrowWriterPropertiesBuilder();
        arrowWriterPropertiesBuilder.StoreSchema();
        using var arrowWriterProperties = arrowWriterPropertiesBuilder.Build();
        using var writerPropertiesBuilder = new ParquetSharp.WriterPropertiesBuilder();
        var limits = encode.Limits;
        if (limits?.RowsPerRowGroup is int rowsPerRowGroup)
        {
            writerPropertiesBuilder.MaxRowGroupLength(rowsPerRowGroup);
        }

        if (limits?.CompressWithZstd == true)
        {
            writerPropertiesBuilder.Compression(ParquetSharp.Compression.Zstd);
        }

        using var writerProperties = limits is null || (limits.RowsPerRowGroup is null && !limits.CompressWithZstd)
            ? null
            : writerPropertiesBuilder.Build();
        using var writer = new FileWriter(encode.Stream, encode.Schema, writerProperties, arrowWriterProperties, true);
        encode.WriteBatches?.Invoke(writer);
        writer.Close();
    }

    private static Schema DocumentedSchema(Schema schema, ArtifactNotes notes)
    {
        schema = WithFileMetadata(schema, notes.FileMetadata);
        var limits = notes.Limits;
        if (limits.RowsPerRowGroup is null && !limits.OrderByBboxHilbert && !limits.CompressWithZstd && limits.MaxArtifactBytes is null)
        {
            return schema;
        }

        var documented = new Dictionary<string, string>
        {
            [RowOrderMetadataKey] = notes.RowOrder,
        };
        if (limits.RowsPerRowGroup is int rowsPerRowGroup)
        {
            documented[RowsPerRowGroupMetadataKey] = rowsPerRowGroup.ToString(CultureInfo.InvariantCulture);
        }

        return WithFileMetadata(schema, documented);
    }

    private static ImmutableArray<Feature> OrderByBboxCenterHilbert(ImmutableArray<Feature> features, HilbertPass pass)
    {
        var (envelopes, extent) = CollectEnvelopes(features, pass);
        if (extent.IsNull)
        {
            return features;
        }

        return ReorderByKey(features, HilbertKeys(envelopes, extent));
    }

    private static (Envelope?[] Envelopes, Envelope Extent) CollectEnvelopes(
        ImmutableArray<Feature> features,
        HilbertPass pass)
    {
        var envelopes = new Envelope?[features.Length];
        var extent = new Envelope();
        for (var index = 0; index < features.Length; index++)
        {
            var (_, envelope, _, _) = ProcessGeometryCore(
                features[index].Geometry, pass.Srid, pass.GeometryLimits, pass.ReturnZ, pass.ReturnM,
                features[index].Id, pass.Logger, encodeWkb: false);
            envelopes[index] = envelope;
            if (HasFiniteEnvelope(envelope))
            {
                extent.ExpandToInclude(envelope);
            }
        }

        return (envelopes, extent);
    }

    private static ulong[] HilbertKeys(Envelope?[] envelopes, Envelope extent)
    {
        var keys = new ulong[envelopes.Length];
        for (var index = 0; index < envelopes.Length; index++)
        {
            keys[index] = HilbertKey(envelopes[index], extent);
        }

        return keys;
    }

    private static ImmutableArray<Feature> ReorderByKey(ImmutableArray<Feature> features, ulong[] keys)
    {
        var order = new int[features.Length];
        for (var index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        System.Array.Sort(order, (left, right) =>
        {
            var compared = keys[left].CompareTo(keys[right]);
            return compared != 0 ? compared : left.CompareTo(right);
        });

        var ordered = ImmutableArray.CreateBuilder<Feature>(features.Length);
        foreach (var index in order)
        {
            ordered.Add(features[index]);
        }

        return ordered.MoveToImmutable();
    }

    private static ulong HilbertKey(Envelope? envelope, Envelope extent)
    {
        if (!HasFiniteEnvelope(envelope))
        {
            return ulong.MaxValue;
        }

        var centerX = (envelope!.MinX + envelope.MaxX) / 2;
        var centerY = (envelope.MinY + envelope.MaxY) / 2;
        var cells = 1 << HilbertZoom;
        var x = Quantize(centerX, new Axis(extent.MinX, extent.MaxX, cells));
        var y = Quantize(centerY, new Axis(extent.MinY, extent.MaxY, cells));
        return HilbertCurve.XYZToTileId(HilbertZoom, x, y);
    }

    private static int Quantize(double value, Axis axis)
    {
        if (!(axis.Max > axis.Min) || !double.IsFinite(value))
        {
            return 0;
        }

        var span = (value - axis.Min) / (axis.Max - axis.Min);
        if (span <= 0)
        {
            return 0;
        }

        if (span >= 1)
        {
            return axis.Cells - 1;
        }

        return (int)(span * (axis.Cells - 1));
    }

    private static bool HasFiniteEnvelope(Envelope? envelope)
        => envelope is not null && !envelope.IsNull
            && double.IsFinite(envelope.MinX) && double.IsFinite(envelope.MinY)
            && double.IsFinite(envelope.MaxX) && double.IsFinite(envelope.MaxY);
}
