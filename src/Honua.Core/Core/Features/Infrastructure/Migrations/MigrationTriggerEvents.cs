// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Migrations;

internal static partial class MigrationTriggerEvents
{
    private const string Identifier = "(?:[a-z_][a-z0-9_$]*|\"(?:[^\"]|\"\")+\")";
    private const string Event = "(?:INSERT|DELETE|TRUNCATE|UPDATE(?:\\s+OF\\s+" + Identifier +
        "(?:\\s*,\\s*" + Identifier + ")*)?)";

    // Only the trigger header is inert for the TRUNCATE rule. Statements following ON,
    // including executable routine bodies, still pass through the destructive-SQL scan.
    internal static string ExcludeDeclarations(string executableSql)
    {
        var result = new StringBuilder(executableSql.Length);
        var quotedIdentifier = false;
        for (var index = 0; index < executableSql.Length; index++)
        {
            var character = executableSql[index];
            if (character == '"')
            {
                result.Append(character);
                if (quotedIdentifier && index + 1 < executableSql.Length && executableSql[index + 1] == '"')
                {
                    result.Append(executableSql[++index]);
                    continue;
                }

                quotedIdentifier = !quotedIdentifier;
                continue;
            }

            if (!quotedIdentifier && character is 'C' or 'c')
            {
                var header = TriggerHeader().Match(executableSql, index);
                if (header.Success)
                {
                    result.Append(' ');
                    index += header.Length - 1;
                    continue;
                }
            }

            result.Append(character);
        }

        return result.ToString();
    }

    [GeneratedRegex(@"\G(?<![a-z0-9_$])CREATE\s+(?:OR\s+REPLACE\s+)?(?:CONSTRAINT\s+)?TRIGGER\s+" + Identifier +
        @"\s+(?:BEFORE|AFTER|INSTEAD\s+OF)\s+" + Event + @"(?:\s+OR\s+" + Event + @")*\s+ON\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TriggerHeader();
}
