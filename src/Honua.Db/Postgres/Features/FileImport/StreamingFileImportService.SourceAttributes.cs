// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Features;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static string? PreserveCsvSourceId(IFeature feature, ImportTypedColumnInference inference)
    {
        var names = feature.Attributes.GetNames();
        var sourceName = names.FirstOrDefault(name => name.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (sourceName is null)
        {
            return null;
        }

        var occupied = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var alias = "source_id";
        for (var suffix = 2; occupied.Contains(alias); suffix++)
        {
            alias = "source_id_" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        var value = feature.Attributes[sourceName];
        feature.Attributes.Add(alias, value);
        // Reserve the identity alias before the optional attribute promotion cap.
        inference.Observe(alias, value);
        return alias;
    }
}
