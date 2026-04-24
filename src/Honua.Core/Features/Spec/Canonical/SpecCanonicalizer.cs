// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using NetTopologySuite.IO;

namespace Honua.Core.Features.Spec.Canonical;

/// <summary>
/// Deterministic canonical-JSON emitter for <see cref="SpecDocument"/>.
/// Emits UTF-8 bytes via <see cref="Utf8JsonWriter"/> with alphabetically
/// sorted object keys, arrays preserving declaration order, and no ambient
/// state. Comments from the text form are preserved under
/// <c>meta.comments</c> keyed by JSON-Pointer path.
/// </summary>
public sealed class SpecCanonicalizer : ISpecCanonicalizer
{
    private static readonly WKTWriter _wktWriter = new();

    /// <summary>
    /// Serializes <paramref name="document"/> to canonical-JSON UTF-8 bytes.
    /// </summary>
    /// <param name="document">AST to serialize.</param>
    /// <param name="indent"><c>true</c> for pretty-printed output (testing/tools only — hash inputs should pass <c>false</c>).</param>
    /// <returns>Canonical JSON bytes.</returns>
    public byte[] ToUtf8(SpecDocument document, bool indent = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream(1024);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indent,
            SkipValidation = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            WriteDocument(writer, document);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Serializes <paramref name="document"/> to a canonical-JSON UTF-16 string.
    /// </summary>
    /// <param name="document">AST to serialize.</param>
    /// <param name="indent"><c>true</c> for pretty-printed output.</param>
    /// <returns>Canonical JSON.</returns>
    public string ToJson(SpecDocument document, bool indent = false)
    {
        var bytes = ToUtf8(document, indent);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteDocument(Utf8JsonWriter writer, SpecDocument document)
    {
        // Canonical root: alphabetically sorted.
        // $schema, capabilities, compute, grammar, kind, map, meta, outputs, scope, sources, title
        writer.WriteStartObject();

        writer.WriteString("$schema", SpecGrammarVersion.SchemaUrl);

        writer.WritePropertyName("capabilities");
        writer.WriteStartObject();
        writer.WriteString("operators", SpecGrammarVersion.CurrentOperatorCapability);
        writer.WriteEndObject();

        writer.WritePropertyName("compute");
        WriteComputeArray(writer, document);

        writer.WriteString("grammar", document.Grammar ?? SpecGrammarVersion.Current);

        if (!string.IsNullOrEmpty(document.Kind))
        {
            writer.WriteString("kind", document.Kind);
        }

        WriteMap(writer, document);

        WriteMeta(writer, document);

        WriteOutputs(writer, document);

        WriteScope(writer, document);

        WriteSources(writer, document);

        if (!string.IsNullOrEmpty(document.Title))
        {
            writer.WriteString("title", document.Title);
        }

        writer.WriteEndObject();
    }

    private static void WriteSources(Utf8JsonWriter writer, SpecDocument document)
    {
        writer.WritePropertyName("sources");
        writer.WriteStartArray();
        foreach (var source in document.Sources)
        {
            writer.WriteStartObject();
            writer.WriteString("id", source.Id);
            WriteObjectFieldsSorted(writer, source.Properties);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteScope(Utf8JsonWriter writer, SpecDocument document)
    {
        if (document.Scopes.IsDefaultOrEmpty || document.Scopes.Length == 0)
        {
            return;
        }

        writer.WritePropertyName("scope");
        writer.WriteStartArray();
        foreach (var clause in document.Scopes)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("target");
            WriteExpression(writer, clause.Target);
            if (clause.Where is not null)
            {
                writer.WritePropertyName("where");
                WriteExpression(writer, clause.Where);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteComputeArray(Utf8JsonWriter writer, SpecDocument document)
    {
        writer.WriteStartArray();
        foreach (var step in document.Compute)
        {
            writer.WriteStartObject();
            writer.WriteString("id", step.Id);
            if (step.Inputs is not null)
            {
                writer.WritePropertyName("inputs");
                WriteExpression(writer, step.Inputs);
            }

            writer.WriteString("op", step.OperatorName);

            if (step.Parameters is not null)
            {
                writer.WritePropertyName("params");
                WriteExpression(writer, step.Parameters);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMap(Utf8JsonWriter writer, SpecDocument document)
    {
        if (document.Map is null)
        {
            return;
        }

        writer.WritePropertyName("map");
        writer.WriteStartObject();

        if (document.Map.Layers is not null)
        {
            writer.WritePropertyName("layers");
            WriteExpression(writer, document.Map.Layers);
        }

        if (document.Map.Legend is not null)
        {
            writer.WritePropertyName("legend");
            WriteExpression(writer, document.Map.Legend);
        }

        if (document.Map.Viewport is not null)
        {
            writer.WritePropertyName("viewport");
            WriteExpression(writer, document.Map.Viewport);
        }

        writer.WriteEndObject();
    }

    private static void WriteOutputs(Utf8JsonWriter writer, SpecDocument document)
    {
        if (document.Outputs.IsDefaultOrEmpty || document.Outputs.Length == 0)
        {
            return;
        }

        writer.WritePropertyName("outputs");
        writer.WriteStartArray();
        foreach (var output in document.Outputs)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("expr");
            WriteExpression(writer, output.Expression);
            writer.WriteString("id", output.Id);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMeta(Utf8JsonWriter writer, SpecDocument document)
    {
        if (document.Comments.Count == 0)
        {
            return;
        }

        writer.WritePropertyName("meta");
        writer.WriteStartObject();

        writer.WritePropertyName("comments");
        writer.WriteStartObject();
        var keys = new string[document.Comments.Count];
        var i = 0;
        foreach (var key in document.Comments.Keys)
        {
            keys[i++] = key;
        }

        Array.Sort(keys, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            writer.WriteString(key, document.Comments[key]);
        }

        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteExpression(Utf8JsonWriter writer, SpecExpression expression)
    {
        switch (expression)
        {
            case LiteralNode literal:
                WriteLiteral(writer, literal);
                break;

            case GeometryLiteral geometry:
                writer.WriteStartObject();
                writer.WriteString("crs", geometry.Crs ?? string.Empty);
                writer.WriteString("type", "geometry");
                writer.WriteString("wkt", _wktWriter.Write(geometry.Geometry));
                writer.WriteEndObject();
                break;

            case Cql2Expression cql2:
                writer.WriteStartObject();
                writer.WriteString("cql2", cql2.Cql2Text);
                writer.WriteEndObject();
                break;

            case ReferenceNode reference:
                writer.WriteStringValue(reference.Canonical);
                break;

            case ArrayLiteral array:
                writer.WriteStartArray();
                foreach (var item in array.Items)
                {
                    WriteExpression(writer, item);
                }

                writer.WriteEndArray();
                break;

            case ObjectExpression obj:
                writer.WriteStartObject();
                WriteObjectFieldsSorted(writer, obj);
                writer.WriteEndObject();
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteObjectFieldsSorted(Utf8JsonWriter writer, ObjectExpression obj)
    {
        if (obj.Fields.IsDefaultOrEmpty || obj.Fields.Length == 0)
        {
            return;
        }

        var keys = ArrayPool<int>.Shared.Rent(obj.Fields.Length);
        try
        {
            for (var i = 0; i < obj.Fields.Length; i++)
            {
                keys[i] = i;
            }

            Array.Sort(
                keys,
                0,
                obj.Fields.Length,
                new ObjectFieldComparer(obj));

            for (var i = 0; i < obj.Fields.Length; i++)
            {
                var field = obj.Fields[keys[i]];
                writer.WritePropertyName(field.Key);
                WriteExpression(writer, field.Value);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(keys);
        }
    }

    private static void WriteLiteral(Utf8JsonWriter writer, LiteralNode literal)
    {
        switch (literal.Kind)
        {
            case SpecTypeKind.String:
                writer.WriteStringValue(literal.String ?? string.Empty);
                break;

            case SpecTypeKind.Boolean:
                writer.WriteBooleanValue(literal.Boolean ?? false);
                break;

            case SpecTypeKind.Integer:
                writer.WriteNumberValue(literal.Integer ?? 0);
                break;

            case SpecTypeKind.Number:
                writer.WriteNumberValue(literal.Number ?? 0);
                break;

            case SpecTypeKind.Distance:
            case SpecTypeKind.Duration:
            case SpecTypeKind.Area:
                writer.WriteStartObject();
                writer.WriteString("kind", literal.Kind.ToString().ToLowerInvariant());
                writer.WriteString("unit", literal.Unit ?? string.Empty);
                if (literal.Number is { } number)
                {
                    writer.WriteNumber("value", number);
                }

                writer.WriteEndObject();
                break;

            case SpecTypeKind.Crs:
                writer.WriteStringValue(literal.String ?? string.Empty);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private sealed class ObjectFieldComparer : IComparer<int>
    {
        private readonly ObjectExpression _obj;

        public ObjectFieldComparer(ObjectExpression obj)
        {
            _obj = obj;
        }

        public int Compare(int x, int y)
            => string.CompareOrdinal(_obj.Fields[x].Key, _obj.Fields[y].Key);
    }
}
