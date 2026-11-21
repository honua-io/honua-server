// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Infrastructure.Rendering;

namespace Honua.Server.Features.Styling.Sld;

internal static class SldLikeFilterConverter
{
    private const int MaxLikeSegments = 6;

    public static MapLibreExpression? Convert(SldFilterLike like, SldDiagnosticContext context)
    {
        var tokens = TokenizePattern(like);
        var hasMulti = tokens.Any(t => t.Kind == LikeTokenKind.MultiWildcard);
        var hasSingle = tokens.Any(t => t.Kind == LikeTokenKind.SingleWildcard);

        if (hasMulti && hasSingle)
        {
            context.Warn(
                "PropertyIsLike",
                "PropertyIsLike pattern mixes multi-character and single-character wildcards; rule omitted.");
            return null;
        }

        var operands = new LikeExpressionOperands
        {
            NonNull = BuildNonNullCheck(like.PropertyName),
            Value = BuildValueExpression(like.PropertyName, like.MatchCase)
        };

        if (!hasMulti && !hasSingle)
        {
            var literal = tokens.Count == 1 ? tokens[0].Text : string.Empty;
            return SldExpressionConverter.Expr(
                "all",
                operands.NonNull,
                SldExpressionConverter.Expr("==", operands.Value, new MapLibreExpression(literal)));
        }

        return hasSingle
            ? BuildFixedLengthLike(tokens, operands)
            : BuildWildcardLike(tokens, operands, context);
    }

    private static List<LikeToken> TokenizePattern(SldFilterLike like)
    {
        var tokens = new List<LikeToken>();
        var buffer = new StringBuilder();
        var pattern = like.Pattern;

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == like.EscapeChar && i + 1 < pattern.Length)
            {
                buffer.Append(pattern[++i]);
                continue;
            }

            if (ch == like.WildCard)
            {
                FlushLiteral(buffer, like.MatchCase, tokens);
                if (tokens.Count == 0 || tokens[^1].Kind != LikeTokenKind.MultiWildcard)
                {
                    tokens.Add(new LikeToken(LikeTokenKind.MultiWildcard, string.Empty));
                }

                continue;
            }

            if (ch == like.SingleChar)
            {
                FlushLiteral(buffer, like.MatchCase, tokens);
                tokens.Add(new LikeToken(LikeTokenKind.SingleWildcard, string.Empty));
                continue;
            }

            buffer.Append(ch);
        }

        FlushLiteral(buffer, like.MatchCase, tokens);
        return tokens;
    }

    private static void FlushLiteral(StringBuilder buffer, SldMatchCase matchCase, List<LikeToken> tokens)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var text = buffer.ToString();
        var normalized = matchCase == SldMatchCase.Insensitive ? text.ToLowerInvariant() : text;
        tokens.Add(new LikeToken(LikeTokenKind.Literal, normalized));
        buffer.Clear();
    }

    private static MapLibreExpression BuildFixedLengthLike(List<LikeToken> tokens, LikeExpressionOperands operands)
    {
        var conditions = new List<MapLibreExpression> { new("all"), operands.NonNull };
        var offset = 0;

        foreach (var token in tokens)
        {
            if (token.Kind == LikeTokenKind.SingleWildcard)
            {
                offset++;
                continue;
            }

            var end = offset + token.Text.Length;
            var slice = SldExpressionConverter.Expr(
                "slice",
                operands.Value,
                new MapLibreExpression((double)offset),
                new MapLibreExpression((double)end));
            conditions.Add(SldExpressionConverter.Expr("==", slice, new MapLibreExpression(token.Text)));
            offset = end;
        }

        conditions.Insert(2, SldExpressionConverter.Expr(
            "==",
            SldExpressionConverter.Expr("length", operands.Value),
            new MapLibreExpression((double)offset)));
        return new MapLibreExpression(conditions.ToArray());
    }

    private static MapLibreExpression? BuildWildcardLike(
        List<LikeToken> tokens,
        LikeExpressionOperands operands,
        SldDiagnosticContext context)
    {
        var parts = tokens.Where(t => t.Kind == LikeTokenKind.Literal).Select(t => t.Text).ToList();
        if (parts.Count == 0)
        {
            return operands.NonNull;
        }

        if (parts.Count > MaxLikeSegments)
        {
            context.Warn("PropertyIsLike", $"PropertyIsLike pattern has more than {MaxLikeSegments} literal segments; rule omitted.");
            return null;
        }

        var shape = new WildcardPatternShape
        {
            Parts = parts,
            Anchoring = ClassifyAnchoring(tokens)
        };
        return parts.Count == 1
            ? BuildSingleSegmentLike(shape, operands)
            : BuildMultiSegmentLike(shape, operands);
    }

    private static LikeAnchoring ClassifyAnchoring(List<LikeToken> tokens)
    {
        var startsWild = tokens[0].Kind == LikeTokenKind.MultiWildcard;
        var endsWild = tokens[^1].Kind == LikeTokenKind.MultiWildcard;
        if (startsWild && endsWild)
        {
            return LikeAnchoring.Floating;
        }

        if (!startsWild && endsWild)
        {
            return LikeAnchoring.Prefix;
        }

        return startsWild ? LikeAnchoring.Suffix : LikeAnchoring.BothEnds;
    }

    private static MapLibreExpression BuildSingleSegmentLike(WildcardPatternShape shape, LikeExpressionOperands operands)
    {
        var segment = shape.Parts[0];
        if (shape.Anchoring == LikeAnchoring.Floating)
        {
            return SldExpressionConverter.Expr(
                "all",
                operands.NonNull,
                SldExpressionConverter.Expr("in", new MapLibreExpression(segment), operands.Value));
        }

        if (shape.Anchoring == LikeAnchoring.Prefix)
        {
            return SldExpressionConverter.Expr(
                "all",
                operands.NonNull,
                SldExpressionConverter.Expr(
                    "==",
                    SldExpressionConverter.Expr("index-of", new MapLibreExpression(segment), operands.Value),
                    new MapLibreExpression(0d)));
        }

        var lenExpr = SldExpressionConverter.Expr("length", operands.Value);
        var segLen = new MapLibreExpression((double)segment.Length);
        var suffixSlice = SldExpressionConverter.Expr("slice", operands.Value, SldExpressionConverter.Expr("-", lenExpr, segLen));
        return SldExpressionConverter.Expr(
            "all",
            operands.NonNull,
            SldExpressionConverter.Expr(">=", lenExpr, segLen),
            SldExpressionConverter.Expr("==", suffixSlice, new MapLibreExpression(segment)));
    }

    private static MapLibreExpression BuildMultiSegmentLike(WildcardPatternShape shape, LikeExpressionOperands operands)
    {
        var conditions = new List<MapLibreExpression> { new("all"), operands.NonNull };
        var cursor = AppendLeadingSegment(shape, operands.Value, conditions);
        var endsWithLiteral = shape.Anchoring is LikeAnchoring.Suffix or LikeAnchoring.BothEnds;
        var lastMiddle = endsWithLiteral ? shape.Parts.Count - 2 : shape.Parts.Count - 1;

        for (var i = 1; i <= lastMiddle; i++)
        {
            var found = SldExpressionConverter.Expr("index-of", new MapLibreExpression(shape.Parts[i]), operands.Value, cursor);
            conditions.Add(SldExpressionConverter.Expr(">=", found, new MapLibreExpression(0d)));
            cursor = SldExpressionConverter.Expr("+", found, new MapLibreExpression((double)shape.Parts[i].Length));
        }

        if (endsWithLiteral)
        {
            var last = shape.Parts[^1];
            var tailStart = SldExpressionConverter.Expr("-", SldExpressionConverter.Expr("length", operands.Value), new MapLibreExpression((double)last.Length));
            conditions.Add(SldExpressionConverter.Expr(">=", tailStart, cursor));
            conditions.Add(SldExpressionConverter.Expr("==", SldExpressionConverter.Expr("slice", operands.Value, tailStart), new MapLibreExpression(last)));
        }

        return new MapLibreExpression(conditions.ToArray());
    }

    private static MapLibreExpression AppendLeadingSegment(
        WildcardPatternShape shape,
        MapLibreExpression valueExpr,
        List<MapLibreExpression> conditions)
    {
        var first = shape.Parts[0];
        if (shape.Anchoring is LikeAnchoring.Prefix or LikeAnchoring.BothEnds)
        {
            conditions.Add(SldExpressionConverter.Expr(
                "==",
                SldExpressionConverter.Expr("index-of", new MapLibreExpression(first), valueExpr),
                new MapLibreExpression(0d)));
            return new MapLibreExpression((double)first.Length);
        }

        var found = SldExpressionConverter.Expr("index-of", new MapLibreExpression(first), valueExpr);
        conditions.Add(SldExpressionConverter.Expr(">=", found, new MapLibreExpression(0d)));
        return SldExpressionConverter.Expr("+", found, new MapLibreExpression((double)first.Length));
    }

    private static MapLibreExpression BuildNonNullCheck(string propertyName)
        => SldExpressionConverter.Expr(
            "!=",
            SldExpressionConverter.Expr("typeof", SldExpressionConverter.Expr("get", new MapLibreExpression(propertyName))),
            new MapLibreExpression("null"));

    private static MapLibreExpression BuildValueExpression(string propertyName, SldMatchCase matchCase)
    {
        var asString = SldExpressionConverter.Expr("to-string", SldExpressionConverter.Expr("get", new MapLibreExpression(propertyName)));
        return matchCase == SldMatchCase.Insensitive
            ? SldExpressionConverter.Expr("downcase", asString)
            : asString;
    }

    private enum LikeTokenKind
    {
        Literal,
        MultiWildcard,
        SingleWildcard
    }

    private enum LikeAnchoring
    {
        Floating,
        Prefix,
        Suffix,
        BothEnds
    }

    private readonly record struct LikeToken(LikeTokenKind Kind, string Text);

    private sealed record LikeExpressionOperands
    {
        public required MapLibreExpression NonNull { get; init; }
        public required MapLibreExpression Value { get; init; }
    }

    private sealed record WildcardPatternShape
    {
        public required List<string> Parts { get; init; }
        public required LikeAnchoring Anchoring { get; init; }
    }
}
