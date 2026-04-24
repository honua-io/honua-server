// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Features.Spec.Grammar;

/// <summary>
/// Recursive-descent parser for the Honua spec text form. The parser is
/// diagnostic-collecting — it never throws for user-facing issues; instead,
/// it appends <see cref="SpecDiagnostic"/> values and synchronizes at
/// top-level section boundaries to produce a partial AST for IntelliSense.
/// </summary>
public sealed class SpecParser : ISpecParser
{
    /// <summary>
    /// Activity source used for lex/parse telemetry.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Honua.Spec.Parse");

    private static readonly FrozenSet<string> _sectionKeywords =
        new HashSet<string>(StringComparer.Ordinal) { "grammar", "kind", "title", "source", "scope", "compute", "map", "output" }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly WKTReader _wktReader = new();

    /// <summary>
    /// Parses <paramref name="text"/> and returns an AST + diagnostics.
    /// </summary>
    /// <param name="text">Spec source text.</param>
    /// <returns>Parse result containing the AST (possibly partial) and any diagnostics.</returns>
    public SpecParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        using var activity = ActivitySource.StartActivity("spec.parse");
        var lexer = new SpecLexer(text);
        var tokens = lexer.Tokenize();
        activity?.SetTag("spec.parse.tokenCount", tokens.Count);

        var diagnostics = new List<SpecDiagnostic>(lexer.Diagnostics);
        var state = new ParserState(tokens, diagnostics);

        string? grammar = null;
        var grammarSpan = SourceSpan.Synthetic;
        string? kind = null;
        string? title = null;
        var sources = ImmutableArray.CreateBuilder<SourceBinding>();
        var scopes = ImmutableArray.CreateBuilder<ScopeClause>();
        var compute = ImmutableArray.CreateBuilder<ComputeStep>();
        MapSpec? map = null;
        var outputs = ImmutableArray.CreateBuilder<OutputBinding>();

        var docStart = state.Peek().Span;
        var sectionOrder = new List<string>();

        while (state.Peek().Type != SpecTokenType.EndOfInput)
        {
            var token = state.Peek();
            if (token.Type != SpecTokenType.Identifier)
            {
                diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.ParseError,
                    $"Expected a top-level section keyword (grammar, kind, title, source, scope, compute, map, output) but found '{token.Text}'.",
                    token.Span));
                Synchronize(state);
                continue;
            }

            switch (token.Text)
            {
                case "grammar":
                    state.Advance();
                    if (state.Peek().Type == SpecTokenType.String)
                    {
                        grammar = state.Peek().Text;
                        grammarSpan = state.Peek().Span;
                        state.Advance();
                    }
                    else
                    {
                        diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "grammar directive requires a quoted version string (e.g. \"v1.0\").",
                            state.Peek().Span));
                    }

                    sectionOrder.Add("grammar");
                    break;

                case "kind":
                    state.Advance();
                    if (state.Peek().Type == SpecTokenType.String)
                    {
                        kind = state.Peek().Text;
                        state.Advance();
                    }
                    else
                    {
                        diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "kind directive requires a quoted string value.",
                            state.Peek().Span));
                    }

                    sectionOrder.Add("kind");
                    break;

                case "title":
                    state.Advance();
                    if (state.Peek().Type == SpecTokenType.String)
                    {
                        title = state.Peek().Text;
                        state.Advance();
                    }
                    else
                    {
                        diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "title directive requires a quoted string value.",
                            state.Peek().Span));
                    }

                    sectionOrder.Add("title");
                    break;

                case "source":
                    {
                        var source = ParseSource(state);
                        if (source is not null)
                        {
                            sources.Add(source);
                            sectionOrder.Add($"source:{source.Id}");
                        }
                    }

                    break;

                case "scope":
                    {
                        var scope = ParseScope(state);
                        if (scope is not null)
                        {
                            scopes.Add(scope);
                            sectionOrder.Add("scope");
                        }
                    }

                    break;

                case "compute":
                    {
                        var step = ParseCompute(state);
                        if (step is not null)
                        {
                            compute.Add(step);
                            sectionOrder.Add($"compute:{step.Id}");
                        }
                    }

                    break;

                case "map":
                    {
                        var parsedMap = ParseMap(state);
                        if (parsedMap is not null)
                        {
                            map = parsedMap;
                            sectionOrder.Add("map");
                        }
                    }

                    break;

                case "output":
                    {
                        var output = ParseOutput(state);
                        if (output is not null)
                        {
                            outputs.Add(output);
                            sectionOrder.Add($"output:{output.Id}");
                        }
                    }

                    break;

                default:
                    diagnostics.Add(SpecDiagnostic.Error(
                        SpecDiagnosticCode.ParseError,
                        $"Unknown top-level section '{token.Text}'.",
                        token.Span));
                    Synchronize(state);
                    break;
            }
        }

        var comments = BuildCommentSidecar(lexer.Comments, sectionOrder);

        var docEnd = state.Peek().Span;
        var span = new SourceSpan(
            Line: docStart.Line,
            Column: docStart.Column,
            Offset: docStart.Offset,
            Length: Math.Max(0, (docEnd.Offset + docEnd.Length) - docStart.Offset));

        var document = new SpecDocument(
            span,
            grammar,
            grammarSpan,
            kind,
            title,
            sources.ToImmutable(),
            scopes.ToImmutable(),
            compute.ToImmutable(),
            map,
            outputs.ToImmutable(),
            comments);

        activity?.SetTag("spec.parse.errorCount", CountErrors(diagnostics));
        return new SpecParseResult(document, diagnostics);
    }

    private static int CountErrors(IReadOnlyList<SpecDiagnostic> diagnostics)
    {
        var count = 0;
        foreach (var d in diagnostics)
        {
            if (d.Severity == SpecDiagnosticSeverity.Error)
            {
                count++;
            }
        }

        return count;
    }

    private static SourceBinding? ParseSource(ParserState state)
    {
        var startSpan = state.Peek().Span;
        state.Advance(); // consume 'source'

        if (!TryExpectIdentifier(state, "source id", out var idToken))
        {
            Synchronize(state);
            return null;
        }

        var properties = ParseObject(state);
        if (properties is null)
        {
            return null;
        }

        return new SourceBinding(SpanFrom(startSpan, properties.Span), idToken!.Text, properties);
    }

    private static ScopeClause? ParseScope(ParserState state)
    {
        var startSpan = state.Peek().Span;
        state.Advance(); // consume 'scope'

        if (state.Peek().Type != SpecTokenType.LeftBrace)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.ParseError,
                "scope block requires '{' after the keyword.",
                state.Peek().Span));
            Synchronize(state);
            return null;
        }

        var obj = ParseObject(state);
        if (obj is null)
        {
            return null;
        }

        ReferenceNode? target = null;
        SpecExpression? where = null;
        foreach (var field in obj.Fields)
        {
            switch (field.Key)
            {
                case "target":
                    if (field.Value is ReferenceNode reference)
                    {
                        target = reference;
                    }
                    else
                    {
                        state.Diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "scope.target must be a reference (e.g. @hospitals).",
                            field.Value.Span));
                    }

                    break;

                case "where":
                    where = field.Value;
                    break;

                default:
                    state.Diagnostics.Add(SpecDiagnostic.Warning(
                        SpecDiagnosticCode.ParseError,
                        $"scope block does not recognize field '{field.Key}'.",
                        field.KeySpan));
                    break;
            }
        }

        if (target is null)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.MissingRequiredParameter,
                "scope block requires a 'target' reference.",
                obj.Span));
            return null;
        }

        return new ScopeClause(SpanFrom(startSpan, obj.Span), target, where);
    }

    private static ComputeStep? ParseCompute(ParserState state)
    {
        var startSpan = state.Peek().Span;
        state.Advance(); // consume 'compute'

        if (!TryExpectIdentifier(state, "compute id", out var idToken))
        {
            Synchronize(state);
            return null;
        }

        var obj = ParseObject(state);
        if (obj is null)
        {
            return null;
        }

        string? operatorName = null;
        var operatorSpan = SourceSpan.Synthetic;
        ObjectExpression? inputs = null;
        ObjectExpression? parameters = null;

        foreach (var field in obj.Fields)
        {
            switch (field.Key)
            {
                case "op":
                    if (field.Value is ReferenceNode reference)
                    {
                        operatorName = reference.Root;
                        operatorSpan = reference.Span;
                    }
                    else if (field.Value is LiteralNode { Kind: SpecTypeKind.String } literal)
                    {
                        operatorName = literal.String;
                        operatorSpan = literal.Span;
                    }
                    else if (field.Value is ReferenceLikeIdentifier ident)
                    {
                        operatorName = ident.Name;
                        operatorSpan = ident.Span;
                    }
                    else
                    {
                        state.Diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "compute.op must be an operator identifier (e.g. spatial_join).",
                            field.Value.Span));
                    }

                    break;

                case "inputs":
                    if (field.Value is ObjectExpression inputsObj)
                    {
                        inputs = inputsObj;
                    }
                    else
                    {
                        state.Diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "compute.inputs must be an object literal (e.g. { left = @a, right = @b }).",
                            field.Value.Span));
                    }

                    break;

                case "params":
                    if (field.Value is ObjectExpression paramsObj)
                    {
                        parameters = paramsObj;
                    }
                    else
                    {
                        state.Diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "compute.params must be an object literal.",
                            field.Value.Span));
                    }

                    break;

                default:
                    state.Diagnostics.Add(SpecDiagnostic.Warning(
                        SpecDiagnosticCode.ParseError,
                        $"compute block does not recognize field '{field.Key}'.",
                        field.KeySpan));
                    break;
            }
        }

        if (operatorName is null)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.MissingRequiredParameter,
                "compute block requires an 'op' field naming the operator.",
                obj.Span));
            return null;
        }

        return new ComputeStep(
            SpanFrom(startSpan, obj.Span),
            idToken!.Text,
            operatorName,
            operatorSpan,
            inputs,
            parameters);
    }

    private static MapSpec? ParseMap(ParserState state)
    {
        var startSpan = state.Peek().Span;
        state.Advance(); // consume 'map'

        var obj = ParseObject(state);
        if (obj is null)
        {
            return null;
        }

        ArrayLiteral? layers = null;
        ObjectExpression? viewport = null;
        ObjectExpression? legend = null;

        foreach (var field in obj.Fields)
        {
            switch (field.Key)
            {
                case "layers":
                    if (field.Value is ArrayLiteral arr)
                    {
                        layers = arr;
                    }
                    else
                    {
                        state.Diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "map.layers must be an array literal.",
                            field.Value.Span));
                    }

                    break;

                case "viewport":
                    if (field.Value is ObjectExpression vp)
                    {
                        viewport = vp;
                    }
                    else
                    {
                        state.Diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "map.viewport must be an object literal.",
                            field.Value.Span));
                    }

                    break;

                case "legend":
                    if (field.Value is ObjectExpression lg)
                    {
                        legend = lg;
                    }
                    else
                    {
                        state.Diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.ParseError,
                            "map.legend must be an object literal.",
                            field.Value.Span));
                    }

                    break;

                default:
                    state.Diagnostics.Add(SpecDiagnostic.Warning(
                        SpecDiagnosticCode.ParseError,
                        $"map block does not recognize field '{field.Key}'.",
                        field.KeySpan));
                    break;
            }
        }

        return new MapSpec(SpanFrom(startSpan, obj.Span), layers, viewport, legend);
    }

    private static OutputBinding? ParseOutput(ParserState state)
    {
        var startSpan = state.Peek().Span;
        state.Advance(); // consume 'output'

        if (!TryExpectIdentifier(state, "output id", out var idToken))
        {
            Synchronize(state);
            return null;
        }

        var obj = ParseObject(state);
        if (obj is null)
        {
            return null;
        }

        SpecExpression? expression = null;
        foreach (var field in obj.Fields)
        {
            if (field.Key == "expr")
            {
                expression = field.Value;
            }
            else
            {
                state.Diagnostics.Add(SpecDiagnostic.Warning(
                    SpecDiagnosticCode.ParseError,
                    $"output block does not recognize field '{field.Key}'.",
                    field.KeySpan));
            }
        }

        if (expression is null)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.MissingRequiredParameter,
                "output block requires an 'expr' field.",
                obj.Span));
            return null;
        }

        return new OutputBinding(SpanFrom(startSpan, obj.Span), idToken!.Text, expression);
    }

    private static ObjectExpression? ParseObject(ParserState state)
    {
        var openSpan = state.Peek().Span;
        if (state.Peek().Type != SpecTokenType.LeftBrace)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.ParseError,
                $"Expected '{{' but found '{state.Peek().Text}'.",
                openSpan));
            Synchronize(state);
            return null;
        }

        state.Advance(); // consume {

        var fields = ImmutableArray.CreateBuilder<ObjectField>();
        while (state.Peek().Type != SpecTokenType.EndOfInput &&
               state.Peek().Type != SpecTokenType.RightBrace)
        {
            var field = ParseObjectField(state);
            if (field is null)
            {
                // Skip to the next comma or closing brace so we don't infinite-loop.
                while (state.Peek().Type != SpecTokenType.EndOfInput &&
                       state.Peek().Type != SpecTokenType.Comma &&
                       state.Peek().Type != SpecTokenType.RightBrace)
                {
                    state.Advance();
                }

                if (state.Peek().Type == SpecTokenType.Comma)
                {
                    state.Advance();
                }

                continue;
            }

            fields.Add(field);
            if (state.Peek().Type == SpecTokenType.Comma)
            {
                state.Advance();
            }
        }

        if (state.Peek().Type != SpecTokenType.RightBrace)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.ParseError,
                "Missing closing '}' for object literal.",
                openSpan));
            return new ObjectExpression(openSpan, fields.ToImmutable());
        }

        var closeSpan = state.Peek().Span;
        state.Advance();
        return new ObjectExpression(SpanFrom(openSpan, closeSpan), fields.ToImmutable());
    }

    private static ObjectField? ParseObjectField(ParserState state)
    {
        var keyToken = state.Peek();
        if (keyToken.Type != SpecTokenType.Identifier && keyToken.Type != SpecTokenType.String)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.ParseError,
                $"Expected field name but found '{keyToken.Text}'.",
                keyToken.Span));
            return null;
        }

        state.Advance();
        if (state.Peek().Type != SpecTokenType.Equals)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.ParseError,
                $"Expected '=' after field '{keyToken.Text}'.",
                state.Peek().Span));
            return null;
        }

        state.Advance(); // consume =
        var value = ParseExpression(state);
        if (value is null)
        {
            return null;
        }

        return new ObjectField(SpanFrom(keyToken.Span, value.Span), keyToken.Span, keyToken.Text, value);
    }

    private static SpecExpression? ParseExpression(ParserState state)
    {
        var token = state.Peek();
        switch (token.Type)
        {
            case SpecTokenType.String:
                state.Advance();
                return new LiteralNode(token.Span, SpecTypeKind.String, String: token.Text);

            case SpecTokenType.Number:
                return ParseNumberOrUnit(state);

            case SpecTokenType.Boolean:
                state.Advance();
                return new LiteralNode(
                    token.Span,
                    SpecTypeKind.Boolean,
                    Boolean: string.Equals(token.Text, "true", StringComparison.Ordinal));

            case SpecTokenType.Null:
                state.Advance();
                return new LiteralNode(token.Span, SpecTypeKind.Unknown);

            case SpecTokenType.LeftBrace:
                return ParseObject(state);

            case SpecTokenType.LeftBracket:
                return ParseArray(state);

            case SpecTokenType.Reference:
                return ParseReference(state);

            case SpecTokenType.Identifier:
                return ParseIdentifierOrCall(state);

            default:
                state.Diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.ParseError,
                    $"Unexpected token '{token.Text}' while parsing expression.",
                    token.Span));
                state.Advance();
                return null;
        }
    }

    private static LiteralNode? ParseNumberOrUnit(ParserState state)
    {
        var numberToken = state.Peek();
        state.Advance();

        if (!double.TryParse(numberToken.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue))
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.SyntaxError,
                $"Invalid numeric literal '{numberToken.Text}'.",
                numberToken.Span));
            return new LiteralNode(numberToken.Span, SpecTypeKind.Unknown);
        }

        if (state.Peek().Type == SpecTokenType.Dot && state.Peek(1).Type == SpecTokenType.Identifier)
        {
            // Unit annotation: number '.' identifier
            var dotSpan = state.Peek().Span;
            state.Advance();
            var unitToken = state.Peek();
            state.Advance();
            var kind = ResolveUnitKind(unitToken.Text);
            if (kind == SpecTypeKind.Unknown)
            {
                state.Diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.SyntaxError,
                    $"Unknown unit suffix '.{unitToken.Text}'. Allowed: km, m, mi, ft, s, min, h, d, ha, km2, m2.",
                    unitToken.Span));
                return new LiteralNode(SpanFrom(numberToken.Span, unitToken.Span), SpecTypeKind.Unknown);
            }

            return new LiteralNode(
                SpanFrom(numberToken.Span, unitToken.Span),
                kind,
                Number: numberValue,
                Unit: unitToken.Text);
        }

        if (!numberToken.Text.Contains('.') && !numberToken.Text.Contains('e') && !numberToken.Text.Contains('E'))
        {
            return new LiteralNode(
                numberToken.Span,
                SpecTypeKind.Integer,
                Number: numberValue,
                Integer: (long)numberValue);
        }

        return new LiteralNode(numberToken.Span, SpecTypeKind.Number, Number: numberValue);
    }

    private static ArrayLiteral? ParseArray(ParserState state)
    {
        var openSpan = state.Peek().Span;
        state.Advance(); // consume [

        var items = ImmutableArray.CreateBuilder<SpecExpression>();
        while (state.Peek().Type != SpecTokenType.EndOfInput &&
               state.Peek().Type != SpecTokenType.RightBracket)
        {
            var value = ParseExpression(state);
            if (value is null)
            {
                while (state.Peek().Type != SpecTokenType.EndOfInput &&
                       state.Peek().Type != SpecTokenType.Comma &&
                       state.Peek().Type != SpecTokenType.RightBracket)
                {
                    state.Advance();
                }
            }
            else
            {
                items.Add(value);
            }

            if (state.Peek().Type == SpecTokenType.Comma)
            {
                state.Advance();
            }
        }

        if (state.Peek().Type != SpecTokenType.RightBracket)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.ParseError,
                "Missing closing ']' for array literal.",
                openSpan));
            return new ArrayLiteral(openSpan, items.ToImmutable());
        }

        var closeSpan = state.Peek().Span;
        state.Advance();
        return new ArrayLiteral(SpanFrom(openSpan, closeSpan), items.ToImmutable());
    }

    private static ReferenceNode ParseReference(ParserState state)
    {
        var token = state.Peek();
        state.Advance();
        var root = token.Text.StartsWith('@') ? token.Text[1..] : token.Text;

        var segments = ImmutableArray.CreateBuilder<string>();
        string? call = null;
        var span = token.Span;

        while (state.Peek().Type == SpecTokenType.Dot && state.Peek(1).Type == SpecTokenType.Identifier)
        {
            state.Advance(); // consume dot
            var segmentToken = state.Peek();
            state.Advance();
            span = SpanFrom(span, segmentToken.Span);

            if (state.Peek().Type == SpecTokenType.LeftParen)
            {
                var parenSpan = state.Peek().Span;
                state.Advance();
                if (state.Peek().Type != SpecTokenType.RightParen)
                {
                    state.Diagnostics.Add(SpecDiagnostic.Error(
                        SpecDiagnosticCode.ParseError,
                        "Reference call must have no arguments (e.g. @compute.foo.count()).",
                        parenSpan));
                    while (state.Peek().Type != SpecTokenType.EndOfInput &&
                           state.Peek().Type != SpecTokenType.RightParen)
                    {
                        state.Advance();
                    }
                }

                if (state.Peek().Type == SpecTokenType.RightParen)
                {
                    span = SpanFrom(span, state.Peek().Span);
                    state.Advance();
                }

                call = segmentToken.Text;
                break;
            }

            segments.Add(segmentToken.Text);
        }

        return new ReferenceNode(span, root, segments.ToImmutable(), call);
    }

    private static SpecExpression? ParseIdentifierOrCall(ParserState state)
    {
        var token = state.Peek();
        state.Advance();

        // cql2(...) — embed the CQL2 expression as an opaque node.
        if (string.Equals(token.Text, "cql2", StringComparison.Ordinal) &&
            state.Peek().Type == SpecTokenType.LeftParen)
        {
            state.Advance();
            if (state.Peek().Type != SpecTokenType.String)
            {
                state.Diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.ParseError,
                    "cql2(...) expects a single quoted string argument.",
                    state.Peek().Span));
                return null;
            }

            var argToken = state.Peek();
            state.Advance();
            if (state.Peek().Type != SpecTokenType.RightParen)
            {
                state.Diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.ParseError,
                    "cql2(...) must be closed with ')'.",
                    state.Peek().Span));
                return null;
            }

            var closeSpan = state.Peek().Span;
            state.Advance();
            return new Cql2Expression(SpanFrom(token.Span, closeSpan), argToken.Text);
        }

        // WKT geometry helpers (POINT(...), POLYGON(...), BBOX(...)) reuse the
        // NetTopologySuite WKT reader directly.
        if (LooksLikeGeometryKeyword(token.Text) && state.Peek().Type == SpecTokenType.LeftParen)
        {
            return ParseWellKnownGeometry(state, token);
        }

        // Bareword identifiers (e.g. operator names used as values) become
        // ReferenceLikeIdentifier so the parent clause can consume them.
        return new ReferenceLikeIdentifier(token.Span, token.Text);
    }

    private static GeometryLiteral? ParseWellKnownGeometry(ParserState state, SpecToken head)
    {
        var startSpan = head.Span;
        state.Advance(); // consume (
        var depth = 1;
        var buffer = new System.Text.StringBuilder(head.Text);
        buffer.Append('(');

        while (state.Peek().Type != SpecTokenType.EndOfInput)
        {
            var token = state.Peek();
            if (token.Type == SpecTokenType.LeftParen)
            {
                buffer.Append('(');
                depth++;
                state.Advance();
                continue;
            }

            if (token.Type == SpecTokenType.RightParen)
            {
                buffer.Append(')');
                depth--;
                state.Advance();
                if (depth == 0)
                {
                    var wkt = buffer.ToString();
                    try
                    {
                        var geometry = _wktReader.Read(wkt);
                        return new GeometryLiteral(
                            SpanFrom(startSpan, token.Span),
                            geometry,
                            wkt);
                    }
                    catch (ParseException ex)
                    {
                        state.Diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.SyntaxError,
                            $"Invalid WKT literal: {ex.Message}",
                            SpanFrom(startSpan, token.Span)));
                        return null;
                    }
                }

                continue;
            }

            switch (token.Type)
            {
                case SpecTokenType.Identifier:
                case SpecTokenType.Number:
                    buffer.Append(' ').Append(token.Text);
                    break;
                case SpecTokenType.Comma:
                    buffer.Append(',');
                    break;
                case SpecTokenType.Dot:
                    buffer.Append('.');
                    break;
                default:
                    break;
            }

            state.Advance();
        }

        state.Diagnostics.Add(SpecDiagnostic.Error(
            SpecDiagnosticCode.SyntaxError,
            "Unterminated WKT geometry literal.",
            startSpan));
        return null;
    }

    private static bool LooksLikeGeometryKeyword(string text)
    {
        return text.Equals("POINT", StringComparison.OrdinalIgnoreCase)
            || text.Equals("LINESTRING", StringComparison.OrdinalIgnoreCase)
            || text.Equals("POLYGON", StringComparison.OrdinalIgnoreCase)
            || text.Equals("MULTIPOINT", StringComparison.OrdinalIgnoreCase)
            || text.Equals("MULTILINESTRING", StringComparison.OrdinalIgnoreCase)
            || text.Equals("MULTIPOLYGON", StringComparison.OrdinalIgnoreCase)
            || text.Equals("GEOMETRYCOLLECTION", StringComparison.OrdinalIgnoreCase);
    }

    private static SpecTypeKind ResolveUnitKind(string suffix)
    {
        return suffix.ToLowerInvariant() switch
        {
            "m" or "km" or "mi" or "ft" or "nm" => SpecTypeKind.Distance,
            "s" or "ms" or "min" or "h" or "d" => SpecTypeKind.Duration,
            "m2" or "km2" or "ha" or "ac" => SpecTypeKind.Area,
            _ => SpecTypeKind.Unknown
        };
    }

    private static bool TryExpectIdentifier(ParserState state, string role, out SpecToken? token)
    {
        var peek = state.Peek();
        if (peek.Type != SpecTokenType.Identifier)
        {
            state.Diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.ParseError,
                $"Expected identifier for {role} but found '{peek.Text}'.",
                peek.Span));
            token = null;
            return false;
        }

        token = peek;
        state.Advance();
        return true;
    }

    private static void Synchronize(ParserState state)
    {
        while (state.Peek().Type != SpecTokenType.EndOfInput)
        {
            var token = state.Peek();
            if (token.Type == SpecTokenType.Identifier && _sectionKeywords.Contains(token.Text))
            {
                return;
            }

            state.Advance();
        }
    }

    private static ImmutableDictionary<string, string> BuildCommentSidecar(
        IReadOnlyList<(string LocationKey, string Text)> comments,
        IReadOnlyList<string> sectionOrder)
    {
        if (comments.Count == 0)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var commentIndex = 0;
        var attached = new bool[comments.Count];

        foreach (var section in sectionOrder)
        {
            if (commentIndex >= comments.Count)
            {
                break;
            }

            var path = SectionToJsonPointer(section);
            if (path is null)
            {
                continue;
            }

            // Attach the first unattached comment preceding this section.
            for (var i = 0; i < comments.Count; i++)
            {
                if (attached[i])
                {
                    continue;
                }

                var key = $"{path}#{i}";
                builder[key] = comments[i].Text;
                attached[i] = true;
                break;
            }

            commentIndex++;
        }

        for (var i = 0; i < comments.Count; i++)
        {
            if (!attached[i])
            {
                builder[$"/meta/comments/trailing#{i}"] = comments[i].Text;
            }
        }

        return builder.ToImmutable();
    }

    private static string? SectionToJsonPointer(string section)
    {
        if (section.StartsWith("source:", StringComparison.Ordinal))
        {
            return $"/sources/{EscapeJsonPointerSegment(section[7..])}";
        }

        if (section.StartsWith("compute:", StringComparison.Ordinal))
        {
            return $"/compute/{EscapeJsonPointerSegment(section[8..])}";
        }

        if (section.StartsWith("output:", StringComparison.Ordinal))
        {
            return $"/outputs/{EscapeJsonPointerSegment(section[7..])}";
        }

        return section switch
        {
            "grammar" => "/grammar",
            "kind" => "/kind",
            "title" => "/title",
            "scope" => "/scope",
            "map" => "/map",
            _ => null
        };
    }

    private static string EscapeJsonPointerSegment(string value)
    {
        if (!value.Contains('~') && !value.Contains('/'))
        {
            return value;
        }

        return value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    private static SourceSpan SpanFrom(SourceSpan start, SourceSpan end)
    {
        var end2Offset = end.Offset + end.Length;
        var startOffset = start.Offset;
        return new SourceSpan(
            start.Line,
            start.Column,
            startOffset,
            Math.Max(0, end2Offset - startOffset));
    }

    private sealed class ParserState
    {
        private readonly IReadOnlyList<SpecToken> _tokens;
        private int _index;

        public ParserState(IReadOnlyList<SpecToken> tokens, List<SpecDiagnostic> diagnostics)
        {
            _tokens = tokens;
            Diagnostics = diagnostics;
        }

        public List<SpecDiagnostic> Diagnostics { get; }

        public SpecToken Peek(int lookahead = 0)
        {
            var pos = _index + lookahead;
            return pos < _tokens.Count
                ? _tokens[pos]
                : _tokens[^1];
        }

        public void Advance()
        {
            if (_index < _tokens.Count - 1)
            {
                _index++;
            }
        }
    }

    /// <summary>
    /// Transient expression for a bare identifier used where a reference-like
    /// value is expected (for example, an <c>op = spatial_join</c> field). The
    /// parent rule consumes this and does not emit it to canonical JSON.
    /// </summary>
    /// <param name="Span">Source location.</param>
    /// <param name="Name">Identifier text.</param>
    internal sealed record ReferenceLikeIdentifier(SourceSpan Span, string Name) : SpecExpression(Span);
}
