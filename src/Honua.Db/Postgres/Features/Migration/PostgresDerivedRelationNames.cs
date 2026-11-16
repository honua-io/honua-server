// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Db.Postgres.Features.Migration;

internal static class PostgresDerivedRelationNames
{
    private const int MaxIdentifierLength = 63;

    internal static string Build(string name1, string? name2, string label)
    {
        var available = MaxIdentifierLength - (label.Length + 1) - (name2 is null ? 0 : 1);
        var name1Chars = name1.Length;
        var name2Chars = name2?.Length ?? 0;
        while (name1Chars + name2Chars > available)
        {
            if (name1Chars > name2Chars)
            {
                name1Chars--;
            }
            else
            {
                name2Chars--;
            }
        }

        return name2 is null
            ? $"{name1[..name1Chars]}_{label}"
            : $"{name1[..name1Chars]}_{name2[..name2Chars]}_{label}";
    }

    internal static string BuildUnique(string name, string label)
    {
        const int hashLength = 8;
        var suffix = "_" + label;
        if (name.Length + suffix.Length <= MaxIdentifierLength)
        {
            return name + suffix;
        }

        var keep = MaxIdentifierLength - suffix.Length - hashLength - 1;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..hashLength]
            .ToLowerInvariant();
        return $"{name[..keep]}_{hash}{suffix}";
    }
}
