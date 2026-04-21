// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Server.Features.Grounding.Spec;

internal sealed class SpecSummarizer
{
    public SpecSummary Summarize(SpecDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sections = new List<SpecSectionSummary>(capacity: 5);

        if (document.Sources.Length > 0)
        {
            var parts = document.Sources
                .Select(source => $"source {source.Id} uses dataset {GetSourceDisplayName(source)}")
                .ToArray();
            sections.Add(new SpecSectionSummary("sources", BuildSectionText(parts)));
        }

        if (document.Scopes.Length > 0)
        {
            var parts = document.Scopes
                .Select(scope => $"filters {scope.Target.Canonical} where {FormatScope(scope.Where)}")
                .ToArray();
            sections.Add(new SpecSectionSummary("scope", BuildSectionText(parts)));
        }

        if (document.Compute.Length > 0)
        {
            var parts = document.Compute
                .Select(FormatComputeStep)
                .ToArray();
            sections.Add(new SpecSectionSummary("compute", BuildSectionText(parts)));
        }

        if (document.Map is not null)
        {
            var sentences = new List<string>(capacity: 2);
            if (document.Map.Layers is { Items.Length: > 0 })
            {
                var layerIds = document.Map.Layers.Items.Select(FormatExpression).ToArray();
                sentences.Add($"shows {string.Join(", ", layerIds)} on the map");
            }

            if (document.Map.Viewport is not null)
            {
                sentences.Add($"viewport {FormatViewport(document.Map.Viewport)}");
            }

            if (sentences.Count > 0)
            {
                sections.Add(new SpecSectionSummary("map", BuildSectionText(sentences)));
            }
        }

        if (document.Outputs.Length > 0)
        {
            var parts = document.Outputs
                .Select(output => $"output {output.Id} returns {FormatExpression(output.Expression)}")
                .ToArray();
            sections.Add(new SpecSectionSummary("outputs", BuildSectionText(parts)));
        }

        var titleSummary = BuildTitleSummary(document);
        return new SpecSummary(titleSummary, sections);
    }

    private static string BuildTitleSummary(SpecDocument document)
    {
        if (document.Compute.Length > 0)
        {
            var primary = document.Compute[0];
            return $"Runs {primary.OperatorName} with {document.Sources.Length} source{Suffix(document.Sources.Length)}.";
        }

        if (document.Sources.Length > 0)
        {
            return $"Uses {document.Sources.Length} source{Suffix(document.Sources.Length)} for the analysis.";
        }

        return "No sources, computations, or outputs are defined yet.";
    }

    private static string FormatComputeStep(ComputeStep step)
    {
        var inputs = step.Inputs is null
            ? string.Empty
            : string.Join(" and ", step.Inputs.Fields.Select(field => $"{field.Key}={FormatExpression(field.Value)}"));
        var parameters = step.Parameters is null
            ? string.Empty
            : string.Join(" and ", step.Parameters.Fields.Select(field => $"{field.Key}={FormatExpression(field.Value)}"));

        if (inputs.Length > 0 && parameters.Length > 0)
        {
            return $"runs {step.OperatorName} as {step.Id} on {inputs} using {parameters}";
        }

        if (inputs.Length > 0)
        {
            return $"runs {step.OperatorName} as {step.Id} on {inputs}";
        }

        if (parameters.Length > 0)
        {
            return $"runs {step.OperatorName} as {step.Id} using {parameters}";
        }

        return $"runs {step.OperatorName} as {step.Id}";
    }

    private static string FormatViewport(ObjectExpression viewport)
    {
        var center = TryGetField(viewport, "center");
        var zoom = TryGetField(viewport, "zoom");
        if (center is ArrayLiteral { Items.Length: 2 } centerArray)
        {
            var lon = FormatExpression(centerArray.Items[0]);
            var lat = FormatExpression(centerArray.Items[1]);
            if (zoom is not null)
            {
                return $"center {lon} {lat} zoom {FormatExpression(zoom)}";
            }

            return $"center {lon} {lat}";
        }

        return string.Join(
            " ",
            viewport.Fields.Select(field => $"{field.Key} {FormatExpression(field.Value)}"));
    }

    private static string FormatScope(SpecExpression? expression)
        => expression switch
        {
            Cql2Expression cql => cql.Cql2Text,
            null => "true",
            _ => FormatExpression(expression)
        };

    private static string FormatExpression(SpecExpression expression)
    {
        return expression switch
        {
            LiteralNode { Kind: SpecTypeKind.String } literal => literal.String ?? string.Empty,
            LiteralNode { Kind: SpecTypeKind.Boolean } literal => literal.Boolean?.ToString().ToLowerInvariant() ?? "false",
            LiteralNode { Kind: SpecTypeKind.Integer } literal => literal.Integer?.ToString() ?? "0",
            LiteralNode { Kind: SpecTypeKind.Number } literal => literal.Number?.ToString("0.###") ?? "0",
            LiteralNode { Kind: SpecTypeKind.Distance or SpecTypeKind.Duration or SpecTypeKind.Area } literal =>
                $"{literal.Number?.ToString("0.###")}.{literal.Unit}",
            ReferenceNode reference => reference.Canonical,
            ArrayLiteral array => string.Join(", ", array.Items.Select(FormatExpression)),
            ObjectExpression obj => string.Join(", ", obj.Fields.Select(field => $"{field.Key}={FormatExpression(field.Value)}")),
            Cql2Expression cql => cql.Cql2Text,
            _ => expression.ToString() ?? string.Empty
        };
    }

    private static string GetSourceDisplayName(SourceBinding source)
        => TryGetField(source.Properties, "title") is LiteralNode { Kind: SpecTypeKind.String } title && !string.IsNullOrWhiteSpace(title.String)
            ? title.String!
            : TryGetField(source.Properties, "ref") is LiteralNode { Kind: SpecTypeKind.String } reference && !string.IsNullOrWhiteSpace(reference.String)
                ? reference.String!
                : source.Id;

    private static SpecExpression? TryGetField(ObjectExpression expression, string key)
    {
        foreach (var field in expression.Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal))
            {
                return field.Value;
            }
        }

        return null;
    }

    private static string BuildSectionText(IEnumerable<string> clauses)
    {
        var parts = clauses
            .Where(clause => !string.IsNullOrWhiteSpace(clause))
            .Select(UppercaseFirst)
            .ToArray();
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        if (parts.Length <= 2)
        {
            return string.Join(". ", parts) + ".";
        }

        var midpoint = (parts.Length + 1) / 2;
        var firstSentence = string.Join("; ", parts.Take(midpoint));
        var secondSentence = string.Join("; ", parts.Skip(midpoint));
        return $"{firstSentence}. {secondSentence}.";
    }

    private static string UppercaseFirst(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Suffix(int count) => count == 1 ? string.Empty : "s";
}
