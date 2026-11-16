// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services.FileGdb;

internal static class FileGdbReader
{
    internal static int? DetectSrid(string gdbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gdbPath);

        if (!Directory.Exists(gdbPath))
        {
            throw new DirectoryNotFoundException($"File Geodatabase directory not found: {gdbPath}");
        }

        var layers = DiscoverLayers(gdbPath);
        for (var i = 0; i < layers.Length; i++)
        {
            if (layers[i].Srid > 0)
            {
                return layers[i].Srid;
            }
        }

        return null;
    }

    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        string gdbPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gdbPath);

        if (!Directory.Exists(gdbPath))
        {
            throw new DirectoryNotFoundException($"File Geodatabase directory not found: {gdbPath}");
        }

        var layers = DiscoverLayers(gdbPath);
        if (layers.Length == 0)
        {
            yield break;
        }

        if (layers.Length > 1)
        {
            throw new InvalidDataException(BuildMultiLayerImportMessage(layers));
        }

        await foreach (var feature in ReadLayerStreamingAsync(gdbPath, layers[0], cancellationToken))
        {
            yield return feature;
        }
    }

    internal static async IAsyncEnumerable<IFeature> ReadLayerStreamingAsync(
        string gdbPath,
        FileGdbLayerInfo layer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gdbPath);

        if (!Directory.Exists(gdbPath))
        {
            throw new DirectoryNotFoundException($"File Geodatabase directory not found: {gdbPath}");
        }

        var recordIndex = 0;
        // layer.FileNumber is an int formatted as 8 hex digits, so this segment can never be
        // rooted/absolute — Path.Combine cannot silently drop gdbPath here.
        var tablePath = Path.Join(gdbPath, $"a{layer.FileNumber:x8}.gdbtable");

        if (!File.Exists(tablePath))
        {
            yield break;
        }

        GdbTableReader tableReader;
        try
        {
            tableReader = GdbTableReader.Open(tablePath);
        }
        catch (InvalidDataException)
        {
            yield break;
        }
        catch (EndOfStreamException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        using (tableReader)
        {
            if (tableReader.RowCount == 0)
            {
                yield break;
            }

            var geomDef = tableReader.GetGeometryDef();
            if (geomDef == null)
            {
                yield break;
            }

            // An unresolved spatial reference stays SRID 0 (unknown) rather than defaulting to
            // 4326: ESRI WKT without an AUTHORITY node (State Plane, UTM, ...) is resolved by the
            // import service through ICrsDetectionService or an explicit SourceSrid, and a 4326
            // stamp here would silently store projected coordinates as degrees (#6114).
            var geometryFactory = new GeometryFactory(new PrecisionModel(), Math.Max(geomDef.Srid, 0));
            var geomReader = new GdbGeometryReader(geometryFactory, geomDef);

            // Find the geometry field index.
            var geomFieldIndex = -1;
            for (var i = 0; i < tableReader.Fields.Count; i++)
            {
                if (tableReader.Fields[i].FieldType == GdbFieldType.Geometry)
                {
                    geomFieldIndex = i;
                    break;
                }
            }

            foreach (var row in tableReader.ReadRows())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var feature = ConvertRowToFeature(row, tableReader.Fields, geomFieldIndex, geomReader);
                if (feature != null)
                {
                    yield return feature;
                }

                if (++recordIndex % 256 == 0)
                {
                    await Task.Yield();
                }
            }
        }
    }

    internal static string BuildMultiLayerImportMessage(IReadOnlyList<FileGdbLayerInfo> layers)
    {
        var layerNames = string.Join(", ", layers.Select(layer => layer.Name));
        return $"File Geodatabase contains multiple feature classes ({layerNames}). Import requires a single feature class; preview AvailableLayers lists the source layers to export or split before import.";
    }

    internal static FileGdbLayerInfo[] DiscoverLayers(string gdbPath)
    {
        // "a00000001.gdbtable" is a hardcoded literal, so this segment can never be
        // rooted/absolute — Path.Combine cannot silently drop gdbPath here.
        var catalogPath = Path.Join(gdbPath, "a00000001.gdbtable");
        if (File.Exists(catalogPath))
        {
            var layers = DiscoverLayersFromCatalog(gdbPath, catalogPath);
            if (layers.Length > 0)
            {
                return layers;
            }
        }

        // Fallback: scan .gdbtable files directly when catalog is unavailable.
        return DiscoverLayersByScan(gdbPath);
    }

    private static FileGdbLayerInfo[] DiscoverLayersFromCatalog(string gdbPath, string catalogPath)
    {
        GdbTableReader catalogReader;
        try
        {
            catalogReader = GdbTableReader.Open(catalogPath);
        }
        catch (InvalidDataException)
        {
            return [];
        }

        var layers = new List<FileGdbLayerInfo>();

        using (catalogReader)
        {
            // Find field indices for "Name" in the catalog.
            var nameFieldIndex = -1;
            for (var i = 0; i < catalogReader.Fields.Count; i++)
            {
                if (string.Equals(catalogReader.Fields[i].Name, "Name", StringComparison.OrdinalIgnoreCase))
                {
                    nameFieldIndex = i;
                    break;
                }
            }

            if (nameFieldIndex < 0)
            {
                return [];
            }

            // The row's ObjectID (1-based) maps to filename a{objectId:08x}.gdbtable.
            var objectId = 0;
            foreach (var row in catalogReader.ReadRows())
            {
                objectId++;

                if (!row.TryGetValue(catalogReader.Fields[nameFieldIndex].Name, out var nameObj) ||
                    nameObj is not string tableName)
                {
                    continue;
                }

                // Skip system catalog tables (GDB_ prefix).
                if (tableName.StartsWith("GDB_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Probe the table file to check if it has a geometry field.
                var fileNumber = objectId;
                // fileNumber is an int formatted as 8 hex digits, so this segment can never be
                // rooted/absolute — Path.Combine cannot silently drop gdbPath here.
                var probeTablePath = Path.Join(gdbPath, $"a{fileNumber:x8}.gdbtable");
                if (!File.Exists(probeTablePath))
                {
                    continue;
                }

                var layerInfo = ProbeTable(probeTablePath, tableName, fileNumber);
                if (layerInfo != null)
                {
                    layers.Add(layerInfo.Value);
                }
            }
        }

        return layers.ToArray();
    }

    private static FileGdbLayerInfo[] DiscoverLayersByScan(string gdbPath)
    {
        var layers = new List<FileGdbLayerInfo>();

        foreach (var tablePath in Directory.GetFiles(gdbPath, "a*.gdbtable"))
        {
            var fileName = Path.GetFileNameWithoutExtension(tablePath);
            if (fileName.Length < 2 || !int.TryParse(fileName[1..], System.Globalization.NumberStyles.HexNumber,
                    null, out var fileNumber))
            {
                continue;
            }

            // Skip system tables (IDs 1-8: catalog, dbtune, spatialrefs, items, itemtypes, etc.)
            if (fileNumber <= 8)
            {
                continue;
            }

            var layerInfo = ProbeTable(tablePath, fileName, fileNumber);
            if (layerInfo != null)
            {
                layers.Add(layerInfo.Value);
            }
        }

        return layers.ToArray();
    }

    private static FileGdbLayerInfo? ProbeTable(string tablePath, string tableName, int fileNumber)
    {
        GdbTableReader tableReader;
        try
        {
            tableReader = GdbTableReader.Open(tablePath);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        using (tableReader)
        {
            var geomDef = tableReader.GetGeometryDef();
            if (geomDef == null)
            {
                return null;
            }

            return new FileGdbLayerInfo(tableName, tableReader.RowCount, geomDef.Srid, fileNumber, geomDef.WktSpatialReference);
        }
    }

    private static Feature? ConvertRowToFeature(
        Dictionary<string, object?> row,
        IReadOnlyList<GdbField> fields,
        int geomFieldIndex,
        GdbGeometryReader geomReader)
    {
        NtsGeometry? geometry = null;

        // Parse geometry from the geometry field's blob.
        if (geomFieldIndex >= 0)
        {
            var geomFieldName = fields[geomFieldIndex].Name;
            if (row.TryGetValue(geomFieldName, out var geomObj) && geomObj is byte[] geomBlob)
            {
                geometry = geomReader.ReadGeometry(geomBlob);
                row.Remove(geomFieldName);
            }
        }

        // Build attributes table from remaining fields.
        var attributes = new AttributesTable();
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];

            // Skip ObjectID and geometry fields in attributes.
            if (field.FieldType == GdbFieldType.ObjectId || field.FieldType == GdbFieldType.Geometry)
            {
                continue;
            }

            if (row.TryGetValue(field.Name, out var value))
            {
                attributes.Add(field.Name, value);
            }
        }

        return new Feature(geometry, attributes);
    }

    internal readonly record struct FileGdbLayerInfo(
        string Name,
        int RowCount,
        int Srid,
        int FileNumber,
        string? SpatialReferenceWkt = null);
}
