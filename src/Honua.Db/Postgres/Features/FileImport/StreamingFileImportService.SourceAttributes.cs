// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Features;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
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
        inference.ObserveText(alias);
        return alias;
    }

    private static string BuildCsvSourceIdWarning(string alias, IReadOnlyList<ImportTypedColumn> columns)
        => columns.Any(column => column.Name.Equals(alias, StringComparison.OrdinalIgnoreCase))
            ? $"The CSV 'id' column is available as '{alias}'; 'id' is the generated feature key."
            : $"CSV 'id' values were preserved in properties as '{alias}', but the column could not be promoted; 'id' is the generated feature key.";
}
