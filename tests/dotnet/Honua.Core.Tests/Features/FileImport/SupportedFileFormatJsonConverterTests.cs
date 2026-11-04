// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FileImport.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FileImport;

public sealed class SupportedFileFormatJsonConverterTests
{
    [UnitTheory]
    [InlineData("\"EsriJson\"")]
    [InlineData("\"esrijson\"")]
    [InlineData("\"GeoServicesJson\"")]
    public void Deserialize_LegacyAndCurrentNames_ReadGeoServicesJson(string json)
        => JsonSerializer.Deserialize<SupportedFileFormat>(json).Should().Be(SupportedFileFormat.GeoServicesJson);

    [UnitTest]
    public void Serialize_GeoServicesJson_WritesCurrentName()
        => JsonSerializer.Serialize(SupportedFileFormat.GeoServicesJson).Should().Be("\"GeoServicesJson\"");

    [UnitTest]
    public void Deserialize_OtherFormats_KeepTheirNames()
        => JsonSerializer.Deserialize<SupportedFileFormat>("\"GeoParquet\"").Should().Be(SupportedFileFormat.GeoParquet);
}
