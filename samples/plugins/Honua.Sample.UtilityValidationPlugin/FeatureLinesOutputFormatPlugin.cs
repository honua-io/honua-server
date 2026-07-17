// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Plugins.Abstractions;

namespace Honua.Sample.UtilityValidationPlugin;

/// <summary>
/// Reference plugin (issue #2856, ADR-0066) demonstrating the <see cref="IFeatureOutputFormat"/>
/// extension point. Contributes a newline-delimited JSON feature format the host serves under the
/// wire token <c>featurelines</c> — a third-party output format added entirely out-of-tree, with no
/// core change. It declares the <see cref="PluginCapability.OutputFormats"/> capability required to
/// contribute a format.
/// </summary>
/// <remarks>
/// The writer is AOT-safe: it uses the low-level, reflection-free <see cref="Utf8JsonWriter"/> and a
/// small type switch over attribute values, and encodes geometry as WKB hex so the sample takes no
/// geometry-library dependency. It references only the lean <c>Honua.Plugins.Abstractions</c>
/// contract package (the canonical <see cref="Feature"/> flows in transitively).
/// </remarks>
[Plugin("feature-lines-format", "1.0.0",
    Description = "Newline-delimited JSON feature output format.",
    Capabilities = PluginCapability.OutputFormats)]
public sealed class FeatureLinesOutputFormatPlugin : IFeatureOutputFormat
{
    private static ReadOnlySpan<byte> Newline => "\n"u8;

    /// <inheritdoc />
    public string FormatId => "featurelines";

    /// <inheritdoc />
    public string MediaType => "application/x-ndjson";

    /// <inheritdoc />
    public string FileExtension => "ndjson";

    /// <inheritdoc />
    public async ValueTask<long> WriteAsync(
        IAsyncEnumerable<Feature> features,
        FeatureOutputFormatContext context,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        var count = 0L;
        await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            WriteFeatureLine(output, feature, context);
            output.Write(Newline);
            count++;

            if (count % 64 == 0)
            {
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private static void WriteFeatureLine(Stream output, Feature feature, FeatureOutputFormatContext context)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteNumber("id", feature.Id);

        writer.WriteStartObject("properties");
        foreach (var field in context.Fields)
        {
            feature.Attributes.TryGetValue(field.Name, out var value);
            WriteValue(writer, field.Name, value);
        }

        writer.WriteEndObject();

        if (feature.Geometry is { Length: > 0 } wkb)
        {
            writer.WriteString("geometryWkbHex", Convert.ToHexString(wkb));
        }
        else
        {
            writer.WriteNull("geometryWkbHex");
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNull(name);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            case bool b:
                writer.WriteBoolean(name, b);
                break;
            case int i:
                writer.WriteNumber(name, i);
                break;
            case long l:
                writer.WriteNumber(name, l);
                break;
            case double d:
                writer.WriteNumber(name, d);
                break;
            case float f:
                writer.WriteNumber(name, f);
                break;
            case decimal m:
                writer.WriteNumber(name, m);
                break;
            default:
                writer.WriteString(name, value.ToString());
                break;
        }
    }
}
