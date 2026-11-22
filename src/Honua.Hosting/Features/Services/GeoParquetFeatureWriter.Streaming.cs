// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Apache.Arrow;
using Apache.Arrow.Types;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;
using ParquetSharp.Arrow;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    public static async Task<(Stream response, string contentType)> FormatAsGeoParquetStreamAsync(
        IAsyncEnumerable<Feature> features,
        MetadataV2Object resource,
        GeoParquetStreamEncodingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(options);

        var limits = options.EncodingLimits ?? new GeoParquetLimits();
        ValidateEncodingLimits(limits);

        var includeGeometry = options.ReturnGeometry && resource.HasGeometry();
        var srid = options.OutputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        _ = ResolveCoordinateEpoch(resource, includeGeometry, srid);
        _ = ResolveGeoParquetCrsProjJson(includeGeometry, srid);
        EnsureSupportedCloudNativeGeometryMeasures(includeGeometry, options.ReturnM, "GeoParquet");

        if (limits.OrderByBboxHilbert && includeGeometry)
        {
            var materialized = await CollectFeaturesWithinBudgetAsync(
                features, resource, limits, cancellationToken).ConfigureAwait(false);
            return FormatAsGeoParquetStream(
                QueryResult<Feature>.Create(materialized.Length, materialized), resource, options);
        }

        var session = new StreamingParquetSession(resource, options, limits, includeGeometry, srid);
        try
        {
            await session.WriteAllAsync(features, cancellationToken).ConfigureAwait(false);
            return (session.Complete(), ContentType);
        }
        catch (Exception ex)
        {
            throw session.AbortAndTranslate(ex);
        }
    }

    private static async Task<ImmutableArray<Feature>> CollectFeaturesWithinBudgetAsync(
        IAsyncEnumerable<Feature> features,
        MetadataV2Object resource,
        GeoParquetLimits limits,
        CancellationToken cancellationToken)
    {
        var collected = ImmutableArray.CreateBuilder<Feature>();
        long totalBytes = 0;
        var columnCount = resource.SchemaFields.Count + 6;
        await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var bytes = EstimateFeatureBytes(feature, columnCount);
            totalBytes += bytes;
            if (bytes > limits.MaxEstimatedBatchBytes || totalBytes > limits.MaxEstimatedInputBytes)
            {
                throw new GeoParquetLimitExceededException();
            }

            collected.Add(feature);
        }

        return collected.ToImmutable();
    }

    private sealed class StreamingParquetSession(
        MetadataV2Object resource,
        GeoParquetStreamEncodingOptions options,
        GeoParquetLimits limits,
        bool includeGeometry,
        int srid)
    {
        private readonly HashSet<string> _geometryTypes = new(StringComparer.Ordinal);
        private readonly List<(string Name, IArrowType Type)> _runtimeFields = [];
        private readonly HashSet<string> _runtimeFieldNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _schemaFieldNames = new(
            resource.SchemaFields.Select(static f => f.Name), StringComparer.OrdinalIgnoreCase);
        private readonly bool _isMixedGeometry = resource.ReadGeometryType() == MetadataV2GeometryType.Mixed;

        private bool _includeCovering = true;
        private bool _hasWrittenRows;
        private Schema? _schema;
        private List<MetadataV2Field>? _fieldsToInclude;
        private string _resolvedObjectIdFieldName = options.ObjectIdFieldName;
        private string _geometryColumnName = GeometryColumnName;
        private string _bboxColumnName = BboxColumnName;
        private BoundedParquetStream? _stream;
        private FileWriter? _writer;
        private ArrowWriterProperties? _arrowProperties;
        private ParquetSharp.WriterProperties? _writerProperties;

        public async Task WriteAllAsync(IAsyncEnumerable<Feature> features, CancellationToken cancellationToken)
        {
            var batch = new List<Feature>(Math.Min(limits.MaxRowsPerBatch, 256));
            long totalBytes = 0;
            long batchBytes = 0;

            await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var columnCount = resource.SchemaFields.Count + _runtimeFields.Count + 6;
                var bytes = EstimateFeatureBytes(feature, columnCount);
                totalBytes += bytes;
                if (bytes > limits.MaxEstimatedBatchBytes || totalBytes > limits.MaxEstimatedInputBytes)
                {
                    throw new GeoParquetLimitExceededException();
                }

                if (batch.Count > 0 && (batch.Count == limits.MaxRowsPerBatch || batchBytes + bytes > limits.MaxEstimatedBatchBytes))
                {
                    await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                    batchBytes = 0;
                }

                batch.Add(feature);
                batchBytes += bytes;
            }

            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
        }

        public Stream Complete()
        {
            if (!_hasWrittenRows)
            {
                DisposeWriterOnly();
                var (emptyStream, _) = CreateEmptyGeoParquet(
                    resource, options.ObjectIdFieldName, options.ReturnGeometry,
                    options.OutFields, options.OutputSrid, limits, options.FileMetadata);
                return emptyStream;
            }

            CloseWriter();
            _stream!.Position = 0;
            var finished = _stream;
            _stream = null;
            return finished;
        }

        public Exception AbortAndTranslate(Exception ex)
        {
            var limitExceeded = _stream?.LimitExceeded == true;
            var storageFull = _stream?.StorageFull == true || IsStorageFull(ex);
            DisposeAll();

            if (limitExceeded)
            {
                return new GeoParquetLimitExceededException();
            }

            if (storageFull)
            {
                return new ServiceUnavailableException(ScratchFullClientMessage, ScratchFullRetryAfterSeconds);
            }

            if (ParquetRuntimeUnavailableException.IsNativeLoadFailure(ex))
            {
                return new ParquetRuntimeUnavailableException(ex);
            }

            return ex;
        }

        private async Task FlushBatchAsync(List<Feature> batch, CancellationToken cancellationToken)
        {
            var batchFeatures = ImmutableArray.CreateRange(batch);
            var schemaChanged = InspectBatch(batchFeatures);

            if (!_hasWrittenRows)
            {
                OpenWriterForCurrentSchema();
            }
            else if (schemaChanged)
            {
                await RewriteExistingSpoolWithUpgradedSchemaAsync(cancellationToken).ConfigureAwait(false);
            }

            WriteRecordBatch(batchFeatures);
            _hasWrittenRows = true;
        }

        private bool InspectBatch(ImmutableArray<Feature> batchFeatures)
        {
            var changed = MergeRuntimeFields(batchFeatures);
            if (!includeGeometry)
            {
                return changed;
            }

            foreach (var feature in batchFeatures)
            {
                var (_, envelope, _, geometryType) = ProcessGeometryCore(
                    feature.Geometry, srid, options.GeometryLimits,
                    options.ReturnZ, options.ReturnM, feature.Id, options.Logger, encodeWkb: false);
                if (geometryType is null)
                {
                    continue;
                }

                if (_geometryTypes.Add(geometryType) && !_isMixedGeometry)
                {
                    changed = true;
                }

                if (_includeCovering && !HasFiniteEnvelope(envelope))
                {
                    _includeCovering = false;
                    changed = true;
                }
            }

            return changed;
        }

        private bool MergeRuntimeFields(ImmutableArray<Feature> batchFeatures)
        {
            var changed = false;
            var includeAllFields = options.OutFields is null || options.OutFields.Length == 0
                || (options.OutFields.Length == 1 && options.OutFields[0].Equals("*", StringComparison.Ordinal));

            foreach (var feature in batchFeatures)
            {
                var seenInFeature = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in feature.Attributes)
                {
                    if (key.StartsWith("__", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!seenInFeature.Add(key)
                        || (_runtimeFieldNames.TryGetValue(key, out var existing)
                            && !string.Equals(existing, key, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException($"Cloud-native output contains ambiguous attribute name '{key}'.");
                    }

                    if (_runtimeFieldNames.Contains(key) || _schemaFieldNames.Contains(key))
                    {
                        continue;
                    }

                    var arrowType = InferArrowTypeFromValue(value);
                    if (arrowType is null)
                    {
                        continue;
                    }

                    _runtimeFields.Add((key, arrowType));
                    _runtimeFieldNames.Add(key);
                    if (includeAllFields || options.OutFields!.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private void OpenWriterForCurrentSchema()
        {
            var built = BuildSchema(
                resource, options.ObjectIdFieldName, options.ReturnGeometry,
                options.OutFields, options.OutputSrid, _runtimeFields, _geometryTypes,
                includeCovering: _includeCovering);
            _schema = DocumentedSchema(built.schema, new ArtifactNotes
            {
                FileMetadata = options.FileMetadata,
                Limits = limits,
                RowOrder = SourceOrder,
            });
            _fieldsToInclude = built.fieldsToInclude;
            _resolvedObjectIdFieldName = built.objectIdFieldName;
            _geometryColumnName = built.geometryColumnName;
            _bboxColumnName = built.bboxColumnName;

            _stream = CreateBoundedSpool();
            (_writer, _arrowProperties, _writerProperties) = OpenFileWriter(_stream, _schema, limits);
        }

        private async Task RewriteExistingSpoolWithUpgradedSchemaAsync(CancellationToken cancellationToken)
        {
            CloseWriter();
            var previousStream = _stream!;
            _stream = null;
            previousStream.Position = 0;

            try
            {
                OpenWriterForCurrentSchema();
                using var readerProps = CreateSafeArrowReaderProperties();
                using var reader = new FileReader(previousStream, arrowProperties: readerProps, leaveOpen: true);
                using var batches = reader.GetRecordBatchReader();
                while (await batches.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false) is { } batch)
                {
                    using (batch)
                    using (var adapted = CopyToUpgradedSchema(batch, _schema!))
                    {
                        WritePreparedRecordBatch(adapted);
                    }
                }
            }
            finally
            {
                previousStream.Dispose();
            }
        }

        private static ArrowReaderProperties CreateSafeArrowReaderProperties()
        {
            var properties = ArrowReaderProperties.GetDefault();
            try
            {
                properties.PreBuffer = false;
                properties.UseThreads = false;
                return properties;
            }
            catch
            {
                properties.Dispose();
                throw;
            }
        }

        private static RecordBatch CopyToUpgradedSchema(RecordBatch source, Schema targetSchema)
        {
            var sourceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < source.Schema.FieldsList.Count; i++)
            {
                sourceIndex[source.Schema.FieldsList[i].Name] = i;
            }

            // Copy C-imported reader columns into fresh managed Arrow arrays so CArrowArrayExporter
            // never re-exports native buffers owned by the reader's importer handle.
            var arrays = new IArrowArray[targetSchema.FieldsList.Count];
            var created = 0;
            try
            {
                for (var i = 0; i < targetSchema.FieldsList.Count; i++)
                {
                    var field = targetSchema.FieldsList[i];
                    arrays[i] = sourceIndex.TryGetValue(field.Name, out var oldIndex)
                        ? CloneToManagedArray(source.Column(oldIndex))
                        : BuildNullArray(field.DataType, source.Length);
                    created++;
                }

                return new RecordBatch(targetSchema, arrays, source.Length);
            }
            catch
            {
                for (var i = 0; i < created; i++)
                {
                    arrays[i].Dispose();
                }

                throw;
            }
        }

        private static IArrowArray CloneToManagedArray(IArrowArray column) => column switch
        {
            Int64Array a => ClonePrimitive(a, new Int64Array.Builder()),
            Int32Array a => ClonePrimitive(a, new Int32Array.Builder()),
            Int16Array a => ClonePrimitive(a, new Int16Array.Builder()),
            DoubleArray a => ClonePrimitive(a, new DoubleArray.Builder()),
            FloatArray a => ClonePrimitive(a, new FloatArray.Builder()),
            BooleanArray a => CloneBoolean(a),
            StringArray a => CloneString(a),
            BinaryArray a => CloneBinary(a),
            Date32Array a => CloneDate32(a),
            TimestampArray a => CloneTimestamp(a),
            Time64Array a => CloneTime64(a),
            StructArray a => CloneStruct(a),
            _ => throw new NotSupportedException($"Unsupported Arrow column type '{column.Data.DataType.Name}' during schema rewrite.")
        };

        private static TArray ClonePrimitive<T, TArray, TBuilder>(
            PrimitiveArray<T> source,
            PrimitiveArrayBuilder<T, TArray, TBuilder> builder)
            where T : struct, IEquatable<T>
            where TArray : IArrowArray
            where TBuilder : PrimitiveArrayBuilder<T, TArray, TBuilder>
        {
            for (var i = 0; i < source.Length; i++)
            {
                if (source.GetValue(i) is { } value)
                {
                    builder.Append(value);
                }
                else
                {
                    builder.AppendNull();
                }
            }

            return builder.Build();
        }

        private static BooleanArray CloneBoolean(BooleanArray source)
        {
            var builder = new BooleanArray.Builder();
            for (var i = 0; i < source.Length; i++)
            {
                if (source.GetValue(i) is { } value)
                {
                    builder.Append(value);
                }
                else
                {
                    builder.AppendNull();
                }
            }

            return builder.Build();
        }

        private static StringArray CloneString(StringArray source)
        {
            var builder = new StringArray.Builder();
            for (var i = 0; i < source.Length; i++)
            {
                if (source.IsNull(i))
                {
                    builder.AppendNull();
                }
                else
                {
                    builder.Append(source.GetString(i));
                }
            }

            return builder.Build();
        }

        private static BinaryArray CloneBinary(BinaryArray source)
        {
            var builder = new BinaryArray.Builder();
            for (var i = 0; i < source.Length; i++)
            {
                if (source.IsNull(i))
                {
                    builder.AppendNull();
                }
                else
                {
                    builder.Append(source.GetBytes(i));
                }
            }

            return builder.Build();
        }

        private static Date32Array CloneDate32(Date32Array source)
        {
            var builder = new Date32Array.Builder();
            for (var i = 0; i < source.Length; i++)
            {
                if (source.GetDateOnly(i) is { } value)
                {
                    builder.Append(value);
                }
                else
                {
                    builder.AppendNull();
                }
            }

            return builder.Build();
        }

        private static TimestampArray CloneTimestamp(TimestampArray source)
        {
            var timestampType = (TimestampType)source.Data.DataType;
            var builder = new TimestampArray.Builder(timestampType);
            for (var i = 0; i < source.Length; i++)
            {
                if (source.GetTimestamp(i) is { } value)
                {
                    builder.Append(value);
                }
                else
                {
                    builder.AppendNull();
                }
            }

            return builder.Build();
        }

        private static Time64Array CloneTime64(Time64Array source)
        {
            var builder = new Int64Array.Builder();
            for (var i = 0; i < source.Length; i++)
            {
                if (source.GetValue(i) is { } value)
                {
                    builder.Append(value);
                }
                else
                {
                    builder.AppendNull();
                }
            }

            var int64Array = builder.Build();
            return new Time64Array(
                (Time64Type)source.Data.DataType,
                int64Array.ValueBuffer,
                int64Array.NullBitmapBuffer,
                int64Array.Length,
                int64Array.NullCount,
                int64Array.Offset);
        }

        private static StructArray CloneStruct(StructArray source)
        {
            var structType = (StructType)source.Data.DataType;
            var fields = new IArrowArray[source.Fields.Count];
            var bitmap = new ArrowBuffer.BitmapBuilder();
            for (var i = 0; i < source.Fields.Count; i++)
            {
                fields[i] = CloneToManagedArray(source.Fields[i]);
            }

            for (var i = 0; i < source.Length; i++)
            {
                bitmap.Append(!source.IsNull(i));
            }

            return new StructArray(structType, source.Length, fields, bitmap.Build(), source.NullCount);
        }

        private static IArrowArray BuildNullArray(IArrowType dataType, int length) => dataType switch
        {
            DoubleType => FillNulls(new DoubleArray.Builder(), length),
            FloatType => FillNulls(new FloatArray.Builder(), length),
            Int32Type => FillNulls(new Int32Array.Builder(), length),
            Int64Type => FillNulls(new Int64Array.Builder(), length),
            BooleanType => FillNulls(new BooleanArray.Builder(), length),
            Date32Type => FillNulls(new Date32Array.Builder(), length),
            Time64Type => BuildNullTime64(length),
            TimestampType => FillNulls(new TimestampArray.Builder(TimeUnit.Microsecond, TimeZoneInfo.Utc), length),
            _ => FillNulls(new StringArray.Builder(), length)
        };

        private static TArray FillNulls<T, TArray, TBuilder>(
            PrimitiveArrayBuilder<T, TArray, TBuilder> builder, int length)
            where T : struct, IEquatable<T>
            where TArray : IArrowArray
            where TBuilder : PrimitiveArrayBuilder<T, TArray, TBuilder>
        {
            for (var i = 0; i < length; i++)
            {
                builder.AppendNull();
            }

            return builder.Build();
        }

        private static BooleanArray FillNulls(BooleanArray.Builder builder, int length)
        {
            for (var i = 0; i < length; i++)
            {
                builder.AppendNull();
            }

            return builder.Build();
        }

        private static StringArray FillNulls(StringArray.Builder builder, int length)
        {
            for (var i = 0; i < length; i++)
            {
                builder.AppendNull();
            }

            return builder.Build();
        }

        private static Date32Array FillNulls(Date32Array.Builder builder, int length)
        {
            for (var i = 0; i < length; i++)
            {
                builder.AppendNull();
            }

            return builder.Build();
        }

        private static TimestampArray FillNulls(TimestampArray.Builder builder, int length)
        {
            for (var i = 0; i < length; i++)
            {
                builder.AppendNull();
            }

            return builder.Build();
        }

        private static Time64Array BuildNullTime64(int length)
        {
            var builder = new Int64Array.Builder();
            for (var i = 0; i < length; i++)
            {
                builder.AppendNull();
            }

            var int64Array = builder.Build();
            return new Time64Array(
                new Time64Type(TimeUnit.Microsecond),
                int64Array.ValueBuffer,
                int64Array.NullBitmapBuffer,
                int64Array.Length,
                int64Array.NullCount,
                int64Array.Offset);
        }

        private void WriteRecordBatch(ImmutableArray<Feature> batchFeatures)
        {
            BinaryArray? geometryArray = null;
            StructArray? bboxArray = null;
            if (includeGeometry)
            {
                (geometryArray, bboxArray, _) = BuildGeometryArray(
                    batchFeatures, srid, options.ReturnZ, options.ReturnM, options.GeometryLimits, options.Logger);
                if (!_includeCovering)
                {
                    bboxArray.Dispose();
                    bboxArray = null;
                }
            }

            var arrays = BuildArrays(
                batchFeatures, _schema!, options.ReturnGeometry, geometryArray, bboxArray,
                _resolvedObjectIdFieldName, _fieldsToInclude!, _geometryColumnName, _bboxColumnName, options.Logger);
            using var recordBatch = new RecordBatch(_schema!, arrays, batchFeatures.Length);
            WritePreparedRecordBatch(recordBatch);
        }

        private void WritePreparedRecordBatch(RecordBatch recordBatch)
        {
            if (limits.RowsPerRowGroup is null)
            {
                _writer!.WriteRecordBatch(recordBatch, limits.MaxRowsPerBatch);
            }
            else
            {
                _writer!.WriteBufferedRecordBatch(recordBatch);
            }
        }

        private BoundedParquetStream CreateBoundedSpool()
        {
            try
            {
                return new BoundedParquetStream(checked((int)OutputByteCap(limits)));
            }
            catch (IOException ex) when (IsStorageFull(ex))
            {
                throw new ServiceUnavailableException(ScratchFullClientMessage, ScratchFullRetryAfterSeconds);
            }
        }

        private static (FileWriter Writer, ArrowWriterProperties ArrowProps, ParquetSharp.WriterProperties? WriterProps)
            OpenFileWriter(BoundedParquetStream stream, Schema schema, GeoParquetLimits limits)
        {
            using var arrowBuilder = new ArrowWriterPropertiesBuilder();
            arrowBuilder.StoreSchema();
            var arrowProps = arrowBuilder.Build();
            ParquetSharp.WriterProperties? writerProps = null;
            try
            {
                if (limits.RowsPerRowGroup is not null || limits.CompressWithZstd)
                {
                    using var propsBuilder = new ParquetSharp.WriterPropertiesBuilder();
                    if (limits.RowsPerRowGroup is int rowsPerRowGroup)
                    {
                        propsBuilder.MaxRowGroupLength(rowsPerRowGroup);
                    }

                    if (limits.CompressWithZstd)
                    {
                        propsBuilder.Compression(ParquetSharp.Compression.Zstd);
                    }

                    writerProps = propsBuilder.Build();
                }

                var writer = new FileWriter(stream, schema, writerProps, arrowProps, true);
                return (writer, arrowProps, writerProps);
            }
            catch
            {
                writerProps?.Dispose();
                arrowProps.Dispose();
                throw;
            }
        }

        private void CloseWriter()
        {
            _writer?.Close();
            DisposeWriterOnly();
        }

        private void DisposeWriterOnly()
        {
            _writer?.Dispose();
            _writer = null;
            _writerProperties?.Dispose();
            _writerProperties = null;
            _arrowProperties?.Dispose();
            _arrowProperties = null;
        }

        private void DisposeAll()
        {
            DisposeWriterOnly();
            _stream?.Dispose();
            _stream = null;
        }
    }
}
