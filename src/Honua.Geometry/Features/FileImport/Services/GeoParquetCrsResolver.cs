// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Corvus.Text.Json.RuntimeEvaluator;
using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Validates source PROJJSON before resolving coordinate-bearing definitions to canonical storage CRS.
/// An asserted authority identifier is evidence to verify, never a substitute for the definition.
/// </summary>
internal static class GeoParquetCrsResolver
{
    private const int MaximumInputBytes = 262144;
    private const int MaximumDepth = 32;
    private static readonly JsonSchemaEvaluator _evaluator = CreateEvaluator();
    private static readonly object _evaluationLock = new();
    private static readonly (int Srid, JsonElement Definition)[] _definitions = LoadDefinitions();

    internal static int Resolve(JsonElement crs)
    {
        var json = crs.GetRawText();
        if (crs.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(json) > MaximumInputBytes)
        {
            throw new InvalidDataException("GeoParquet CRS must be a bounded PROJJSON CRS object.");
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            using var bounded = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            lock (_evaluationLock)
            {
                if (!_evaluator.Evaluate(bytes))
                {
                    throw new InvalidDataException("GeoParquet CRS does not satisfy the required PROJJSON structure.");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GeoParquet CRS exceeds the supported PROJJSON structural depth.", ex);
        }

        if (!crs.TryGetProperty("type"u8, out var type) ||
            type.GetString() is not ("GeographicCRS" or "GeodeticCRS" or "ProjectedCRS" or "VerticalCRS" or
                "CompoundCRS" or "BoundCRS" or "EngineeringCRS" or "TemporalCRS" or "ParametricCRS" or "DerivedProjectedCRS"))
        {
            throw new InvalidDataException("GeoParquet CRS must contain a PROJJSON coordinate reference system definition.");
        }

        foreach (var (srid, definition) in _definitions)
        {
            if (Equivalent(crs, definition, string.Empty))
            {
                ValidateAssertedIdentifier(crs, definition, srid);
                return srid;
            }
        }

        throw new NotSupportedException("GeoParquet CRS is valid PROJJSON but its complete definition cannot be resolved to a supported storage CRS.");
    }

    private static void ValidateAssertedIdentifier(JsonElement source, JsonElement canonical, int srid)
    {
        if (!source.TryGetProperty("id"u8, out var id))
        {
            return;
        }

        var authority = id.GetProperty("authority"u8).GetString();
        var code = id.GetProperty("code"u8);
        var codeText = code.ValueKind == JsonValueKind.String ? code.GetString() : code.GetRawText();
        if (string.Equals(authority, "EPSG", StringComparison.OrdinalIgnoreCase))
        {
            var hasCode = code.ValueKind == JsonValueKind.Number
                ? code.TryGetDecimal(out var number) && number == srid
                : int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var asserted) && asserted == srid;
            if (!hasCode ||
                canonical.GetProperty("id"u8).GetProperty("authority"u8).GetString() != "EPSG")
            {
                throw new InvalidDataException("GeoParquet CRS authority identifier conflicts with its coordinate reference system definition.");
            }
        }
        else if (string.Equals(authority, "OGC", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(codeText, "CRS84", StringComparison.OrdinalIgnoreCase) &&
                 canonical.GetProperty("id"u8).GetProperty("authority"u8).GetString() != "OGC")
        {
            throw new InvalidDataException("GeoParquet CRS authority identifier conflicts with its coordinate reference system definition.");
        }
    }

    private static bool Equivalent(JsonElement source, JsonElement canonical, string context)
    {
        if (source.ValueKind != canonical.ValueKind)
        {
            return false;
        }

        switch (source.ValueKind)
        {
            case JsonValueKind.Object:
                var sourceProperties = source.EnumerateObject().Where(p => !IgnoreProperty(p.Name, context)).ToArray();
                var canonicalProperties = canonical.EnumerateObject().Where(p => !IgnoreProperty(p.Name, context)).ToArray();
                if (sourceProperties.Length != canonicalProperties.Length)
                {
                    return false;
                }

                foreach (var property in sourceProperties)
                {
                    if (!canonical.TryGetProperty(property.Name, out var expected) ||
                        !Equivalent(property.Value, expected, property.Name))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                return source.GetArrayLength() == canonical.GetArrayLength() &&
                    source.EnumerateArray().Zip(canonical.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second, context));
            case JsonValueKind.Number:
                return source.TryGetDecimal(out var actualNumber) && canonical.TryGetDecimal(out var expectedNumber)
                    ? actualNumber == expectedNumber : source.GetDouble().Equals(canonical.GetDouble());
            case JsonValueKind.String:
                return source.GetString() == canonical.GetString();
            default:
                return source.GetRawText() == canonical.GetRawText();
        }
    }

    // Datum/frame and parameter names remain significant when identifiers are absent; matching is
    // deliberately conservative rather than equating distinct frames with the same ellipsoid.
    private static bool IgnoreProperty(string name, string context) =>
        name is "$schema" or "id" or "scope" or "area" or "bbox" or "remarks" or "description" or "abbreviation" ||
        (name == "name" && context is "" or "base_crs" or "conversion" or "coordinate_system" or "axis");

    private static (int Srid, JsonElement Definition)[] LoadDefinitions()
    {
        var definitions = new List<(int, JsonElement)>();
        using (var crs84 = JsonDocument.Parse(GeoParquetProjJsonCatalog.Crs84ProjJson))
        {
            definitions.Add((4326, crs84.RootElement.Clone()));
        }

        foreach (var srid in GeoParquetProjJsonCatalog.SupportedSrids.Order())
        {
            if (GeoParquetProjJsonCatalog.TryGetProjJson(srid, out var json))
            {
                using var definition = JsonDocument.Parse(json);
                definitions.Add((srid, definition.RootElement.Clone()));
            }
        }

        return definitions.ToArray();
    }

    private static JsonSchemaEvaluator CreateEvaluator()
    {
        using var stream = typeof(GeoParquetCrsResolver).Assembly.GetManifestResourceStream(
            "Honua.Core.Features.FileImport.Resources.projjson-v0.7.schema.json")
            ?? throw new InvalidOperationException("Embedded PROJJSON schema was not found.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return JsonSchemaEvaluator.Compile(bytes.ToArray(), new JsonSchemaEvaluatorOptions
        {
            CodeGeneration = JsonSchemaCodeGeneration.Disabled,
            DefaultDialect = JsonSchemaDialect.Draft7,
            MaxDepth = MaximumDepth,
            RegexMatchTimeout = TimeSpan.FromMilliseconds(100),
            DocumentResolver = RejectExternalDocument,
            FallbackDocumentResolver = RejectExternalDocument
        });
    }

    private static bool RejectExternalDocument(string uri, out ReadOnlyMemory<byte> document)
    {
        _ = uri;
        document = default;
        return false;
    }
}
