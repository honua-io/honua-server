// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Plugins.Abstractions;

namespace Honua.Sample.UtilityValidationPlugin;

[Plugin("feature-lines-format", "1.0.0",
    Description = "Newline-delimited JSON feature output format.",
    Capabilities = PluginCapability.OutputFormats)]
public sealed class FeatureLinesOutputFormatPlugin : IFeatureOutputFormat
{
    private static ReadOnlySpan<byte> Newline => "\n"u8;

    public string FormatId => "featurelines";

    public string MediaType => "application/x-ndjson";

    public string FileExtension => "ndjson";

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
