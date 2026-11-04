// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.FileImport.Domain;

// Writes the current member names and still reads the pre-rename name of GeoServicesJson.
public sealed class SupportedFileFormatJsonConverter : JsonConverter<SupportedFileFormat>
{
    private const string LegacyGeoServicesJsonName = "EsriJson";

    private static readonly JsonConverter<SupportedFileFormat> NameConverter =
        (JsonConverter<SupportedFileFormat>)new JsonStringEnumConverter<SupportedFileFormat>()
            .CreateConverter(typeof(SupportedFileFormat), JsonSerializerOptions.Default);

    public override SupportedFileFormat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && string.Equals(reader.GetString(), LegacyGeoServicesJsonName, StringComparison.OrdinalIgnoreCase))
        {
            return SupportedFileFormat.GeoServicesJson;
        }

        return NameConverter.Read(ref reader, typeToConvert, options);
    }

    public override void Write(Utf8JsonWriter writer, SupportedFileFormat value, JsonSerializerOptions options)
        => NameConverter.Write(writer, value, options);
}
