// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Grammar;

/// <summary>
/// Lexical analyzer for the brace-based Honua spec text form. Emits tokens
/// annotated with 1-based line/column so diagnostics can surface precise
/// positions (improvement over CQL2's character-offset-only tokens).
/// </summary>
public sealed class SpecLexer
{
    private const int MaxInputLength = 1024 * 1024; // 1 MiB safety cap.
    private const int MaxStringLiteralLength = 64 * 1024;

    private static readonly FrozenDictionary<string, SpecTokenType> _keywords =
        new Dictionary<string, SpecTokenType>(StringComparer.Ordinal)
        {
            ["true"] = SpecTokenType.Boolean,
            ["false"] = SpecTokenType.Boolean,
            ["null"] = SpecTokenType.Null
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly string _input;
    private readonly List<SpecToken> _tokens = [];
    private readonly List<SpecDiagnostic> _diagnostics = [];
    private readonly List<(string Path, string Text)> _comments = [];
    private int _offset;
    private int _line = 1;
    private int _column = 1;

    /// <summary>
    /// Creates a new lexer over <paramref name="input"/>.
    /// </summary>
    /// <param name="input">Spec source text.</param>
    public SpecLexer(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxInputLength)
        {
            throw new ArgumentException($"Spec input exceeds the {MaxInputLength:N0}-character safety cap.", nameof(input));
        }

        _input = input;
    }

    /// <summary>
    /// Gets comments collected during tokenization, in source order. Each item
    /// pairs a placeholder location key (<c>line:column</c>) with the raw
    /// comment text. The parser converts these into JSON-Pointer–keyed
    /// sidecar entries based on the surrounding block.
    /// </summary>
    public IReadOnlyList<(string LocationKey, string Text)> Comments => _comments;

    /// <summary>
    /// Tokenizes the input. Lexer errors become <see cref="SpecDiagnostic"/>
    /// instances rather than exceptions; an <c>EndOfInput</c> token is always
    /// appended so parsers can rely on look-ahead.
    /// </summary>
    /// <returns>Ordered token list.</returns>
    public IReadOnlyList<SpecToken> Tokenize()
    {
        _tokens.Clear();
        _diagnostics.Clear();
        _comments.Clear();

        while (_offset < _input.Length)
        {
            var ch = _input[_offset];

            if (IsInlineWhitespace(ch))
            {
                Advance();
                continue;
            }

            if (ch == '\n' || ch == '\r')
            {
                ConsumeLineBreak();
                continue;
            }

            if (ch == '#' || (ch == '/' && Peek(1) == '/'))
            {
                ReadLineComment();
                continue;
            }

            if (ch == '/' && Peek(1) == '*')
            {
                ReadBlockComment();
                continue;
            }

            switch (ch)
            {
                case '{':
                    AddSingleChar(SpecTokenType.LeftBrace, "{");
                    continue;
                case '}':
                    AddSingleChar(SpecTokenType.RightBrace, "}");
                    continue;
                case '[':
                    AddSingleChar(SpecTokenType.LeftBracket, "[");
                    continue;
                case ']':
                    AddSingleChar(SpecTokenType.RightBracket, "]");
                    continue;
                case '(':
                    AddSingleChar(SpecTokenType.LeftParen, "(");
                    continue;
                case ')':
                    AddSingleChar(SpecTokenType.RightParen, ")");
                    continue;
                case ',':
                    AddSingleChar(SpecTokenType.Comma, ",");
                    continue;
                case '=':
                    AddSingleChar(SpecTokenType.Equals, "=");
                    continue;
                case '.':
                    // A plain '.' is only meaningful inside a reference; we emit
                    // it as a Dot token so the parser can assemble dotted paths
                    // and unit-annotated numeric literals.
                    AddSingleChar(SpecTokenType.Dot, ".");
                    continue;
                case '"':
                    ReadString();
                    continue;
                case '@':
                    ReadReference();
                    continue;
            }

            if (char.IsDigit(ch))
            {
                ReadNumber();
                continue;
            }

            if (IsIdentifierStart(ch))
            {
                ReadIdentifier();
                continue;
            }

            EmitSyntaxDiagnostic($"Unexpected character '{ch}' in spec source.", Current(1));
            Advance();
        }

        _tokens.Add(new SpecToken(SpecTokenType.EndOfInput, string.Empty, Current(0)));
        return _tokens;
    }

    /// <summary>
    /// Diagnostics collected during tokenization. Consumed by the parser and
    /// surfaced in <see cref="SpecParseResult.Diagnostics"/>.
    /// </summary>
    public IReadOnlyList<SpecDiagnostic> Diagnostics => _diagnostics;

    private static bool IsInlineWhitespace(char ch) => ch == ' ' || ch == '\t';

    private static bool IsIdentifierStart(char ch) => ch == '_' || char.IsLetter(ch);

    private static bool IsIdentifierPart(char ch) => ch == '_' || char.IsLetterOrDigit(ch);

    private char Peek(int delta)
    {
        var pos = _offset + delta;
        return pos < _input.Length ? _input[pos] : '\0';
    }

    private void Advance()
    {
        if (_offset < _input.Length)
        {
            _offset++;
            _column++;
        }
    }

    private void ConsumeLineBreak()
    {
        if (_input[_offset] == '\r' && Peek(1) == '\n')
        {
            _offset += 2;
        }
        else
        {
            _offset++;
        }

        _line++;
        _column = 1;
    }

    private SourceSpan Current(int length)
        => new(_line, _column, _offset, length);

    private void AddSingleChar(SpecTokenType type, string text)
    {
        _tokens.Add(new SpecToken(type, text, Current(1)));
        Advance();
    }

    private void ReadLineComment()
    {
        var startLine = _line;
        var startColumn = _column;
        var startOffset = _offset;

        if (_input[_offset] == '#')
        {
            Advance();
        }
        else
        {
            Advance();
            Advance();
        }

        var start = _offset;
        while (_offset < _input.Length && _input[_offset] != '\n' && _input[_offset] != '\r')
        {
            Advance();
        }

        var text = _input.Substring(start, _offset - start).Trim();
        _comments.Add(($"{startLine}:{startColumn}:{startOffset}", text));
    }

    private void ReadBlockComment()
    {
        var startLine = _line;
        var startColumn = _column;
        var startOffset = _offset;

        // Skip the opening /*.
        Advance();
        Advance();

        var start = _offset;
        while (_offset < _input.Length)
        {
            if (_input[_offset] == '*' && Peek(1) == '/')
            {
                var text = _input.Substring(start, _offset - start).Trim();
                Advance();
                Advance();
                _comments.Add(($"{startLine}:{startColumn}:{startOffset}", text));
                return;
            }

            if (_input[_offset] == '\n' || _input[_offset] == '\r')
            {
                ConsumeLineBreak();
                continue;
            }

            Advance();
        }

        _diagnostics.Add(SpecDiagnostic.Error(
            SpecDiagnosticCode.SyntaxError,
            "Unterminated block comment.",
            new SourceSpan(startLine, startColumn, startOffset, _offset - startOffset)));
    }

    private void ReadString()
    {
        var startLine = _line;
        var startColumn = _column;
        var startOffset = _offset;
        var value = new StringBuilder();

        Advance(); // consume opening quote

        while (_offset < _input.Length)
        {
            var ch = _input[_offset];
            if (ch == '"')
            {
                Advance();
                var span = new SourceSpan(startLine, startColumn, startOffset, _offset - startOffset);
                _tokens.Add(new SpecToken(SpecTokenType.String, value.ToString(), span));
                return;
            }

            if (ch == '\\')
            {
                if (_offset + 1 >= _input.Length)
                {
                    break;
                }

                var next = _input[_offset + 1];
                switch (next)
                {
                    case '"':
                        value.Append('"');
                        break;
                    case '\\':
                        value.Append('\\');
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 'r':
                        value.Append('\r');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    case '/':
                        value.Append('/');
                        break;
                    default:
                        _diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.SyntaxError,
                            $"Unknown string escape '\\{next}'.",
                            new SourceSpan(_line, _column, _offset, 2)));
                        value.Append(next);
                        break;
                }

                Advance();
                Advance();
                continue;
            }

            if (ch == '\n' || ch == '\r')
            {
                _diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.SyntaxError,
                    "String literal is not terminated before end of line.",
                    new SourceSpan(startLine, startColumn, startOffset, _offset - startOffset)));
                return;
            }

            if (value.Length >= MaxStringLiteralLength)
            {
                _diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.SyntaxError,
                    $"String literal exceeds the {MaxStringLiteralLength:N0}-character cap.",
                    new SourceSpan(startLine, startColumn, startOffset, _offset - startOffset)));
                return;
            }

            value.Append(ch);
            Advance();
        }

        _diagnostics.Add(SpecDiagnostic.Error(
            SpecDiagnosticCode.SyntaxError,
            "Unterminated string literal.",
            new SourceSpan(startLine, startColumn, startOffset, _offset - startOffset)));
    }

    private void ReadNumber()
    {
        var startLine = _line;
        var startColumn = _column;
        var startOffset = _offset;
        var hasDot = false;

        while (_offset < _input.Length)
        {
            var ch = _input[_offset];
            if (char.IsDigit(ch))
            {
                Advance();
                continue;
            }

            if (ch == '.')
            {
                // Unit-annotated literal (5.km) — stop before the dot; the
                // parser reads the following Dot+Identifier and assembles the
                // unit. A decimal fraction (5.0) must be followed by a digit.
                if (hasDot || !char.IsDigit(Peek(1)))
                {
                    break;
                }

                hasDot = true;
                Advance();
                continue;
            }

            break;
        }

        // Optional scientific suffix (1e5, 1.5E-3).
        if (_offset < _input.Length && (_input[_offset] == 'e' || _input[_offset] == 'E'))
        {
            Advance();
            if (_offset < _input.Length && (_input[_offset] == '+' || _input[_offset] == '-'))
            {
                Advance();
            }

            while (_offset < _input.Length && char.IsDigit(_input[_offset]))
            {
                Advance();
            }
        }

        var length = _offset - startOffset;
        var text = _input.Substring(startOffset, length);
        _tokens.Add(new SpecToken(SpecTokenType.Number, text, new SourceSpan(startLine, startColumn, startOffset, length)));
    }

    private void ReadIdentifier()
    {
        var startLine = _line;
        var startColumn = _column;
        var startOffset = _offset;
        Advance();
        while (_offset < _input.Length && IsIdentifierPart(_input[_offset]))
        {
            Advance();
        }

        var length = _offset - startOffset;
        var text = _input.Substring(startOffset, length);
        var type = _keywords.TryGetValue(text, out var keyword) ? keyword : SpecTokenType.Identifier;
        _tokens.Add(new SpecToken(type, text, new SourceSpan(startLine, startColumn, startOffset, length)));
    }

    private void ReadReference()
    {
        var startLine = _line;
        var startColumn = _column;
        var startOffset = _offset;
        Advance(); // consume @

        if (_offset >= _input.Length || !IsIdentifierStart(_input[_offset]))
        {
            _diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.SyntaxError,
                "Reference '@' must be followed by an identifier.",
                new SourceSpan(startLine, startColumn, startOffset, 1)));
            _tokens.Add(new SpecToken(SpecTokenType.Reference, "@", new SourceSpan(startLine, startColumn, startOffset, 1)));
            return;
        }

        while (_offset < _input.Length && IsIdentifierPart(_input[_offset]))
        {
            Advance();
        }

        var length = _offset - startOffset;
        var text = _input.Substring(startOffset, length);
        _tokens.Add(new SpecToken(SpecTokenType.Reference, text, new SourceSpan(startLine, startColumn, startOffset, length)));
    }

    private void EmitSyntaxDiagnostic(string message, SourceSpan span)
    {
        _diagnostics.Add(SpecDiagnostic.Error(SpecDiagnosticCode.SyntaxError, message, span));
    }
}
