// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Io.Export.Writers;
using Honua.Plugins.Abstractions;

namespace Honua.Io.Export;

internal sealed class CsvOutputFormat : IFeatureOutputFormat
{
    public static CsvOutputFormat Instance { get; } = new();

    public string FormatId => "csv";

    public string MediaType => "text/csv";

    public string FileExtension => "csv";

    public async ValueTask<long> WriteAsync(
        IAsyncEnumerable<Feature> features,
        FeatureOutputFormatContext context,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // CsvExportWriter only reads ExportField.Name (it looks values up by name from the feature's
        // attribute bag), so the field type/nullability carried on ExportField is immaterial here.
        var fields = new ExportField[context.Fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = new ExportField(context.Fields[i].Name, ExportFieldType.Unknown, context.Fields[i].Nullable);
        }

        var written = await CsvExportWriter.WriteAsync(output, features, fields, cancellationToken).ConfigureAwait(false);
        return written;
    }
}
