// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed record ProjectedWktNode(string Kind, IReadOnlyList<object> Values)
{
    internal IEnumerable<ProjectedWktNode> Children(string kind) =>
        Values.OfType<ProjectedWktNode>().Where(node => node.Kind == kind);

    internal ProjectedWktNode One(string kind) => Children(kind).Single();
    internal string Text(int index) => Values[index] as string ?? throw new FormatException();
    internal double Number(int index) => Values[index] is double number ? number : throw new FormatException();

    internal void Validate(int scalarCount, params string[] childKinds)
    {
        if (Values.Count < scalarCount || Values.Take(scalarCount).Any(value => value is ProjectedWktNode)
            || Values.Skip(scalarCount).Any(value => value is not ProjectedWktNode node || !childKinds.Contains(node.Kind)))
            throw new FormatException();
    }

    internal static ProjectedWktNode Parse(string text)
    {
        if (text.Length > 65536) throw new FormatException();
        var reader = new Reader(text);
        var node = reader.ReadNode(0);
        reader.SkipWhitespace();
        if (reader.Position != text.Length) throw new FormatException();
        return node;
    }

    private sealed class Reader(string text)
    {
        internal int Position { get; private set; }

        internal void SkipWhitespace()
        {
            while (Position < text.Length && char.IsWhiteSpace(text[Position])) Position++;
        }

        private string ReadToken()
        {
            SkipWhitespace();
            var start = Position;
            while (Position < text.Length && !char.IsWhiteSpace(text[Position]) && text[Position] is not '[' and not ']' and not ',')
                Position++;
            if (Position == start) throw new FormatException();
            return text[start..Position];
        }

        private string ReadQuoted()
        {
            Position++;
            var result = new StringBuilder();
            while (Position < text.Length)
            {
                var character = text[Position++];
                if (character != '"') { result.Append(character); continue; }
                if (Position < text.Length && text[Position] == '"') { Position++; result.Append('"'); continue; }
                return result.ToString();
            }
            throw new FormatException();
        }

        internal ProjectedWktNode ReadNode(int depth)
        {
            if (depth > 16) throw new FormatException();
            var kind = ReadToken().ToUpperInvariant();
            SkipWhitespace();
            if (Position >= text.Length || text[Position++] != '[') throw new FormatException();
            var values = new List<object>();
            while (true)
            {
                values.Add(ReadValue(depth));
                SkipWhitespace();
                if (Position >= text.Length) throw new FormatException();
                var delimiter = text[Position++];
                if (delimiter == ']') return new ProjectedWktNode(kind, values);
                if (delimiter != ',') throw new FormatException();
            }
        }

        private object ReadValue(int depth)
        {
            SkipWhitespace();
            if (Position >= text.Length) throw new FormatException();
            if (text[Position] == '"') return ReadQuoted();
            var start = Position;
            var token = ReadToken();
            SkipWhitespace();
            if (Position < text.Length && text[Position] == '[')
            {
                Position = start;
                return ReadNode(depth + 1);
            }
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                return number;
            if (token.All(char.IsLetter)) return token;
            throw new FormatException();
        }
    }
}
