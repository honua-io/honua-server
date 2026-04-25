// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;

namespace Honua.Core.Features.Import.Services.FileGdb;

/// <summary>
/// Detects advanced constructs in a File Geodatabase by inspecting the GDB_Items
/// system table (a00000004.gdbtable). Produces warnings for features that may not
/// be fully preserved during import, such as domains, subtypes, and relationships.
/// </summary>
internal static class FileGdbAdvancedConstructs
{
    // GDB_Items XML definition type keywords that indicate advanced constructs.
    private static readonly string[] DomainTypes =
    [
        "DECodedValueDomain",
        "DERangeDomain",
    ];

    private static readonly string[] RelationshipTypes =
    [
        "DERelationshipClass",
    ];

    private static readonly string[] SubtypeKeywords =
    [
        "<SubtypeFieldName>",
        "<Subtype ",
    ];

    private static readonly string[] TopologyTypes =
    [
        "DETopology",
    ];

    private static readonly string[] NetworkTypes =
    [
        "DEGeometricNetwork",
        "DENetworkDataset",
    ];

    /// <summary>
    /// Scans the GDB_Items table to detect advanced constructs and returns warnings.
    /// </summary>
    /// <param name="gdbPath">Path to the .gdb directory.</param>
    /// <returns>Array of warning messages for detected advanced constructs.</returns>
    internal static string[] DetectWarnings(string gdbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gdbPath);

        if (!Directory.Exists(gdbPath))
        {
            return [];
        }

        // GDB_Items is typically stored as a00000004.gdbtable.
        var gdbItemsPath = Path.Combine(gdbPath, "a00000004.gdbtable");
        if (!File.Exists(gdbItemsPath))
        {
            return [];
        }

        var warnings = new List<string>();
        GdbTableReader reader;

        try
        {
            reader = GdbTableReader.Open(gdbItemsPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return [];
        }

        try
        {
            using (reader)
            {
                // Find the "Definition" (XML) field index.
                var defFieldIndex = -1;
                for (var i = 0; i < reader.Fields.Count; i++)
                {
                    if (string.Equals(reader.Fields[i].Name, "Definition", StringComparison.OrdinalIgnoreCase))
                    {
                        defFieldIndex = i;
                        break;
                    }
                }

                if (defFieldIndex < 0)
                {
                    return [];
                }

                var defFieldName = reader.Fields[defFieldIndex].Name;
                var hasDomains = false;
                var hasRelationships = false;
                var hasSubtypes = false;
                var hasTopology = false;
                var hasNetworks = false;

                foreach (var row in reader.ReadRows())
                {
                    if (!row.TryGetValue(defFieldName, out var defObj))
                    {
                        continue;
                    }

                    var xml = defObj as string;
                    if (string.IsNullOrEmpty(xml))
                    {
                        continue;
                    }

                    if (!hasDomains)
                    {
                        hasDomains = ContainsAny(xml, DomainTypes);
                    }

                    if (!hasRelationships)
                    {
                        hasRelationships = ContainsAny(xml, RelationshipTypes);
                    }

                    if (!hasSubtypes)
                    {
                        hasSubtypes = ContainsAny(xml, SubtypeKeywords);
                    }

                    if (!hasTopology)
                    {
                        hasTopology = ContainsAny(xml, TopologyTypes);
                    }

                    if (!hasNetworks)
                    {
                        hasNetworks = ContainsAny(xml, NetworkTypes);
                    }
                }

                if (hasDomains)
                {
                    warnings.Add("Geodatabase contains coded value or range domains. Domain constraints will not be imported.");
                }

                if (hasRelationships)
                {
                    warnings.Add("Geodatabase contains relationship classes. Relationships will not be imported.");
                }

                if (hasSubtypes)
                {
                    warnings.Add("Geodatabase contains subtypes. Subtype definitions will not be imported.");
                }

                if (hasTopology)
                {
                    warnings.Add("Geodatabase contains topology rules. Topology will not be imported.");
                }

                if (hasNetworks)
                {
                    warnings.Add("Geodatabase contains network datasets. Network topology will not be imported.");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            return warnings.ToArray();
        }

        return warnings.ToArray();
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        for (var i = 0; i < keywords.Length; i++)
        {
            if (text.Contains(keywords[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
