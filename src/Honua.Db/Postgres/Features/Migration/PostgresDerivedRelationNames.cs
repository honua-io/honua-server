// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Db.Postgres.Features.Migration;

/// <summary>
/// Names for the relations an importer creates alongside a generated table (index, primary key,
/// sequence), derived the way PostgreSQL derives implicit names so they always fit its 63-byte
/// identifier limit and never collide with the table itself.
/// </summary>
internal static class PostgresDerivedRelationNames
{
    private const int MaxIdentifierLength = 63;

    /// <summary>
    /// The name PostgreSQL derives for a table's implicit relations (<c>makeObjectName</c> in
    /// <c>indexcmds.c</c>): the longer part is shortened one character at a time until
    /// <c>name1_name2_label</c> fits in 63 bytes. Callers pass ASCII identifiers (validated table names,
    /// fixed column names), so characters are bytes.
    /// </summary>
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

    /// <summary>
    /// A derived name that stays distinct for every source name. PostgreSQL's own derivation only
    /// shortens, so two table names that differ after the truncation point share one name; a caller
    /// that creates the relation with <c>IF NOT EXISTS</c> would then silently skip the second one.
    /// Names that fit keep the plain <c>name_label</c> form; longer ones carry a hash of the full
    /// name, the way the file-import path disambiguates long physical names.
    /// </summary>
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
