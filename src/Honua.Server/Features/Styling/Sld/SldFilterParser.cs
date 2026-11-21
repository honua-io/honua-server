// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Server.Features.Styling.Sld;

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
                    if (operands.Count == 0)
                    {
                        diagnostics.Add(Unsupported("And", "Empty And predicate cannot be represented; rule omitted.", ruleName));
                        return new SldFilterUnsupported("And", "Empty And predicate.");
                    }

                    return new SldFilterAnd(operands);
                }

            case "Or":
                {
                    var operands = element.Elements()
                        .Select(e => ParseExpression(e, diagnostics, ruleName))
                        .ToList();
                    if (operands.Count == 0)
                    {
                        diagnostics.Add(Unsupported("Or", "Empty Or predicate cannot be represented; rule omitted.", ruleName));
                        return new SldFilterUnsupported("Or", "Empty Or predicate.");
                    }

                    return new SldFilterOr(operands);
                }

            case "Not":
                {
                    var inner = element.Elements().FirstOrDefault();
                    if (inner == null)
                    {
                        diagnostics.Add(Unsupported("Not", "Empty Not predicate cannot be represented; rule omitted.", ruleName));
                        return new SldFilterUnsupported("Not", "Empty Not predicate.");
                    }

                    return new SldFilterNot(ParseExpression(inner, diagnostics, ruleName));
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
                return ParseLike(element, diagnostics, ruleName);

            case "PropertyIsBetween":
                return TryDecomposeBetween(element, diagnostics, ruleName);

            case "PropertyIsNull":
                diagnostics.Add(Unsupported(
                    "PropertyIsNull",
                    "PropertyIsNull has no canonical MapLibre equivalent; rule omitted.",
                    ruleName));
                return new SldFilterUnsupported("PropertyIsNull", null);

            case "Function":
                diagnostics.Add(Unsupported(
                    "OgcFunction",
                    $"OGC Function '{element.Attribute("name")?.Value}' is not portable; rule omitted.",
                    ruleName));
                return new SldFilterUnsupported("OgcFunction", element.Attribute("name")?.Value);

            case "FeatureId":
            case "ResourceId":
            case "GmlObjectId":
                diagnostics.Add(Unsupported(
                    local,
                    $"{local} predicate is not supported; rule omitted.",
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
                    $"Spatial/temporal predicate '{local}' is not supported in MapLibre filter expressions; rule omitted.",
                    ruleName));
                return new SldFilterUnsupported(local, null);

            default:
                diagnostics.Add(Unsupported(
                    local,
                    $"Filter predicate '{local}' is not recognized; rule omitted.",
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
            diagnostics.Add(Unsupported("PropertyIsBetween", "PropertyIsBetween requires PropertyName and literal bounds; rule omitted.", ruleName));
            return new SldFilterUnsupported("PropertyIsBetween", null);
        }

        return new SldFilterAnd(
        [
            new SldFilterComparison(SldFilterComparisonOperator.GreaterThanOrEqual, property, lower),
            new SldFilterComparison(SldFilterComparisonOperator.LessThanOrEqual, property, upper)
        ]);
    }

    private static SldFilter ParseLike(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        var (property, literal) = ReadPropertyAndLiteral(element);
        var wildAttr = element.Attribute("wildCard")?.Value ?? "%";
        var singleAttr = element.Attribute("singleChar")?.Value ?? "_";
        var escapeAttr = element.Attribute("escapeChar")?.Value ?? element.Attribute("escape")?.Value ?? "\\";
        var matchCase = string.Equals(element.Attribute("matchCase")?.Value, "false", StringComparison.OrdinalIgnoreCase)
            ? SldMatchCase.Insensitive
            : SldMatchCase.Sensitive;

        if (string.IsNullOrEmpty(property) || literal == null
            || wildAttr.Length != 1 || singleAttr.Length != 1 || escapeAttr.Length != 1)
        {
            diagnostics.Add(Unsupported(
                "PropertyIsLike",
                "PropertyIsLike requires PropertyName, Literal, and single-character wildcard attributes; rule omitted.",
                ruleName));
            return new SldFilterUnsupported("PropertyIsLike", null);
        }

        return new SldFilterLike
        {
            PropertyName = property,
            Pattern = literal,
            WildCard = wildAttr[0],
            SingleChar = singleAttr[0],
            EscapeChar = escapeAttr[0],
            MatchCase = matchCase
        };
    }

    private static (string? Property, string? Literal) ReadPropertyAndLiteral(XElement element)
    {
        string? property = null;
        string? literal = null;
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName is "PropertyName" or "ValueReference")
            {
                property = child.Value?.Trim();
            }
            else if (child.Name.LocalName == "Literal")
            {
                literal = child.Value;
            }
        }

        return (property, literal);
    }

    private static SldConversionDiagnostic Unsupported(string construct, string message, string? ruleName) => new()
    {
        Severity = SldDiagnosticSeverity.Warning,
        Construct = construct,
        Message = message,
        RuleName = ruleName
    };
}
