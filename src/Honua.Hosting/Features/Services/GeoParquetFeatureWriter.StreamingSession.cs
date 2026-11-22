// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Apache.Arrow;
using Apache.Arrow.Types;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Logging;
using ParquetSharp.Arrow;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    private sealed class StreamingParquetSession(StreamingEncoding encoding)
    {
        private readonly HashSet<string> _geometryTypes = new(StringComparer.Ordinal);
        private readonly List<(string Name, IArrowType Type)> _runtimeFields = [];
        private readonly HashSet<string> _runtimeFieldNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _schemaFieldNames = new(
            encoding.Resource.SchemaFields.Select(static f => f.Name), StringComparer.OrdinalIgnoreCase);
        private readonly bool _isMixedGeometry = encoding.Resource.ReadGeometryType() == MetadataV2GeometryType.Mixed;

        private bool _includeCovering = true;
        private bool _hasWrittenRows;
        private Schema? _schema;
        private List<MetadataV2Field>? _fieldsToInclude;
        private string _resolvedObjectIdFieldName = encoding.Options.ObjectIdFieldName;
        private string _geometryColumnName = GeometryColumnName;
        private string _bboxColumnName = BboxColumnName;
        private bool _schemaIncludesBbox;
        private BoundedParquetStream? _stream;
        private FileWriter? _writer;
        private ArrowWriterProperties? _arrowProperties;
        private ParquetSharp.WriterProperties? _writerProperties;

        public async Task WriteAllAsync(IAsyncEnumerable<Feature> features, CancellationToken cancellationToken)
        {
            var batch = new List<Feature>(Math.Min(encoding.Limits.MaxRowsPerBatch, 256));
            long totalBytes = 0;
            long batchBytes = 0;

            await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var columnCount = encoding.Resource.SchemaFields.Count + _runtimeFields.Count + 6;
                var bytes = EstimateFeatureBytes(feature, columnCount);
                totalBytes += bytes;
                if (bytes > encoding.Limits.MaxEstimatedBatchBytes || totalBytes > encoding.Limits.MaxEstimatedInputBytes)
                {
                    throw new GeoParquetLimitExceededException();
                }

                if (batch.Count > 0 && (batch.Count == encoding.Limits.MaxRowsPerBatch || batchBytes + bytes > encoding.Limits.MaxEstimatedBatchBytes))
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
                    encoding.Resource, encoding.Options.ObjectIdFieldName, encoding.Options.ReturnGeometry,
                    encoding.Options.OutFields, encoding.Options.OutputSrid, encoding.Limits, encoding.Options.FileMetadata);
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
            if (!encoding.IncludeGeometry)
            {
                return changed;
            }

            foreach (var feature in batchFeatures)
            {
                var (_, envelope, _, geometryType) = ProcessGeometryCore(
                    feature.Geometry, encoding.Srid, encoding.Options.GeometryLimits,
                    encoding.Options.ReturnZ, encoding.Options.ReturnM, feature.Id, encoding.Options.Logger, encodeWkb: false);
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
            var includeAllFields = encoding.Options.OutFields is null || encoding.Options.OutFields.Length == 0
                || (encoding.Options.OutFields.Length == 1 && encoding.Options.OutFields[0].Equals("*", StringComparison.Ordinal));

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
                    if (includeAllFields || encoding.Options.OutFields!.Contains(key, StringComparer.OrdinalIgnoreCase))
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
                encoding.Resource, encoding.Options.ObjectIdFieldName, encoding.Options.ReturnGeometry,
                encoding.Options.OutFields, encoding.Options.OutputSrid, _runtimeFields, _geometryTypes,
                includeCovering: _includeCovering);
            _schema = DocumentedSchema(built.schema, new ArtifactNotes
            {
                FileMetadata = encoding.Options.FileMetadata,
                Limits = encoding.Limits,
                RowOrder = SourceOrder,
            });
            _fieldsToInclude = built.fieldsToInclude;
            _resolvedObjectIdFieldName = built.objectIdFieldName;
            _geometryColumnName = built.geometryColumnName;
            _bboxColumnName = built.bboxColumnName;
            _schemaIncludesBbox = encoding.IncludeGeometry && _includeCovering;

            _stream = CreateBoundedSpool();
            (_writer, _arrowProperties, _writerProperties) = OpenFileWriter(_stream, _schema, encoding.Limits);
        }

        private async Task RewriteExistingSpoolWithUpgradedSchemaAsync(CancellationToken cancellationToken)
        {
            var previousColumns = GetGeneratedColumns();
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
                var sourceColumns = MapUpgradeColumns(batches.Schema, previousColumns, GetGeneratedColumns());
                while (await batches.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false) is { } batch)
                {
                    using (batch)
                    using (var adapted = CopyToUpgradedSchema(batch, _schema!, sourceColumns))
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

        private GeneratedColumns GetGeneratedColumns() => new(
            encoding.IncludeGeometry ? _geometryColumnName : null,
            _schemaIncludesBbox ? _bboxColumnName : null);

        private void WriteRecordBatch(ImmutableArray<Feature> batchFeatures)
        {
            BinaryArray? geometryArray = null;
            StructArray? bboxArray = null;
            if (encoding.IncludeGeometry)
            {
                (geometryArray, bboxArray, _) = BuildGeometryArray(
                    batchFeatures, encoding.Srid, encoding.Options.ReturnZ, encoding.Options.ReturnM, encoding.Options.GeometryLimits, encoding.Options.Logger);
                if (!_includeCovering)
                {
                    bboxArray.Dispose();
                    bboxArray = null;
                }
            }

            var arrays = BuildArrays(
                batchFeatures, _schema!, encoding.Options.ReturnGeometry, geometryArray, bboxArray,
                _resolvedObjectIdFieldName, _fieldsToInclude!, _geometryColumnName, _bboxColumnName, encoding.Options.Logger);
            using var recordBatch = new RecordBatch(_schema!, arrays, batchFeatures.Length);
            WritePreparedRecordBatch(recordBatch);
        }

        private void WritePreparedRecordBatch(RecordBatch recordBatch)
        {
            if (encoding.Limits.RowsPerRowGroup is null)
            {
                _writer!.WriteRecordBatch(recordBatch, encoding.Limits.MaxRowsPerBatch);
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
                return new BoundedParquetStream(checked((int)OutputByteCap(encoding.Limits)));
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
