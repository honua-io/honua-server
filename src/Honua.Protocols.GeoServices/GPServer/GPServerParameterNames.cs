// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Projects canonical inputs to identifiers accepted by Esri's Python toolbox importer.
/// Canonical names remain valid on incoming requests and in execution plans.
/// </summary>
internal static class GPServerParameterNames
{
    // Python keywords plus the arguments appended by ArcGIS API for Python.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class",
        "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global",
        "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
        "return", "try", "while", "with", "yield", "gis", "future", "estimate"
    };

    internal static string GetEncodingPrefix(ProcessDefinition definition)
    {
        var prefix = new StringBuilder("HonuaParameter_");
        while (definition.Parameters.Any(parameter => parameter.Name.StartsWith(prefix.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            prefix.Append('_');
        }
        return prefix.ToString();
    }

    internal static string Publish(string name, string prefix)
        => IsSafe(name) ? name : prefix + Convert.ToHexString(Encoding.UTF8.GetBytes(name));

    internal static string Resolve(string name, ProcessDefinition definition, string prefix)
    {
        // Preserve the established case-insensitive canonical input lookup first.
        var canonical = definition.Parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (canonical is not null)
        {
            return canonical.Name;
        }
        return definition.Parameters.FirstOrDefault(parameter =>
            Publish(parameter.Name, prefix).Equals(name, StringComparison.OrdinalIgnoreCase))?.Name ?? name;
    }

    private static bool IsSafe(string name)
        => name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
           name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') &&
           !ReservedNames.Contains(name);
}
