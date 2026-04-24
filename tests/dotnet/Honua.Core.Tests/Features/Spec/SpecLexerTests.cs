// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Unit tests for <see cref="SpecLexer"/> — line/column tracking is the
/// single most important improvement over the existing CQL2 tokenizer so it
/// has the most coverage here.
/// </summary>
public sealed class SpecLexerTests
{
    [Fact]
    public void Tokenize_TracksLineAndColumnAcrossNewlines()
    {
        const string text = "grammar \"v1.0\"\nkind \"analysis\"\n";

        var tokens = new SpecLexer(text).Tokenize();

        var grammar = tokens[0];
        var grammarValue = tokens[1];
        var kind = tokens[2];
        var kindValue = tokens[3];

        grammar.Type.Should().Be(SpecTokenType.Identifier);
        grammar.Span.Line.Should().Be(1);
        grammar.Span.Column.Should().Be(1);

        grammarValue.Type.Should().Be(SpecTokenType.String);
        grammarValue.Span.Line.Should().Be(1);
        grammarValue.Span.Column.Should().Be(9);

        kind.Type.Should().Be(SpecTokenType.Identifier);
        kind.Span.Line.Should().Be(2);
        kind.Span.Column.Should().Be(1);

        kindValue.Span.Line.Should().Be(2);
        kindValue.Span.Column.Should().Be(6);
    }

    [Fact]
    public void Tokenize_HandlesCrlfLineEndings()
    {
        const string text = "grammar \"v1.0\"\r\nkind \"analysis\"";

        var tokens = new SpecLexer(text).Tokenize();

        tokens[2].Type.Should().Be(SpecTokenType.Identifier);
        tokens[2].Text.Should().Be("kind");
        tokens[2].Span.Line.Should().Be(2);
        tokens[2].Span.Column.Should().Be(1);
    }

    [Fact]
    public void Tokenize_BoolAndNullKeywords()
    {
        const string text = "a = true b = false c = null";
        var tokens = new SpecLexer(text).Tokenize();

        tokens[2].Type.Should().Be(SpecTokenType.Boolean);
        tokens[2].Text.Should().Be("true");
        tokens[5].Type.Should().Be(SpecTokenType.Boolean);
        tokens[5].Text.Should().Be("false");
        tokens[8].Type.Should().Be(SpecTokenType.Null);
    }

    [Fact]
    public void Tokenize_ParsesUnitLiteralAsSeparateTokens()
    {
        const string text = "distance = 500.m";
        var tokens = new SpecLexer(text).Tokenize();

        // The lexer emits Number '.' Identifier; the parser assembles the unit.
        tokens[0].Type.Should().Be(SpecTokenType.Identifier);
        tokens[0].Text.Should().Be("distance");
        tokens[1].Type.Should().Be(SpecTokenType.Equals);
        tokens[2].Type.Should().Be(SpecTokenType.Number);
        tokens[2].Text.Should().Be("500");
        tokens[3].Type.Should().Be(SpecTokenType.Dot);
        tokens[4].Type.Should().Be(SpecTokenType.Identifier);
        tokens[4].Text.Should().Be("m");
    }

    [Fact]
    public void Tokenize_AllowsDecimalFractionsWithinNumbers()
    {
        const string text = "x = 3.14";
        var tokens = new SpecLexer(text).Tokenize();

        tokens[2].Type.Should().Be(SpecTokenType.Number);
        tokens[2].Text.Should().Be("3.14");
        tokens[3].Type.Should().Be(SpecTokenType.EndOfInput);
    }

    [Fact]
    public void Tokenize_AllowsScientificNumbers()
    {
        const string text = "x = 1.5e-3";
        var tokens = new SpecLexer(text).Tokenize();

        tokens[2].Type.Should().Be(SpecTokenType.Number);
        tokens[2].Text.Should().Be("1.5e-3");
    }

    [Fact]
    public void Tokenize_ReferenceCapturesAtPrefix()
    {
        const string text = "target = @hospitals";
        var tokens = new SpecLexer(text).Tokenize();

        tokens[2].Type.Should().Be(SpecTokenType.Reference);
        tokens[2].Text.Should().Be("@hospitals");
    }

    [Fact]
    public void Tokenize_StringEscapesResolve()
    {
        const string text = "x = \"line1\\nline2\\t\\\"quoted\\\"\"";
        var tokens = new SpecLexer(text).Tokenize();

        tokens[2].Type.Should().Be(SpecTokenType.String);
        tokens[2].Text.Should().Be("line1\nline2\t\"quoted\"");
    }

    [Fact]
    public void Tokenize_CollectsHashLineComments()
    {
        const string text = "# header comment\ngrammar \"v1.0\"";
        var lexer = new SpecLexer(text);

        lexer.Tokenize();

        lexer.Comments.Should().HaveCount(1);
        lexer.Comments[0].Text.Should().Be("header comment");
    }

    [Fact]
    public void Tokenize_CollectsDoubleSlashLineComments()
    {
        const string text = "// a note\ngrammar \"v1.0\"";
        var lexer = new SpecLexer(text);

        lexer.Tokenize();

        lexer.Comments.Should().HaveCount(1);
        lexer.Comments[0].Text.Should().Be("a note");
    }

    [Fact]
    public void Tokenize_CollectsBlockComments()
    {
        const string text = "/* multi\n   line */ grammar \"v1.0\"";
        var lexer = new SpecLexer(text);

        var tokens = lexer.Tokenize();

        lexer.Comments.Should().HaveCount(1);
        lexer.Comments[0].Text.Should().Contain("multi").And.Contain("line");
        // Line/column must advance past the block comment.
        tokens[0].Type.Should().Be(SpecTokenType.Identifier);
        tokens[0].Text.Should().Be("grammar");
        tokens[0].Span.Line.Should().Be(2);
    }

    [Fact]
    public void Tokenize_UnterminatedStringReportsSyntaxError()
    {
        const string text = "x = \"unterminated";
        var lexer = new SpecLexer(text);

        lexer.Tokenize();

        lexer.Diagnostics.Should().ContainSingle(d =>
            d.Code == SpecDiagnosticCode.SyntaxError &&
            d.Severity == SpecDiagnosticSeverity.Error);
    }

    [Fact]
    public void Tokenize_UnterminatedBlockCommentReportsError()
    {
        const string text = "/* missing close";
        var lexer = new SpecLexer(text);

        lexer.Tokenize();

        lexer.Diagnostics.Should().ContainSingle(d =>
            d.Code == SpecDiagnosticCode.SyntaxError);
    }

    [Fact]
    public void Tokenize_UnknownEscapeReportsError()
    {
        const string text = "x = \"bad\\q\"";
        var lexer = new SpecLexer(text);

        lexer.Tokenize();

        lexer.Diagnostics.Should().ContainSingle(d =>
            d.Message.Contains("escape", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tokenize_BareAtReportsSyntaxError()
    {
        const string text = "x = @";
        var lexer = new SpecLexer(text);

        lexer.Tokenize();

        lexer.Diagnostics.Should().ContainSingle(d =>
            d.Code == SpecDiagnosticCode.SyntaxError);
    }

    [Fact]
    public void Tokenize_AppendsEndOfInputSentinel()
    {
        var tokens = new SpecLexer(string.Empty).Tokenize();

        tokens.Should().HaveCount(1);
        tokens[0].Type.Should().Be(SpecTokenType.EndOfInput);
    }

    [Fact]
    public void Tokenize_RejectsInputOverLimit()
    {
        var huge = new string('a', 1024 * 1024 + 1);

        Action act = () => _ = new SpecLexer(huge);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tokenize_SpanOffsetAndLengthArePopulated()
    {
        const string text = "grammar \"v1.0\"";
        var tokens = new SpecLexer(text).Tokenize();

        tokens[0].Span.Offset.Should().Be(0);
        tokens[0].Span.Length.Should().Be("grammar".Length);
        tokens[1].Span.Offset.Should().Be(8);
        // String span includes both quotes.
        tokens[1].Span.Length.Should().Be("\"v1.0\"".Length);
    }
}
