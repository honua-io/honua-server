// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Server.Features.Grounding.Spec;

internal sealed partial class SpecMutationApplier
{
    public SpecDocument Apply(SpecDocument document, IReadOnlyList<SpecMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(mutations);

        var current = document;
        foreach (var mutation in mutations)
        {
            current = mutation switch
            {
                AddSourceMutation addSource => Apply(current, addSource),
                RemoveSourceMutation removeSource => Apply(current, removeSource),
                AddScopeClauseMutation addScope => Apply(current, addScope),
                AddComputeMutation addCompute => Apply(current, addCompute),
                RemoveComputeMutation removeCompute => Apply(current, removeCompute),
                SetMapLayerMutation setMapLayer => Apply(current, setMapLayer),
                SetViewportMutation setViewport => Apply(current, setViewport),
                SetOutputMutation setOutput => Apply(current, setOutput),
                RenameReferenceMutation rename => Apply(current, rename),
                _ => throw new InvalidOperationException($"Unsupported mutation '{mutation.Kind}'.")
            };
        }

        return current;
    }

    private static SpecDocument Apply(SpecDocument document, AddSourceMutation mutation)
    {
        if (document.Sources.Any(source => string.Equals(source.Id, mutation.SourceId, StringComparison.Ordinal)))
        {
            return document;
        }

        var fields = ImmutableArray.CreateBuilder<ObjectField>();
        fields.Add(CreateObjectField("type", CreateStringLiteral(mutation.SourceType)));
        fields.Add(CreateObjectField("ref", CreateStringLiteral(mutation.SourceRef)));
        if (!string.IsNullOrWhiteSpace(mutation.SourceTitle))
        {
            fields.Add(CreateObjectField("title", CreateStringLiteral(mutation.SourceTitle)));
        }

        var nextSources = document.Sources.Add(new SourceBinding(
            SourceSpan.Synthetic,
            mutation.SourceId,
            new ObjectExpression(SourceSpan.Synthetic, fields.ToImmutable())));

        return document with { Sources = nextSources };
    }

    private static SpecDocument Apply(SpecDocument document, RemoveSourceMutation mutation)
    {
        var nextSources = document.Sources.RemoveAll(source => string.Equals(source.Id, mutation.SourceId, StringComparison.Ordinal));
        if (nextSources.Length == document.Sources.Length)
        {
            throw new InvalidOperationException($"Source '{mutation.SourceId}' does not exist.");
        }

        return document with { Sources = nextSources };
    }

    private static SpecDocument Apply(SpecDocument document, AddScopeClauseMutation mutation)
    {
        var existing = document.Scopes.Any(scope =>
            string.Equals(scope.Target.Root, mutation.TargetId, StringComparison.Ordinal) &&
            scope.Where is Cql2Expression cql &&
            string.Equals(cql.Cql2Text, mutation.Predicate, StringComparison.Ordinal));
        if (existing)
        {
            return document;
        }

        var nextScopes = document.Scopes.Add(new ScopeClause(
            SourceSpan.Synthetic,
            CreateReference(mutation.TargetId),
            new Cql2Expression(SourceSpan.Synthetic, mutation.Predicate)));

        return document with { Scopes = nextScopes };
    }

    private static SpecDocument Apply(SpecDocument document, AddComputeMutation mutation)
    {
        if (document.Compute.Any(step => string.Equals(step.Id, mutation.ComputeId, StringComparison.Ordinal)))
        {
            return document;
        }

        var nextCompute = document.Compute.Add(new ComputeStep(
            SourceSpan.Synthetic,
            mutation.ComputeId,
            mutation.OperatorName,
            SourceSpan.Synthetic,
            BuildObjectExpression(mutation.Inputs),
            mutation.Parameters is null ? null : BuildObjectExpression(mutation.Parameters)));

        return document with { Compute = nextCompute };
    }

    private static SpecDocument Apply(SpecDocument document, RemoveComputeMutation mutation)
    {
        var nextCompute = document.Compute.RemoveAll(step => string.Equals(step.Id, mutation.ComputeId, StringComparison.Ordinal));
        if (nextCompute.Length == document.Compute.Length)
        {
            throw new InvalidOperationException($"Compute '{mutation.ComputeId}' does not exist.");
        }

        return document with { Compute = nextCompute };
    }

    private static SpecDocument Apply(SpecDocument document, SetMapLayerMutation mutation)
    {
        var layers = new ArrayLiteral(
            SourceSpan.Synthetic,
            mutation.LayerIds
                .Select(id => (SpecExpression)CreateStringLiteral(id))
                .ToImmutableArray());

        var map = document.Map is null
            ? new MapSpec(SourceSpan.Synthetic, layers, null, null)
            : document.Map with { Layers = layers };

        return document with { Map = map };
    }

    private static SpecDocument Apply(SpecDocument document, SetViewportMutation mutation)
    {
        var viewport = BuildObjectExpression(mutation.Values);
        var map = document.Map is null
            ? new MapSpec(SourceSpan.Synthetic, null, viewport, null)
            : document.Map with { Viewport = viewport };

        return document with { Map = map };
    }

    private static SpecDocument Apply(SpecDocument document, SetOutputMutation mutation)
    {
        var expression = ParseExpressionToken(mutation.Expression);
        var output = new OutputBinding(SourceSpan.Synthetic, mutation.OutputId, expression);

        var index = -1;
        for (var i = 0; i < document.Outputs.Length; i++)
        {
            if (string.Equals(document.Outputs[i].Id, mutation.OutputId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        var nextOutputs = index >= 0
            ? document.Outputs.SetItem(index, output)
            : document.Outputs.Add(output);

        return document with { Outputs = nextOutputs };
    }

    private static SpecDocument Apply(SpecDocument document, RenameReferenceMutation mutation)
    {
        var renamedSources = document.Sources
            .Select(source =>
                string.Equals(source.Id, mutation.FromId, StringComparison.Ordinal)
                    ? source with { Id = mutation.ToId }
                    : source)
            .ToImmutableArray();

        var renamedScopes = document.Scopes
            .Select(scope => scope with
            {
                Target = RewriteReference(scope.Target, mutation),
                Where = scope.Where is null ? null : RewriteExpression(scope.Where, mutation)
            })
            .ToImmutableArray();

        var renamedCompute = document.Compute
            .Select(step => (string.Equals(step.Id, mutation.FromId, StringComparison.Ordinal)
                    ? step with { Id = mutation.ToId }
                    : step) with
            {
                Inputs = step.Inputs is null ? null : RewriteObject(step.Inputs, mutation),
                Parameters = step.Parameters is null ? null : RewriteObject(step.Parameters, mutation)
            })
            .ToImmutableArray();

        MapSpec? renamedMap = null;
        if (document.Map is not null)
        {
            renamedMap = document.Map with
            {
                Layers = document.Map.Layers is null ? null : RewriteArray(document.Map.Layers, mutation),
                Viewport = document.Map.Viewport is null ? null : RewriteObject(document.Map.Viewport, mutation),
                Legend = document.Map.Legend is null ? null : RewriteObject(document.Map.Legend, mutation)
            };
        }

        var renamedOutputs = document.Outputs
            .Select(output => (string.Equals(output.Id, mutation.FromId, StringComparison.Ordinal)
                    ? output with { Id = mutation.ToId }
                    : output) with
            {
                Expression = RewriteExpression(output.Expression, mutation)
            })
            .ToImmutableArray();

        return document with
        {
            Sources = renamedSources,
            Scopes = renamedScopes,
            Compute = renamedCompute,
            Map = renamedMap,
            Outputs = renamedOutputs
        };
    }

    private static ObjectExpression BuildObjectExpression(IReadOnlyDictionary<string, string> values)
    {
        var fields = values
            .Select(pair => CreateObjectField(pair.Key, ParseExpressionToken(pair.Value)))
            .ToImmutableArray();
        return new ObjectExpression(SourceSpan.Synthetic, fields);
    }

    private static ObjectField CreateObjectField(string key, SpecExpression value)
        => new(SourceSpan.Synthetic, SourceSpan.Synthetic, key, value);

    private static LiteralNode CreateStringLiteral(string? value)
        => new(SourceSpan.Synthetic, SpecTypeKind.String, String: value ?? string.Empty);

    private static ReferenceNode CreateReference(string id)
        => new(SourceSpan.Synthetic, id.TrimStart('@'), ImmutableArray<string>.Empty);

    private static ReferenceNode RewriteReference(ReferenceNode reference, RenameReferenceMutation mutation)
        => string.Equals(reference.Root, mutation.FromId, StringComparison.Ordinal)
            ? reference with { Root = mutation.ToId }
            : reference;

    private static ObjectExpression RewriteObject(ObjectExpression expression, RenameReferenceMutation mutation)
        => expression with
        {
            Fields = expression.Fields
                .Select(field => field with { Value = RewriteExpression(field.Value, mutation) })
                .ToImmutableArray()
        };

    private static ArrayLiteral RewriteArray(ArrayLiteral expression, RenameReferenceMutation mutation)
        => expression with
        {
            Items = expression.Items
                .Select(item => RewriteExpression(item, mutation))
                .ToImmutableArray()
        };

    private static SpecExpression RewriteExpression(SpecExpression expression, RenameReferenceMutation mutation)
    {
        return expression switch
        {
            ReferenceNode reference => RewriteReference(reference, mutation),
            ArrayLiteral array => RewriteArray(array, mutation),
            ObjectExpression obj => RewriteObject(obj, mutation),
            LiteralNode literal when literal.Kind == SpecTypeKind.String &&
                string.Equals(literal.String, mutation.FromId, StringComparison.Ordinal) => literal with { String = mutation.ToId },
            _ => expression
        };
    }

    private static SpecExpression ParseExpressionToken(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length == 0)
        {
            return CreateStringLiteral(string.Empty);
        }

        if (value.StartsWith('@'))
        {
            return ParseReference(value);
        }

        if (bool.TryParse(value, out var boolean))
        {
            return new LiteralNode(SourceSpan.Synthetic, SpecTypeKind.Boolean, Boolean: boolean);
        }

        if (TryParseUnitLiteral(value, out var unitLiteral))
        {
            return unitLiteral;
        }

        if (long.TryParse(value, out var integer))
        {
            return new LiteralNode(SourceSpan.Synthetic, SpecTypeKind.Integer, Number: integer, Integer: integer);
        }

        if (double.TryParse(value, out var number))
        {
            return new LiteralNode(SourceSpan.Synthetic, SpecTypeKind.Number, Number: number);
        }

        return CreateStringLiteral(value);
    }

    private static ReferenceNode ParseReference(string value)
    {
        var trimmed = value.Trim();
        var withoutPrefix = trimmed[1..];
        string? call = null;
        if (withoutPrefix.EndsWith("()", StringComparison.Ordinal))
        {
            var lastDot = withoutPrefix.LastIndexOf('.');
            if (lastDot > 0)
            {
                call = withoutPrefix[(lastDot + 1)..^2];
                withoutPrefix = withoutPrefix[..lastDot];
            }
            else
            {
                call = withoutPrefix[..^2];
                withoutPrefix = string.Empty;
            }
        }

        var segments = withoutPrefix.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return new ReferenceNode(SourceSpan.Synthetic, string.Empty, ImmutableArray<string>.Empty, call);
        }

        return new ReferenceNode(
            SourceSpan.Synthetic,
            segments[0],
            segments.Skip(1).ToImmutableArray(),
            call);
    }

    private static bool TryParseUnitLiteral(string value, out LiteralNode literal)
    {
        var match = UnitLiteralPattern().Match(value);
        if (!match.Success)
        {
            literal = null!;
            return false;
        }

        var numeric = double.Parse(match.Groups["value"].Value);
        var unit = match.Groups["unit"].Value;
        var kind = unit switch
        {
            "m" or "km" or "mi" or "ft" or "nm" => SpecTypeKind.Distance,
            "s" or "min" or "h" => SpecTypeKind.Duration,
            "ha" => SpecTypeKind.Area,
            _ => SpecTypeKind.Number
        };

        literal = new LiteralNode(SourceSpan.Synthetic, kind, Number: numeric, Unit: unit);
        return true;
    }

    [GeneratedRegex(@"^(?<value>-?\d+(?:\.\d+)?)\.(?<unit>[a-zA-Z]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitLiteralPattern();
}
