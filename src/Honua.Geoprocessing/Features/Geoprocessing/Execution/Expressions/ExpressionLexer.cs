// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Geoprocessing.Execution.Expressions;

/// <summary>The lexical token kinds the expression grammar recognizes.</summary>
internal enum ExpressionTokenKind
{
    Number,
    String,
    Identifier,
    True,
    False,
    Null,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    LessThan,
    LessEqual,
    GreaterThan,
    GreaterEqual,
    EqualEqual,
    NotEqual,
    AndAnd,
    OrOr,
    Not,
    Question,
    Colon,
    Comma,
    LeftParen,
    RightParen,
    End,
}

/// <summary>An immutable lexical token with its source value for literals/identifiers.</summary>
internal readonly record struct ExpressionToken(ExpressionTokenKind Kind, string Text, double Number = 0d);

/// <summary>
/// Hand-written lexer for the computed-field expression grammar. It accepts only a
/// fixed character/operator alphabet; any other character is rejected immediately,
/// so the parser never sees an unexpected token. No reflection, fully AOT-safe.
/// </summary>
internal static class ExpressionLexer
{
    /// <summary>Tokenizes <paramref name="source"/> into a terminated token list.</summary>
    /// <exception cref="ExpressionParseException">For any disallowed character or malformed literal.</exception>
    public static IReadOnlyList<ExpressionToken> Tokenize(string source)
    {
        var tokens = new List<ExpressionToken>();
        var i = 0;
        var n = source.Length;

        while (i < n)
        {
            var c = source[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '+': tokens.Add(new(ExpressionTokenKind.Plus, "+")); i++; continue;
                case '-': tokens.Add(new(ExpressionTokenKind.Minus, "-")); i++; continue;
                case '*': tokens.Add(new(ExpressionTokenKind.Star, "*")); i++; continue;
                case '/': tokens.Add(new(ExpressionTokenKind.Slash, "/")); i++; continue;
                case '%': tokens.Add(new(ExpressionTokenKind.Percent, "%")); i++; continue;
                case '?': tokens.Add(new(ExpressionTokenKind.Question, "?")); i++; continue;
                case ':': tokens.Add(new(ExpressionTokenKind.Colon, ":")); i++; continue;
                case ',': tokens.Add(new(ExpressionTokenKind.Comma, ",")); i++; continue;
                case '(': tokens.Add(new(ExpressionTokenKind.LeftParen, "(")); i++; continue;
                case ')': tokens.Add(new(ExpressionTokenKind.RightParen, ")")); i++; continue;
                case '<':
                    if (Peek(source, i + 1) == '=') { tokens.Add(new(ExpressionTokenKind.LessEqual, "<=")); i += 2; }
                    else { tokens.Add(new(ExpressionTokenKind.LessThan, "<")); i++; }
                    continue;
                case '>':
                    if (Peek(source, i + 1) == '=') { tokens.Add(new(ExpressionTokenKind.GreaterEqual, ">=")); i += 2; }
                    else { tokens.Add(new(ExpressionTokenKind.GreaterThan, ">")); i++; }
                    continue;
                case '=':
                    if (Peek(source, i + 1) == '=') { tokens.Add(new(ExpressionTokenKind.EqualEqual, "==")); i += 2; continue; }
                    throw new ExpressionParseException("unexpected '='; use '==' for equality");
                case '!':
                    if (Peek(source, i + 1) == '=') { tokens.Add(new(ExpressionTokenKind.NotEqual, "!=")); i += 2; }
                    else { tokens.Add(new(ExpressionTokenKind.Not, "!")); i++; }
                    continue;
                case '&':
                    if (Peek(source, i + 1) == '&') { tokens.Add(new(ExpressionTokenKind.AndAnd, "&&")); i += 2; continue; }
                    throw new ExpressionParseException("unexpected '&'; use '&&' for logical and");
                case '|':
                    if (Peek(source, i + 1) == '|') { tokens.Add(new(ExpressionTokenKind.OrOr, "||")); i += 2; continue; }
                    throw new ExpressionParseException("unexpected '|'; use '||' for logical or");
                case '"':
                case '\'':
                    i = ReadString(source, i, c, tokens);
                    continue;
            }

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(source, i + 1))))
            {
                i = ReadNumber(source, i, tokens);
                continue;
            }

            if (c == '_' || char.IsLetter(c))
            {
                i = ReadIdentifier(source, i, tokens);
                continue;
            }

            throw new ExpressionParseException(
                $"unexpected character '{c}' at position {i}");
        }

        tokens.Add(new(ExpressionTokenKind.End, "<end>"));
        return tokens;
    }

    private static char Peek(string s, int index) => index < s.Length ? s[index] : '\0';

    private static int ReadString(string source, int start, char quote, List<ExpressionToken> tokens)
    {
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\\')
            {
                var next = i + 1 < source.Length ? source[i + 1] : '\0';
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    default:
                        throw new ExpressionParseException($"invalid escape sequence '\\{next}' in string literal");
                }

                i += 2;
                continue;
            }

            if (c == quote)
            {
                tokens.Add(new(ExpressionTokenKind.String, sb.ToString()));
                return i + 1;
            }

            sb.Append(c);
            i++;
        }

        throw new ExpressionParseException("unterminated string literal");
    }

    private static int ReadNumber(string source, int start, List<ExpressionToken> tokens)
    {
        var i = start;
        var n = source.Length;
        var seenDot = false;
        var seenExp = false;

        while (i < n)
        {
            var c = source[i];
            if (char.IsDigit(c))
            {
                i++;
            }
            else if (c == '.' && !seenDot && !seenExp)
            {
                seenDot = true;
                i++;
            }
            else if ((c == 'e' || c == 'E') && !seenExp)
            {
                seenExp = true;
                i++;
                if (i < n && (source[i] == '+' || source[i] == '-'))
                {
                    i++;
                }
            }
            else
            {
                break;
            }
        }

        var text = source[start..i];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ExpressionParseException($"invalid numeric literal '{text}'");
        }

        tokens.Add(new(ExpressionTokenKind.Number, text, value));
        return i;
    }

    private static int ReadIdentifier(string source, int start, List<ExpressionToken> tokens)
    {
        var i = start;
        var n = source.Length;
        while (i < n && (source[i] == '_' || char.IsLetterOrDigit(source[i])))
        {
            i++;
        }

        var text = source[start..i];
        var token = text.ToLowerInvariant() switch
        {
            "true" => new ExpressionToken(ExpressionTokenKind.True, text),
            "false" => new ExpressionToken(ExpressionTokenKind.False, text),
            "null" => new ExpressionToken(ExpressionTokenKind.Null, text),
            "and" => new ExpressionToken(ExpressionTokenKind.AndAnd, "&&"),
            "or" => new ExpressionToken(ExpressionTokenKind.OrOr, "||"),
            "not" => new ExpressionToken(ExpressionTokenKind.Not, "!"),
            _ => new ExpressionToken(ExpressionTokenKind.Identifier, text),
        };

        tokens.Add(token);
        return i;
    }
}
