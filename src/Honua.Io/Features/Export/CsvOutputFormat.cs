// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Io.Export.Writers;
using Honua.Plugins.Abstractions;

namespace Honua.Io.Export;

/// <summary>
/// Built-in CSV feature output format (issue #2856, ADR-0067). Ported to flow through the
/// <see cref="IFeatureOutputFormat"/> extension point so the export path exercises the same contract
/// a third-party plugin format implements — the proof the seam is load-bearing, not decorative.
/// Delegates the actual byte production to <see cref="CsvExportWriter"/> so behavior (UTF-8, WKT
/// geometry column, streaming) is byte-identical to the pre-extension export.
/// </summary>
internal sealed class CsvOutputFormat : IFeatureOutputFormat
{
    /// <summary>A shared, stateless singleton the export endpoint dispatches CSV through.</summary>
    public static CsvOutputFormat Instance { get; } = new();

    /// <inheritdoc />
    public string FormatId => "csv";

    /// <inheritdoc />
    public string MediaType => "text/csv";

    /// <inheritdoc />
    public string FileExtension => "csv";

    /// <inheritdoc />
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
