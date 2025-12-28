// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Queries.Filters.Cql2;

/// <summary>
/// Lexical analyzer for CQL2-Text expressions
/// </summary>
public sealed class Cql2Lexer
{
    private readonly string _input;
    private int _position;
    private readonly List<Cql2Token> _tokens = [];

    /// <summary>
    /// Keywords that are recognized as special tokens
    /// </summary>
    private static readonly Dictionary<string, Cql2TokenType> _keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "AND", Cql2TokenType.And },
        { "OR", Cql2TokenType.Or },
        { "NOT", Cql2TokenType.Not },
        { "LIKE", Cql2TokenType.Like },
        { "BETWEEN", Cql2TokenType.Between },
        { "IN", Cql2TokenType.In },
        { "IS", Cql2TokenType.IsNull }, // Will be handled specially for "IS NULL"
        { "NULL", Cql2TokenType.Null },
        { "TRUE", Cql2TokenType.Boolean },
        { "FALSE", Cql2TokenType.Boolean },
        { "DIV", Cql2TokenType.Div },
        { "CASEI", Cql2TokenType.Casei },
        { "ACCENTI", Cql2TokenType.Accenti },
        { "DATE", Cql2TokenType.Date },
        { "TIMESTAMP", Cql2TokenType.Timestamp },
        { "INTERVAL", Cql2TokenType.Interval },

        // Spatial predicates
        { "S_INTERSECTS", Cql2TokenType.S_Intersects },
        { "S_CONTAINS", Cql2TokenType.S_Contains },
        { "S_WITHIN", Cql2TokenType.S_Within },
        { "S_CROSSES", Cql2TokenType.S_Crosses },
        { "S_TOUCHES", Cql2TokenType.S_Touches },
        { "S_OVERLAPS", Cql2TokenType.S_Overlaps },
        { "S_DISJOINT", Cql2TokenType.S_Disjoint },
        { "S_EQUALS", Cql2TokenType.S_Equals },
        { "S_DWITHIN", Cql2TokenType.S_DWithin },
        { "S_BEYOND", Cql2TokenType.S_Beyond },

        // Temporal predicates
        { "T_AFTER", Cql2TokenType.T_After },
        { "T_BEFORE", Cql2TokenType.T_Before },
        { "T_CONTAINS", Cql2TokenType.T_Contains },
        { "T_DISJOINT", Cql2TokenType.T_Disjoint },
        { "T_DURING", Cql2TokenType.T_During },
        { "T_EQUALS", Cql2TokenType.T_Equals },
        { "T_FINISHEDBY", Cql2TokenType.T_FinishedBy },
        { "T_FINISHES", Cql2TokenType.T_Finishes },
        { "T_INTERSECTS", Cql2TokenType.T_Intersects },
        { "T_MEETS", Cql2TokenType.T_Meets },
        { "T_METBY", Cql2TokenType.T_MetBy },
        { "T_OVERLAPPEDBY", Cql2TokenType.T_OverlappedBy },
        { "T_OVERLAPS", Cql2TokenType.T_Overlaps },
        { "T_STARTEDBY", Cql2TokenType.T_StartedBy },
        { "T_STARTS", Cql2TokenType.T_Starts },

        // Array predicates
        { "A_EQUALS", Cql2TokenType.A_Equals },
        { "A_CONTAINS", Cql2TokenType.A_Contains },
        { "A_CONTAINEDBY", Cql2TokenType.A_ContainedBy },
        { "A_OVERLAPS", Cql2TokenType.A_Overlaps },

        // Geometry types
        { "POINT", Cql2TokenType.Point },
        { "LINESTRING", Cql2TokenType.LineString },
        { "POLYGON", Cql2TokenType.Polygon },
        { "MULTIPOINT", Cql2TokenType.MultiPoint },
        { "MULTILINESTRING", Cql2TokenType.MultiLineString },
        { "MULTIPOLYGON", Cql2TokenType.MultiPolygon },
        { "GEOMETRYCOLLECTION", Cql2TokenType.GeometryCollection },
        { "BBOX", Cql2TokenType.Bbox }
    };

    public Cql2Lexer(string input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _position = 0;
    }

    /// <summary>
    /// Tokenizes the input CQL2-Text expression
    /// </summary>
    /// <returns>List of tokens</returns>
    public IReadOnlyList<Cql2Token> Tokenize()
    {
        _tokens.Clear();
        _position = 0;

        while (_position < _input.Length)
        {
            SkipWhitespace();

            if (_position >= _input.Length)
                break;

            var ch = _input[_position];

            switch (ch)
            {
                case '(':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.LeftParen, "(", _position));
                    _position++;
                    break;

                case ')':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.RightParen, ")", _position));
                    _position++;
                    break;

                case ',':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.Comma, ",", _position));
                    _position++;
                    break;

                case '+':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.Plus, "+", _position));
                    _position++;
                    break;

                case '-':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.Minus, "-", _position));
                    _position++;
                    break;

                case '*':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.Star, "*", _position));
                    _position++;
                    break;

                case '/':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.Slash, "/", _position));
                    _position++;
                    break;

                case '%':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.Percent, "%", _position));
                    _position++;
                    break;

                case '^':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.Caret, "^", _position));
                    _position++;
                    break;

                case '=':
                    _tokens.Add(Cql2Token.Create(Cql2TokenType.Equal, "=", _position));
                    _position++;
                    break;

                case '<':
                    if (PeekNext() == '>')
                    {
                        _tokens.Add(Cql2Token.Create(Cql2TokenType.NotEqual, "<>", _position, 2));
                        _position += 2;
                    }
                    else if (PeekNext() == '=')
                    {
                        _tokens.Add(Cql2Token.Create(Cql2TokenType.LessThanOrEqual, "<=", _position, 2));
                        _position += 2;
                    }
                    else
                    {
                        _tokens.Add(Cql2Token.Create(Cql2TokenType.LessThan, "<", _position));
                        _position++;
                    }
                    break;

                case '>':
                    if (PeekNext() == '=')
                    {
                        _tokens.Add(Cql2Token.Create(Cql2TokenType.GreaterThanOrEqual, ">=", _position, 2));
                        _position += 2;
                    }
                    else
                    {
                        _tokens.Add(Cql2Token.Create(Cql2TokenType.GreaterThan, ">", _position));
                        _position++;
                    }
                    break;

                case '\'':
                    ReadStringLiteral();
                    break;

                case '"':
                    ReadQuotedIdentifier();
                    break;

                default:
                    if (char.IsDigit(ch) || (ch == '.' && char.IsDigit(PeekNext())))
                    {
                        ReadNumericLiteral();
                    }
                    else if (char.IsLetter(ch) || ch == '_')
                    {
                        ReadIdentifierOrKeyword();
                    }
                    else if (ch == ':')
                    {
                        ReadIdentifierOrKeyword();
                    }
                    else
                    {
                        throw new ArgumentException($"Unexpected character '{ch}' at position {_position}");
                    }
                    break;
            }
        }

        _tokens.Add(Cql2Token.Create(Cql2TokenType.EndOfInput, "", _position));
        return _tokens;
    }

    private void SkipWhitespace()
    {
        while (_position < _input.Length && char.IsWhiteSpace(_input[_position]))
        {
            _position++;
        }
    }

    private char PeekNext()
    {
        return _position + 1 < _input.Length ? _input[_position + 1] : '\0';
    }

    private void ReadStringLiteral()
    {
        var start = _position;
        _position++; // Skip opening quote

        var value = new StringBuilder();
        var terminated = false;

        while (_position < _input.Length)
        {
            var ch = _input[_position];
            if (ch == '\'')
            {
                if (PeekNext() == '\'') // Escaped quote
                {
                    value.Append('\'');
                    _position += 2;
                }
                else
                {
                    _position++; // Skip closing quote
                    terminated = true;
                    break;
                }
            }
            else
            {
                value.Append(ch);
                _position++;
            }
        }

        if (!terminated)
        {
            throw new ArgumentException($"Unterminated quoted string starting at position {start}");
        }

        _tokens.Add(Cql2Token.Create(Cql2TokenType.Text, value.ToString(), start, _position - start));
    }

    private void ReadQuotedIdentifier()
    {
        var start = _position;
        _position++; // Skip opening quote

        var value = new StringBuilder();
        while (_position < _input.Length && _input[_position] != '"')
        {
            value.Append(_input[_position]);
            _position++;
        }

        if (_position < _input.Length)
            _position++; // Skip closing quote

        _tokens.Add(Cql2Token.Create(Cql2TokenType.Identifier, value.ToString(), start, _position - start));
    }

    private void ReadNumericLiteral()
    {
        var start = _position;

        while (_position < _input.Length &&
               (char.IsDigit(_input[_position]) || _input[_position] == '.'))
        {
            _position++;
        }

        var value = _input[start.._position];
        _tokens.Add(Cql2Token.Create(Cql2TokenType.Number, value, start, _position - start));
    }

    private void ReadIdentifierOrKeyword()
    {
        var start = _position;

        while (_position < _input.Length &&
               (char.IsLetterOrDigit(_input[_position]) ||
                _input[_position] == '_' ||
                _input[_position] == ':' ||
                _input[_position] == '.'))
        {
            _position++;
        }

        var value = _input[start.._position];

        // Check for keywords
        var tokenType = _keywords.TryGetValue(value, out var keywordType)
            ? keywordType
            : Cql2TokenType.Identifier;

        _tokens.Add(Cql2Token.Create(tokenType, value, start, _position - start));
    }
}
