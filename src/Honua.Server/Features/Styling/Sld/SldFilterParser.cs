// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Server.Features.Styling.Sld;

/// <summary>
/// Parses an OGC Filter element subtree into the internal <see cref="SldFilter"/> AST.
/// Only the subset that maps cleanly to MapLibre is recognized; anything else is
/// surfaced as <see cref="SldFilterUnsupported"/> with a diagnostic.
/// </summary>
internal static class SldFilterParser
{
    public static SldFilter? ParseFilter(
        XElement filterElement,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        var firstChild = filterElement.Elements().FirstOrDefault();
        if (firstChild == null)
        {
            return null;
        }

        return ParseExpression(firstChild, diagnostics, ruleName);
    }

    private static SldFilter ParseExpression(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        var local = element.Name.LocalName;
        switch (local)
        {
            case "And":
                {
                    var operands = element.Elements()
                        .Select(e => ParseExpression(e, diagnostics, ruleName))
                        .ToList();
                    return new SldFilterAnd(operands);
                }

            case "Or":
                {
                    var operands = element.Elements()
                        .Select(e => ParseExpression(e, diagnostics, ruleName))
                        .ToList();
                    return new SldFilterOr(operands);
                }

            case "Not":
                {
                    var inner = element.Elements().FirstOrDefault();
                    return inner == null
                        ? new SldFilterUnsupported("Not", "Empty Not predicate.")
                        : new SldFilterNot(ParseExpression(inner, diagnostics, ruleName));
                }

            case "PropertyIsEqualTo":
                return ParseComparison(element, SldFilterComparisonOperator.Equal, diagnostics, ruleName);
            case "PropertyIsNotEqualTo":
                return ParseComparison(element, SldFilterComparisonOperator.NotEqual, diagnostics, ruleName);
            case "PropertyIsLessThan":
                return ParseComparison(element, SldFilterComparisonOperator.LessThan, diagnostics, ruleName);
            case "PropertyIsLessThanOrEqualTo":
                return ParseComparison(element, SldFilterComparisonOperator.LessThanOrEqual, diagnostics, ruleName);
            case "PropertyIsGreaterThan":
                return ParseComparison(element, SldFilterComparisonOperator.GreaterThan, diagnostics, ruleName);
            case "PropertyIsGreaterThanOrEqualTo":
                return ParseComparison(element, SldFilterComparisonOperator.GreaterThanOrEqual, diagnostics, ruleName);
            case "PropertyIsLike":
                // SLD wildcard semantics (default `%` and `_`, configurable via wildCard/singleChar
                // attributes) have no portable MapLibre filter equivalent. Treat as unsupported so
                // the rule renders unfiltered rather than matching the literal pattern string.
                diagnostics.Add(Unsupported(
                    "PropertyIsLike",
                    "PropertyIsLike has no portable MapLibre equivalent; layer renders unfiltered.",
                    ruleName));
                return new SldFilterUnsupported("PropertyIsLike", null);

            case "PropertyIsBetween":
                diagnostics.Add(Unsupported(
                    "PropertyIsBetween",
                    "PropertyIsBetween is decomposed into >= and <= when possible; using unfiltered fallback otherwise.",
                    ruleName));
                return TryDecomposeBetween(element, diagnostics, ruleName);

            case "PropertyIsNull":
                diagnostics.Add(Unsupported(
                    "PropertyIsNull",
                    "PropertyIsNull has no canonical MapLibre equivalent; layer renders unfiltered.",
                    ruleName));
                return new SldFilterUnsupported("PropertyIsNull", null);

            case "Function":
                diagnostics.Add(Unsupported(
                    "OgcFunction",
                    $"OGC Function '{element.Attribute("name")?.Value}' is not portable; layer renders unfiltered.",
                    ruleName));
                return new SldFilterUnsupported("OgcFunction", element.Attribute("name")?.Value);

            case "FeatureId":
            case "ResourceId":
            case "GmlObjectId":
                diagnostics.Add(Unsupported(
                    local,
                    $"{local} predicate is not supported.",
                    ruleName));
                return new SldFilterUnsupported(local, null);

            // Spatial and temporal predicates.
            case "BBOX":
            case "Intersects":
            case "Contains":
            case "Within":
            case "Crosses":
            case "Touches":
            case "Disjoint":
            case "Overlaps":
            case "Equals":
            case "Beyond":
            case "DWithin":
            case "After":
            case "Before":
            case "During":
                diagnostics.Add(Unsupported(
                    local,
                    $"Spatial/temporal predicate '{local}' is not supported in MapLibre filter expressions.",
                    ruleName));
                return new SldFilterUnsupported(local, null);

            default:
                diagnostics.Add(Unsupported(
                    local,
                    $"Filter predicate '{local}' is not recognized; layer renders unfiltered.",
                    ruleName));
                return new SldFilterUnsupported(local, null);
        }
    }

    private static SldFilter ParseComparison(
        XElement element,
        SldFilterComparisonOperator op,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        string? property = null;
        string? literal = null;

        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "PropertyName":
                case "ValueReference":
                    property = child.Value?.Trim();
                    break;
                case "Literal":
                    literal = child.Value;
                    break;
            }
        }

        if (string.IsNullOrEmpty(property) || literal == null)
        {
            diagnostics.Add(Unsupported(
                element.Name.LocalName,
                $"{element.Name.LocalName} requires a PropertyName/Literal pair; complex expression dropped.",
                ruleName));
            return new SldFilterUnsupported(element.Name.LocalName, null);
        }

        return new SldFilterComparison(op, property, literal);
    }

    private static SldFilter TryDecomposeBetween(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        string? property = null;
        string? lower = null;
        string? upper = null;

        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "PropertyName":
                case "ValueReference":
                    property = child.Value?.Trim();
                    break;
                case "LowerBoundary":
                    lower = child.Elements().FirstOrDefault(e => e.Name.LocalName == "Literal")?.Value;
                    break;
                case "UpperBoundary":
                    upper = child.Elements().FirstOrDefault(e => e.Name.LocalName == "Literal")?.Value;
                    break;
            }
        }

        if (string.IsNullOrEmpty(property) || string.IsNullOrEmpty(lower) || string.IsNullOrEmpty(upper))
        {
            return new SldFilterUnsupported("PropertyIsBetween", null);
        }

        return new SldFilterAnd(new List<SldFilter>
        {
            new SldFilterComparison(SldFilterComparisonOperator.GreaterThanOrEqual, property, lower),
            new SldFilterComparison(SldFilterComparisonOperator.LessThanOrEqual, property, upper)
        });
    }

    private static SldConversionDiagnostic Unsupported(string construct, string message, string? ruleName) => new()
    {
        Severity = SldDiagnosticSeverity.Warning,
        Construct = construct,
        Message = message,
        RuleName = ruleName
    };
}
