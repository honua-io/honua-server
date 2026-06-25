// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Emits a JSON Schema (draft 2020-12) document for a <see cref="ProcessDefinition"/>'s
/// typed input parameters, plus a compact output descriptor for the artifact kinds the
/// process produces. This is the authoring-contract schema surface the GP Devkit
/// <c>describe</c> command (issue #2124) consumes; it derives entirely from the typed
/// <see cref="ProcessParameterSpec"/> set rather than a hand-written schema, so the
/// advertised schema can never drift from the catalog (issue #2122).
/// </summary>
/// <remarks>
/// The document is written with a <see cref="Utf8JsonWriter"/> (no reflection-based
/// serialization) so it stays AOT-safe.
/// </remarks>
public static class ProcessSchemaJsonGenerator
{
    private const string SchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// Produces the JSON Schema document for the given process definition as a UTF-8
    /// JSON string. The root object has <c>processId</c>, <c>title</c>,
    /// <c>description</c>, <c>category</c>, <c>runtimeProfile</c>, an <c>inputs</c>
    /// JSON Schema object (with <c>properties</c>/<c>required</c>), and an
    /// <c>outputs</c> array describing the produced artifact kinds.
    /// </summary>
    /// <param name="process">The catalog process definition. Must not be null.</param>
    /// <param name="indented">Whether to pretty-print the output.</param>
    public static string Generate(ProcessDefinition process, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(process);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartObject();
            writer.WriteString("processId", process.ProcessId);
            writer.WriteString("title", process.Title);
            writer.WriteString("description", process.Description);
            writer.WriteString("category", process.Category);
            writer.WriteString("runtimeProfile", process.RuntimeProfile);

            WriteInputsSchema(writer, process);
            WriteOutputs(writer, process);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteInputsSchema(Utf8JsonWriter writer, ProcessDefinition process)
    {
        writer.WritePropertyName("inputs");
        writer.WriteStartObject();
        writer.WriteString("$schema", SchemaDialect);
        writer.WriteString("type", "object");

        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        foreach (var parameter in process.Parameters)
        {
            writer.WritePropertyName(parameter.Name);
            WriteParameterSchema(writer, parameter);
        }

        writer.WriteEndObject();

        // Required array — only the parameters flagged Required, in declared order.
        writer.WritePropertyName("required");
        writer.WriteStartArray();
        foreach (var parameter in process.Parameters)
        {
            if (parameter.Required)
            {
                writer.WriteStringValue(parameter.Name);
            }
        }

        writer.WriteEndArray();

        // Authoring contract: the parameter set is closed.
        writer.WriteBoolean("additionalProperties", false);

        writer.WriteEndObject();
    }

    private static void WriteParameterSchema(Utf8JsonWriter writer, ProcessParameterSpec parameter)
    {
        writer.WriteStartObject();

        var (jsonType, format) = MapType(parameter.ValueType);
        writer.WriteString("type", jsonType);
        if (format is not null)
        {
            writer.WriteString("format", format);
        }

        writer.WriteString("title", parameter.DisplayName);
        writer.WriteString("description", parameter.Description);

        if (parameter.DefaultValue is not null)
        {
            writer.WriteString("default", parameter.DefaultValue);
        }

        if (parameter.AllowedValues is { Count: > 0 })
        {
            writer.WritePropertyName("enum");
            writer.WriteStartArray();
            foreach (var allowed in parameter.AllowedValues)
            {
                writer.WriteStringValue(allowed);
            }

            writer.WriteEndArray();
        }

        // Surface the canonical Honua value type so authoring tools can render a
        // precise editor (e.g. a WKB picker for Wkb, a layer chooser for LayerId)
        // beyond the coarse JSON Schema primitive.
        writer.WriteString("x-honua-value-type", parameter.ValueType.ToString());

        writer.WriteEndObject();
    }

    private static void WriteOutputs(Utf8JsonWriter writer, ProcessDefinition process)
    {
        writer.WritePropertyName("outputs");
        writer.WriteStartArray();
        foreach (var kind in process.OutputArtifactKinds)
        {
            writer.WriteStartObject();
            writer.WriteString("artifactKind", kind.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Maps a Honua <see cref="ProcessParameterValueType"/> to a JSON Schema
    /// <c>(type, format)</c> pair. Geometry/array/identifier types are represented by
    /// their closest JSON Schema primitive plus a descriptive <c>format</c>; the
    /// precise type is also carried on <c>x-honua-value-type</c>.
    /// </summary>
    private static (string Type, string? Format) MapType(ProcessParameterValueType valueType)
        => valueType switch
        {
            ProcessParameterValueType.Text => ("string", null),
            ProcessParameterValueType.WholeNumber => ("integer", "int32"),
            ProcessParameterValueType.FloatingPoint => ("number", "double"),
            ProcessParameterValueType.Flag => ("boolean", null),
            ProcessParameterValueType.Wkb => ("string", "wkb-base64"),
            ProcessParameterValueType.WkbArray => ("array", "wkb-base64"),
            ProcessParameterValueType.Srid => ("integer", "srid"),
            ProcessParameterValueType.LayerId => ("string", "layer-id"),
            _ => ("string", null),
        };
}
