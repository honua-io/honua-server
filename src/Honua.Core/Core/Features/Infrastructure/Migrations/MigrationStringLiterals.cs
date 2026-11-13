// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Features.Infrastructure.Migrations;

internal static class MigrationStringLiterals
{
    private enum LiteralKind { Standard, Escaped }

    internal static (string Contents, int NextIndex) ReadStandard(string sql, int index) => Read(sql, index, LiteralKind.Standard);

    internal static (string Contents, int NextIndex) ReadEscaped(string sql, int index) => Read(sql, index, LiteralKind.Escaped);

    private static (string Contents, int NextIndex) Read(string sql, int index, LiteralKind kind)
    {
        var contents = new StringBuilder();
        index++;
        while (index < sql.Length)
        {
            if (kind == LiteralKind.Escaped && sql[index] == '\\' && index + 1 < sql.Length)
            {
                index++;
                var escape = ReadEscape(sql, index);
                contents.Append(escape.Contents);
                index = escape.NextIndex;
                continue;
            }

            if (sql[index] != '\'')
            {
                contents.Append(sql[index++]);
                continue;
            }

            index++;
            if (index < sql.Length && sql[index] == '\'')
            {
                contents.Append('\'');
                index++;
                continue;
            }

            // PostgreSQL joins literals separated by whitespace containing a newline. The
            // first literal's E mode applies to every continuation, including after comments.
            var continuation = FindContinuation(sql, index);
            if (continuation < 0)
            {
                break;
            }

            index = continuation + 1;
        }

        return (contents.ToString(), index);
    }

    private static (string Contents, int NextIndex) ReadEscape(string sql, int index)
    {
        var escaped = sql[index++];
        var radix = escaped is >= '0' and <= '7' ? 8 : 16;
        var maximum = escaped switch { >= '0' and <= '7' => 3, 'x' => 2, 'u' => 4, 'U' => 8, _ => 0 };
        if (maximum == 0)
        {
            var character = escaped switch
            {
                'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped
            };
            return (character.ToString(), index);
        }

        var start = radix == 8 ? index - 1 : index;
        var number = ReadNumber(sql, start, (radix, maximum));
        return number.NextIndex == start
            ? (escaped.ToString(), index)
            : (DecodeNumber(number.Value, escaped), number.NextIndex);
    }

    private static (long Value, int NextIndex) ReadNumber(string sql, int index, (int Radix, int Maximum) format)
    {
        long value = 0;
        var end = Math.Min(sql.Length, index + format.Maximum);
        while (index < end)
        {
            var digit = sql[index] switch
            {
                >= '0' and <= '9' => sql[index] - '0',
                >= 'a' and <= 'f' => sql[index] - 'a' + 10,
                >= 'A' and <= 'F' => sql[index] - 'A' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= format.Radix)
            {
                break;
            }

            value = value * format.Radix + digit;
            index++;
        }

        return (value, index);
    }

    private static string DecodeNumber(long value, char escaped)
    {
        if (escaped is >= '0' and <= '7' or 'x')
        {
            return ((char)(value & 255)).ToString();
        }

        if (value <= char.MaxValue)
        {
            return ((char)value).ToString();
        }

        return value <= 0x10ffff ? char.ConvertFromUtf32((int)value) : string.Empty;
    }

    private static int FindContinuation(string sql, int index)
    {
        var newline = false;
        while (index < sql.Length)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                newline |= sql[index] is '\n' or '\r';
                index++;
            }
            else
            {
                var commentEnd = MigrationSqlComments.FindEnd(sql, index);
                if (commentEnd == index)
                {
                    return newline && sql[index] == '\'' ? index : -1;
                }

                newline |= sql.AsSpan(index, commentEnd - index).IndexOfAny('\r', '\n') >= 0;
                index = commentEnd;
            }
        }

        return -1;
    }
}
