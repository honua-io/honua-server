// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Features.Infrastructure.Migrations;

internal static class MigrationSqlComments
{
    // The caller decides whether quotes establish a known context. Uncertain dynamic
    // fragments deliberately use this operation at every character, including inside quotes.
    internal static string NormalizeFragment(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var index = 0;
        while (index < sql.Length)
        {
            var commentEnd = FindEnd(sql, index);
            if (commentEnd > index)
            {
                index = commentEnd;
                result.Append(' ');
            }
            else
            {
                result.Append(sql[index++]);
            }
        }

        return result.ToString();
    }

    internal static int FindEnd(string sql, int index)
    {
        if (index + 1 >= sql.Length)
        {
            return index;
        }

        if (sql[index] == '-' && sql[index + 1] == '-')
        {
            index += 2;
            while (index < sql.Length && sql[index] is not ('\n' or '\r'))
            {
                index++;
            }

            return index;
        }

        if (sql[index] != '/' || sql[index + 1] != '*')
        {
            return index;
        }

        index += 2;
        var depth = 1;
        while (index < sql.Length && depth > 0)
        {
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

        return index;
    }
}
