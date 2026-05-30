// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Derives the source property set for an OGC API Features collection (either from the advertised
/// JSON schema document or inferred from the first page of features) and compares it to the
/// target table columns to emit <see cref="OgcApiFeaturesSchemaMappingDiagnostic"/> entries.
/// </summary>
/// <remarks>
/// <para>
/// The mapper is intentionally side-effect free: callers supply the parsed first-page JSON document
/// plus the optional schema document and receive a deterministic list of diagnostics. The classifier
/// is conservative — only properties that need operator attention are reported, so a clean run
/// returns an empty list.
/// </para>
/// </remarks>
internal static class OgcApiFeaturesSchemaMapper
{
    /// <summary>
    /// Derives the source property set from <paramref name="schemaDocument"/> (when supplied) or
    /// infers it from <paramref name="firstPageFeatures"/>. Schema-derived types win when both
    /// sources advertise a property, since the schema is authoritative.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DeriveSourceProperties(
        JsonElement? schemaDocument,
        JsonElement firstPageFeatures)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (firstPageFeatures.ValueKind == JsonValueKind.Array)
        {
            foreach (var feature in firstPageFeatures.EnumerateArray())
            {
                if (feature.ValueKind != JsonValueKind.Object ||
                    !feature.TryGetProperty("properties", out var props) ||
                    props.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var member in props.EnumerateObject())
                {
                    if (properties.ContainsKey(member.Name))
                    {
                        continue;
                    }

                    properties[member.Name] = InferSourceType(member.Value);
                }
            }
        }

        if (schemaDocument.HasValue)
        {
            var schemaProperties = ReadSchemaProperties(schemaDocument.Value);
            foreach (var (name, type) in schemaProperties)
            {
                // Schema is authoritative when present, but we still preserve inferred properties
                // that the schema omits so we surface them in the diagnostic comparison.
                properties[name] = type;
            }
        }

        return properties;
    }

    /// <summary>
    /// Compares the derived source property set to the target columns and emits one diagnostic per
    /// non-automated mapping. The output is deterministic (ordered by property name) so snapshots
    /// and JSON round-trip tests are stable.
    /// </summary>
    public static IReadOnlyList<OgcApiFeaturesSchemaMappingDiagnostic> Diagnose(
        IReadOnlyDictionary<string, string> sourceProperties,
        IReadOnlyList<OgcApiFeaturesSinkColumn> targetColumns)
    {
        if (sourceProperties.Count == 0 || targetColumns.Count == 0)
        {
            return [];
        }

        var byName = new Dictionary<string, OgcApiFeaturesSinkColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in targetColumns)
        {
            byName[column.Name] = column;
        }

        var diagnostics = new List<OgcApiFeaturesSchemaMappingDiagnostic>();
        foreach (var (propertyName, sourceType) in sourceProperties.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (!byName.TryGetValue(propertyName, out var column))
            {
                diagnostics.Add(new OgcApiFeaturesSchemaMappingDiagnostic
                {
                    PropertyName = propertyName,
                    SourceType = sourceType,
                    TargetColumnType = null,
                    Classification = OgcApiFeaturesSchemaMappingClassification.Unsupported,
                    Severity = "error",
                    Reason = $"Source property '{propertyName}' ({sourceType}) has no matching column in the target table; the value will not be projected."
                });
                continue;
            }

            var classification = Classify(sourceType, column.DataType);
            switch (classification)
            {
                case OgcApiFeaturesSchemaMappingClassification.Automated:
                    continue;
                case OgcApiFeaturesSchemaMappingClassification.Assisted:
                    diagnostics.Add(new OgcApiFeaturesSchemaMappingDiagnostic
                    {
                        PropertyName = propertyName,
                        SourceType = sourceType,
                        TargetColumnType = column.DataType,
                        Classification = OgcApiFeaturesSchemaMappingClassification.Assisted,
                        Severity = "info",
                        Reason = $"Source property '{propertyName}' ({sourceType}) widens to target column type {column.DataType}; conversion is lossless."
                    });
                    break;
                case OgcApiFeaturesSchemaMappingClassification.ManualReview:
                    diagnostics.Add(new OgcApiFeaturesSchemaMappingDiagnostic
                    {
                        PropertyName = propertyName,
                        SourceType = sourceType,
                        TargetColumnType = column.DataType,
                        Classification = OgcApiFeaturesSchemaMappingClassification.ManualReview,
                        Severity = "warning",
                        Reason = $"Source property '{propertyName}' ({sourceType}) narrows to target column type {column.DataType}; conversion may truncate or fail at write time."
                    });
                    break;
                case OgcApiFeaturesSchemaMappingClassification.Unsupported:
                default:
                    diagnostics.Add(new OgcApiFeaturesSchemaMappingDiagnostic
                    {
                        PropertyName = propertyName,
                        SourceType = sourceType,
                        TargetColumnType = column.DataType,
                        Classification = OgcApiFeaturesSchemaMappingClassification.Unsupported,
                        Severity = "error",
                        Reason = $"Source property '{propertyName}' ({sourceType}) cannot be converted to target column type {column.DataType}."
                    });
                    break;
            }
        }

        return diagnostics;
    }

    private static Dictionary<string, string> ReadSchemaProperties(JsonElement schemaDocument)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (schemaDocument.ValueKind != JsonValueKind.Object ||
            !schemaDocument.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var member in properties.EnumerateObject())
        {
            if (member.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ReadSchemaType(member.Value);
            if (type != null)
            {
                result[member.Name] = type;
            }
        }

        return result;
    }

    private static string? ReadSchemaType(JsonElement propertyElement)
    {
        if (!propertyElement.TryGetProperty("type", out var typeElement))
        {
            return null;
        }

        var primary = typeElement.ValueKind switch
        {
            JsonValueKind.String => typeElement.GetString(),
            JsonValueKind.Array => typeElement
                .EnumerateArray()
                .Where(static element => element.ValueKind == JsonValueKind.String)
                .Select(static element => element.GetString())
                .FirstOrDefault(static value => !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(primary))
        {
            return null;
        }

        if (string.Equals(primary, "string", StringComparison.OrdinalIgnoreCase) &&
            propertyElement.TryGetProperty("maxLength", out var maxLength) &&
            maxLength.ValueKind == JsonValueKind.Number &&
            maxLength.TryGetInt32(out var length) &&
            length > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"varchar({length})");
        }

        if (string.Equals(primary, "integer", StringComparison.OrdinalIgnoreCase) &&
            propertyElement.TryGetProperty("format", out var formatElement) &&
            formatElement.ValueKind == JsonValueKind.String)
        {
            var format = formatElement.GetString();
            if (string.Equals(format, "int64", StringComparison.OrdinalIgnoreCase))
            {
                return "bigint";
            }

            if (string.Equals(format, "int32", StringComparison.OrdinalIgnoreCase))
            {
                return "integer";
            }
        }

        return primary.ToLowerInvariant();
    }

    private static string InferSourceType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => "unknown"
    };

    private static OgcApiFeaturesSchemaMappingClassification Classify(string sourceType, string targetType)
    {
        var normalizedSource = NormalizeType(sourceType);
        var normalizedTarget = NormalizeType(targetType);

        if (string.Equals(normalizedSource.family, normalizedTarget.family, StringComparison.Ordinal) &&
            normalizedSource.precision == normalizedTarget.precision)
        {
            return OgcApiFeaturesSchemaMappingClassification.Automated;
        }

        // Integer family: int < bigint
        if (normalizedSource.family == "integer" && normalizedTarget.family == "integer")
        {
            return normalizedSource.precision <= normalizedTarget.precision
                ? OgcApiFeaturesSchemaMappingClassification.Assisted
                : OgcApiFeaturesSchemaMappingClassification.ManualReview;
        }

        // String family: varchar(N) widens to text or larger varchar
        if (normalizedSource.family == "string" && normalizedTarget.family == "string")
        {
            // Both are bounded varchar(N).
            if (normalizedSource.precision > 0 && normalizedTarget.precision > 0)
            {
                return normalizedSource.precision <= normalizedTarget.precision
                    ? OgcApiFeaturesSchemaMappingClassification.Assisted
                    : OgcApiFeaturesSchemaMappingClassification.ManualReview;
            }

            // Bounded → unbounded text widens; unbounded → bounded narrows.
            if (normalizedSource.precision > 0 && normalizedTarget.precision == 0)
            {
                return OgcApiFeaturesSchemaMappingClassification.Assisted;
            }

            if (normalizedSource.precision == 0 && normalizedTarget.precision > 0)
            {
                return OgcApiFeaturesSchemaMappingClassification.ManualReview;
            }

            return OgcApiFeaturesSchemaMappingClassification.Automated;
        }

        // Numeric widening across families: integer → number/double is widening.
        if (normalizedSource.family == "integer" && normalizedTarget.family == "number")
        {
            return OgcApiFeaturesSchemaMappingClassification.Assisted;
        }

        if (normalizedSource.family == "number" && normalizedTarget.family == "integer")
        {
            return OgcApiFeaturesSchemaMappingClassification.ManualReview;
        }

        return OgcApiFeaturesSchemaMappingClassification.Unsupported;
    }

    private static (string family, int precision) NormalizeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return ("unknown", 0);
        }

        var trimmed = type.Trim().ToLowerInvariant();
        var parenIndex = trimmed.IndexOf('(');
        var bareName = parenIndex < 0 ? trimmed : trimmed[..parenIndex].Trim();
        var precision = 0;

        if (parenIndex >= 0)
        {
            var closeIndex = trimmed.IndexOf(')', parenIndex + 1);
            if (closeIndex > parenIndex)
            {
                var inner = trimmed[(parenIndex + 1)..closeIndex];
                var commaIndex = inner.IndexOf(',');
                var precisionToken = commaIndex < 0 ? inner : inner[..commaIndex];
                if (int.TryParse(precisionToken.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    precision = parsed;
                }
            }
        }

        return bareName switch
        {
            "smallint" or "int2" => ("integer", 16),
            "integer" or "int" or "int4" => ("integer", 32),
            "bigint" or "int8" or "long" => ("integer", 64),
            "real" or "float4" or "float" => ("number", 32),
            "double" or "double precision" or "float8" or "numeric" or "decimal" or "number" => ("number", 64),
            "varchar" or "character varying" => ("string", precision),
            "char" or "character" => ("string", precision == 0 ? 1 : precision),
            "text" or "string" => ("string", 0),
            "boolean" or "bool" => ("boolean", 0),
            "date" => ("date", 0),
            "timestamp" or "timestamptz" or "datetime" or "date-time" => ("timestamp", 0),
            "object" or "jsonb" or "json" => ("object", 0),
            "array" => ("array", 0),
            _ => (bareName, precision)
        };
    }
}
