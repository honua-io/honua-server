// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Features.Infrastructure.Migrations;

internal static class MigrationStringLiterals
{
    private enum LiteralKind { Standard, Escaped }

    internal static string ReadStandard(string sql, ref int index) => Read(sql, ref index, LiteralKind.Standard);

    internal static string ReadEscaped(string sql, ref int index) => Read(sql, ref index, LiteralKind.Escaped);

    private static string Read(string sql, ref int index, LiteralKind kind)
    {
        var contents = new StringBuilder();
        index++;
        while (index < sql.Length)
        {
            if (kind == LiteralKind.Escaped && sql[index] == '\\' && index + 1 < sql.Length)
            {
                index++;
                AppendEscape(sql, ref index, contents);
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

        return contents.ToString();
    }

    private static void AppendEscape(string sql, ref int index, StringBuilder contents)
    {
        var escaped = sql[index++];
        var radix = escaped is >= '0' and <= '7' ? 8 : 16;
        var maximum = escaped switch { >= '0' and <= '7' => 3, 'x' => 2, 'u' => 4, 'U' => 8, _ => 0 };
        if (maximum == 0)
        {
            contents.Append(escaped switch
            {
                'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped
            });
            return;
        }

        if (radix == 8)
        {
            index--;
        }

        long value = 0;
        var digits = 0;
        while (index < sql.Length && digits < maximum)
        {
            var digit = sql[index] switch
            {
                >= '0' and <= '9' => sql[index] - '0',
                >= 'a' and <= 'f' => sql[index] - 'a' + 10,
                >= 'A' and <= 'F' => sql[index] - 'A' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= radix)
            {
                break;
            }

            value = value * radix + digit;
            digits++;
            index++;
        }

        if (digits == 0)
        {
            contents.Append(escaped);
        }
        else if (escaped is >= '0' and <= '7' or 'x')
        {
            contents.Append((char)(value & 255));
        }
        else if (value <= char.MaxValue)
        {
            contents.Append((char)value);
        }
        else if (value <= 0x10ffff)
        {
            contents.Append(char.ConvertFromUtf32((int)value));
        }
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
            else if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\n' or '\r'))
                {
                    index++;
                }
            }
            else if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                index += 2;
                var depth = 1;
                while (index < sql.Length && depth > 0)
                {
                    newline |= sql[index] is '\n' or '\r';
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }
            }
            else
            {
                return newline && sql[index] == '\'' ? index : -1;
            }
        }

        return -1;
    }
}
