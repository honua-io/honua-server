// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Import.Domain;

/// <summary>Canonical physical naming shared by import publication and destination authorization.</summary>
public static class ImportPhysicalTableName
{
    /// <summary>Returns the bounded physical table name for an already validated logical identifier.</summary>
    /// <param name="logicalName">Validated logical import table identifier.</param>
    /// <returns>Physical PostgreSQL identifier with room for staging and index suffixes.</returns>
    public static string Canonical(string logicalName)
    {
        var physical = Legacy(logicalName);
        if (physical.Length <= 40) return physical;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physical)))[..12];
        return $"{physical[..27]}_{hash}";
    }

    /// <summary>Returns the historical physical name; names over 63 characters are never valid legacy targets.</summary>
    /// <param name="logicalName">Validated logical import table identifier.</param>
    /// <returns>Historical import table identifier.</returns>
    public static string Legacy(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        if (logicalName.Length > 63 || !char.IsAsciiLetter(logicalName[0])
            || logicalName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("A validated logical import identifier is required.", nameof(logicalName));
        return "imported_" + logicalName.ToLowerInvariant();
    }
}
