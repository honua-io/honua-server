// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Spec.Domain;
using NetTopologySuite.IO;

namespace Honua.Core.Features.Spec.Canonical;

/// <summary>
/// Reads a canonical-JSON spec document back into a <see cref="SpecDocument"/>
/// AST. Used by the round-trip tests and by downstream consumers (grounding,
/// plan/apply) that receive the canonical form over the wire.
/// </summary>
/// <remarks>
/// The reader is a focused structural deserializer rather than a general
/// <see cref="System.Text.Json.JsonSerializer"/> adapter: it consumes
/// <see cref="JsonElement"/> nodes directly so no reflection or runtime
/// polymorphism is involved (keeping the path AOT-safe).
/// </remarks>
public static class SpecJsonReader
{
    private static readonly WKTReader _wktReader = new();

    /// <summary>
    /// Reads the canonical JSON string into a <see cref="SpecDocument"/>.
    /// </summary>
    /// <param name="json">Canonical JSON.</param>
    /// <returns>Parsed AST.</returns>
    public static SpecDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        return ReadDocument(doc.RootElement);
    }

    /// <summary>
    /// Reads the canonical JSON from a UTF-8 byte sequence.
    /// </summary>
    /// <param name="utf8">Canonical JSON bytes.</param>
    /// <returns>Parsed AST.</returns>
    public static SpecDocument Read(ReadOnlySpan<byte> utf8)
    {
        using var doc = JsonDocument.Parse(utf8.ToArray());
        return ReadDocument(doc.RootElement);
    }


    private static SpecDocument ReadDocument(JsonElement root)
    {
        var grammar = TryGetString(root, "grammar");
        var kind = TryGetString(root, "kind");
        var title = TryGetString(root, "title");

        var sources = ImmutableArray.CreateBuilder<SourceBinding>();
        if (root.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sourcesEl.EnumerateArray())
            {
                var id = TryGetString(source, "id") ?? string.Empty;
                var props = ImmutableArray.CreateBuilder<ObjectField>();
                foreach (var prop in source.EnumerateObject())
                {
                    if (prop.NameEquals("id"))
                    {
                        continue;
                    }

                    props.Add(new ObjectField(
                        SourceSpan.Synthetic,
                        SourceSpan.Synthetic,
                        prop.Name,
                        ReadExpression(prop.Value)));
                }

                sources.Add(new SourceBinding(
                    SourceSpan.Synthetic,
                    id,
                    new ObjectExpression(SourceSpan.Synthetic, props.ToImmutable())));
            }
        }

        var scopes = ImmutableArray.CreateBuilder<ScopeClause>();
        if (root.TryGetProperty("scope", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var clause in scopeEl.EnumerateArray())
            {
                ReferenceNode? target = null;
                SpecExpression? where = null;
                if (clause.TryGetProperty("target", out var targetEl))
                {
                    if (ReadExpression(targetEl) is ReferenceNode r)
                    {
                        target = r;
                    }
                }

                if (clause.TryGetProperty("where", out var whereEl))
                {
                    where = ReadExpression(whereEl);
                }

                if (target is not null)
                {
                    scopes.Add(new ScopeClause(SourceSpan.Synthetic, target, where));
                }
            }
        }

        var compute = ImmutableArray.CreateBuilder<ComputeStep>();
        if (root.TryGetProperty("compute", out var computeEl) && computeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in computeEl.EnumerateArray())
            {
                var id = TryGetString(step, "id") ?? string.Empty;
                var op = TryGetString(step, "op") ?? string.Empty;

                ObjectExpression? inputs = null;
                if (step.TryGetProperty("inputs", out var inputsEl))
                {
                    inputs = ReadExpression(inputsEl) as ObjectExpression;
                }

                ObjectExpression? parameters = null;
                if (step.TryGetProperty("params", out var paramsEl))
                {
                    parameters = ReadExpression(paramsEl) as ObjectExpression;
                }

                compute.Add(new ComputeStep(
                    SourceSpan.Synthetic,
                    id,
                    op,
                    SourceSpan.Synthetic,
                    inputs,
                    parameters));
            }
        }

        MapSpec? map = null;
        if (root.TryGetProperty("map", out var mapEl) && mapEl.ValueKind == JsonValueKind.Object)
        {
            ArrayLiteral? layers = null;
            if (mapEl.TryGetProperty("layers", out var layersEl))
            {
                layers = ReadExpression(layersEl) as ArrayLiteral;
            }

            ObjectExpression? viewport = null;
            if (mapEl.TryGetProperty("viewport", out var viewportEl))
            {
                viewport = ReadExpression(viewportEl) as ObjectExpression;
            }

            ObjectExpression? legend = null;
            if (mapEl.TryGetProperty("legend", out var legendEl))
            {
                legend = ReadExpression(legendEl) as ObjectExpression;
            }

            map = new MapSpec(SourceSpan.Synthetic, layers, viewport, legend);
        }

        var outputs = ImmutableArray.CreateBuilder<OutputBinding>();
        if (root.TryGetProperty("outputs", out var outputsEl) && outputsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputsEl.EnumerateArray())
            {
                var id = TryGetString(output, "id") ?? string.Empty;
                SpecExpression expression = output.TryGetProperty("expr", out var exprEl)
                    ? ReadExpression(exprEl)
                    : new LiteralNode(SourceSpan.Synthetic, SpecTypeKind.Unknown);

                outputs.Add(new OutputBinding(SourceSpan.Synthetic, id, expression));
            }
        }

        var comments = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("meta", out var metaEl) &&
            metaEl.ValueKind == JsonValueKind.Object &&
            metaEl.TryGetProperty("comments", out var commentsEl) &&
            commentsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in commentsEl.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    comments[entry.Name] = entry.Value.GetString() ?? string.Empty;
                }
            }
        }

        return new SpecDocument(
            SourceSpan.Synthetic,
            grammar,
            SourceSpan.Synthetic,
            kind,
            title,
            sources.ToImmutable(),
            scopes.ToImmutable(),
            compute.ToImmutable(),
            map,
            outputs.ToImmutable(),
            comments.ToImmutable());
    }

    private static SpecExpression ReadExpression(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var text = element.GetString() ?? string.Empty;
                    if (text.StartsWith('@'))
                    {
                        return ParseReferenceString(text);
                    }

                    return new LiteralNode(SourceSpan.Synthetic, SpecTypeKind.String, String: text);
                }

            case JsonValueKind.Number:
                {
                    if (element.TryGetInt64(out var asInt))
                    {
                        return new LiteralNode(
                            SourceSpan.Synthetic,
                            SpecTypeKind.Integer,
                            Number: asInt,
                            Integer: asInt);
                    }

                    return new LiteralNode(
                        SourceSpan.Synthetic,
                        SpecTypeKind.Number,
                        Number: ReadFiniteDouble(element));
                }

            case JsonValueKind.True:
            case JsonValueKind.False:
                return new LiteralNode(SourceSpan.Synthetic, SpecTypeKind.Boolean, Boolean: element.GetBoolean());

            case JsonValueKind.Null:
                return new LiteralNode(SourceSpan.Synthetic, SpecTypeKind.Unknown);

            case JsonValueKind.Array:
                {
                    var items = ImmutableArray.CreateBuilder<SpecExpression>();
                    foreach (var item in element.EnumerateArray())
                    {
                        items.Add(ReadExpression(item));
                    }

                    return new ArrayLiteral(SourceSpan.Synthetic, items.ToImmutable());
                }

            case JsonValueKind.Object:
                {
                    if (TryReadUnitLiteral(element, out var unitLiteral))
                    {
                        return unitLiteral;
                    }

                    if (TryReadGeometry(element, out var geometry))
                    {
                        return geometry;
                    }

                    if (TryReadCql2(element, out var cql2))
                    {
                        return cql2;
                    }

                    var fields = ImmutableArray.CreateBuilder<ObjectField>();
                    foreach (var prop in element.EnumerateObject())
                    {
                        fields.Add(new ObjectField(
                            SourceSpan.Synthetic,
                            SourceSpan.Synthetic,
                            prop.Name,
                            ReadExpression(prop.Value)));
                    }

                    return new ObjectExpression(SourceSpan.Synthetic, fields.ToImmutable());
                }

            default:
                return new LiteralNode(SourceSpan.Synthetic, SpecTypeKind.Unknown);
        }
    }

    private static bool TryReadUnitLiteral(JsonElement element, out LiteralNode literal)
    {
        if (element.TryGetProperty("kind", out var kindEl) &&
            element.TryGetProperty("unit", out var unitEl) &&
            element.TryGetProperty("value", out var valueEl) &&
            kindEl.ValueKind == JsonValueKind.String)
        {
            var kind = kindEl.GetString() switch
            {
                "distance" => SpecTypeKind.Distance,
                "duration" => SpecTypeKind.Duration,
                "area" => SpecTypeKind.Area,
                _ => SpecTypeKind.Unknown
            };

            if (kind != SpecTypeKind.Unknown)
            {
                literal = new LiteralNode(
                    SourceSpan.Synthetic,
                    kind,
                    Number: ReadFiniteDouble(valueEl),
                    Unit: unitEl.GetString());
                return true;
            }
        }

        literal = null!;
        return false;
    }

    private static double ReadFiniteDouble(JsonElement element)
    {
        var value = element.GetDouble();
        if (!double.IsFinite(value))
        {
            throw new FormatException("Canonical spec JSON numeric values must be finite.");
        }

        return value;
    }

    private static bool TryReadGeometry(JsonElement element, out GeometryLiteral literal)
    {
        if (element.TryGetProperty("type", out var typeEl) &&
            typeEl.ValueKind == JsonValueKind.String &&
            string.Equals(typeEl.GetString(), "geometry", StringComparison.Ordinal) &&
            element.TryGetProperty("wkt", out var wktEl) &&
            wktEl.ValueKind == JsonValueKind.String)
        {
            var wkt = wktEl.GetString() ?? string.Empty;
            try
            {
                var geometry = _wktReader.Read(wkt);
                literal = new GeometryLiteral(
                    SourceSpan.Synthetic,
                    geometry,
                    wkt,
                    element.TryGetProperty("crs", out var crsEl) && crsEl.ValueKind == JsonValueKind.String
                        ? crsEl.GetString()
                        : null);
                return true;
            }
            catch (ParseException)
            {
                // fall through
            }
        }

        literal = null!;
        return false;
    }

    private static bool TryReadCql2(JsonElement element, out Cql2Expression cql2)
    {
        if (element.TryGetProperty("cql2", out var cqlEl) &&
            cqlEl.ValueKind == JsonValueKind.String)
        {
            cql2 = new Cql2Expression(SourceSpan.Synthetic, cqlEl.GetString() ?? string.Empty);
            return true;
        }

        cql2 = null!;
        return false;
    }

    private static ReferenceNode ParseReferenceString(string text)
    {
        var body = text[1..];
        string? call = null;
        if (body.EndsWith("()", StringComparison.Ordinal))
        {
            body = body[..^2];
            var lastDot = body.LastIndexOf('.');
            if (lastDot > 0)
            {
                call = body[(lastDot + 1)..];
                body = body[..lastDot];
            }
            else
            {
                call = body;
                body = string.Empty;
            }
        }

        var parts = body.Length == 0
            ? Array.Empty<string>()
            : body.Split('.');

        var root = parts.Length == 0 ? string.Empty : parts[0];
        var segments = parts.Length > 1
            ? ImmutableArray.Create(parts, 1, parts.Length - 1)
            : ImmutableArray<string>.Empty;

        return new ReferenceNode(SourceSpan.Synthetic, root, segments, call);
    }

    private static string? TryGetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
