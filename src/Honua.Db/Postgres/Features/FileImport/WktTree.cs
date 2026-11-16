// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Db.Postgres.Features.FileImport;

// A WKT1 node: KEYWORD[argument, ...]. Quoted strings, numbers and bare enum tokens are kept as text;
// nested nodes are WktNode values.
internal sealed record WktNode(string Keyword, IReadOnlyList<object> Arguments)
{
    public WktNode? Child(string keyword) => Children(keyword).FirstOrDefault();

    public IEnumerable<WktNode> Children(string keyword) =>
        Arguments.OfType<WktNode>().Where(node => node.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));

    public string? Text(int index) => index < Arguments.Count ? Arguments[index] as string : null;

    public double? Number(int index) =>
        double.TryParse(Text(index), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

// Bounded recursive-descent reader for WKT1 (OGC and ESRI dialects). Returns null for anything it cannot read.
internal static class WktTree
{
    private const int MaxLength = 65_536;
    private const int MaxDepth = 32;

    public static WktNode? TryParse(string wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt) || wkt.Length > MaxLength)
            return null;
        var reader = new Reader(wkt);
        var node = reader.ReadNode(0);
        reader.SkipWhitespace();
        return node is not null && reader.AtEnd ? node : null;
    }

    private sealed class Reader(string text)
    {
        private int _position;

        public bool AtEnd => _position >= text.Length;

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(text[_position]))
                _position++;
        }

        public WktNode? ReadNode(int depth)
        {
            SkipWhitespace();
            var keyword = ReadToken();
            if (depth > MaxDepth || keyword.Length == 0 || !char.IsLetter(keyword[0]) || AtEnd)
                return null;
            var close = text[_position] switch { '[' => ']', '(' => ')', _ => '\0' };
            if (close == '\0')
                return null;
            _position++;
            var arguments = new List<object>();
            while (true)
            {
                var argument = ReadArgument(depth);
                if (argument is null)
                    return null;
                arguments.Add(argument);
                SkipWhitespace();
                if (AtEnd)
                    return null;
                var next = text[_position++];
                if (next == close)
                    return new WktNode(keyword, arguments);
                if (next != ',')
                    return null;
            }
        }

        private object? ReadArgument(int depth)
        {
            SkipWhitespace();
            if (AtEnd)
                return null;
            if (text[_position] == '"')
                return ReadQuoted();
            var start = _position;
            var token = ReadToken();
            SkipWhitespace();
            if (token.Length > 0 && char.IsLetter(token[0]) && !AtEnd && text[_position] is '[' or '(')
            {
                _position = start;
                return ReadNode(depth + 1);
            }

            return token.Length == 0 ? null : token;
        }

        private string ReadToken()
        {
            var start = _position;
            while (!AtEnd && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '.' or '-' or '+'))
                _position++;
            return text[start.._position];
        }

        private string? ReadQuoted()
        {
            var builder = new StringBuilder();
            _position++;
            while (!AtEnd)
            {
                var current = text[_position++];
                if (current != '"')
                {
                    builder.Append(current);
                    continue;
                }

                if (AtEnd || text[_position] != '"')
                    return builder.ToString();
                builder.Append('"');
                _position++;
            }

            return null;
        }
    }
}
