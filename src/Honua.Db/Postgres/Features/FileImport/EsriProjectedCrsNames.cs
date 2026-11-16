// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Reflection;

namespace Honua.Db.Postgres.Features.FileImport;

internal static class EsriProjectedCrsNames
{
    private const string ResourceName = "Honua.Db.Postgres.Features.FileImport.EsriProjectedCrsNames.txt";

    private static readonly FrozenDictionary<string, int> Names = Load();

    internal static int? TryGet(string name) => Names.TryGetValue(name, out var epsg) ? epsg : null;

    private static FrozenDictionary<string, int> Load()
    {
        using var stream = typeof(EsriProjectedCrsNames).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("ESRI projected CRS name table is missing.");

        using var reader = new StreamReader(stream);
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var tab = line.IndexOf('\t');
            if (tab <= 0 || !int.TryParse(line.AsSpan(tab + 1), out var code))
            {
                throw new InvalidOperationException("ESRI projected CRS name table has an unreadable row.");
            }

            names.Add(line[..tab], code);
        }

        return names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
