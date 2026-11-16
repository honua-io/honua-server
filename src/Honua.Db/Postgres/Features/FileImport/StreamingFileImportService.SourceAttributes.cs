// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Features;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static void ObserveHiddenSourceProperties(
        IFeature feature, IDictionary<string, string> warnings, SupportedFileFormat format)
    {
        foreach (var name in feature.Attributes.GetNames())
        {
            var isId = name.Equals("id", StringComparison.OrdinalIgnoreCase);
            if ((!isId || format == SupportedFileFormat.Csv)
                && !name.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var column = isId ? "id" : "geometry";
            warnings.TryAdd(column,
                $"Source property '{name}' conflicts with the importer-owned '{column}' column. "
                + "Its original value is retained in the stored properties document but is hidden in published features. "
                + $"Rename the source property to an unused name such as 'source_{column}' and re-import to publish its values.");
        }
    }

    private static string? PreserveCsvSourceId(IFeature feature, ImportTypedColumnInference inference, IReadOnlyList<string> headers)
    {
        var sourceName = headers.FirstOrDefault(name => name.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (sourceName is null)
        {
            return null;
        }

        var occupied = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        var alias = "source_id";
        for (var suffix = 2; occupied.Contains(alias); suffix++)
        {
            alias = "source_id_" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        var value = feature.Attributes.Exists(sourceName) ? feature.Attributes[sourceName] : null;
        feature.Attributes.Add(alias, value);
        // Reserve the identity alias before the optional attribute promotion cap.
        inference.Observe(alias, value);
        return alias;
    }
}
