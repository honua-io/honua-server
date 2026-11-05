// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FileImport.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FileImport;

public sealed class SupportedFileFormatJsonTests
{
    [UnitTheory]
    [InlineData(SupportedFileFormat.GeoServicesJson, "\"GeoServicesJson\"")]
    [InlineData(SupportedFileFormat.GeoParquet, "\"GeoParquet\"")]
    public void Serialize_WritesTheMemberName(SupportedFileFormat format, string json)
        => JsonSerializer.Serialize(format).Should().Be(json);

    [UnitTheory]
    [InlineData("\"GeoServicesJson\"", SupportedFileFormat.GeoServicesJson)]
    [InlineData("\"GeoParquet\"", SupportedFileFormat.GeoParquet)]
    public void Deserialize_ReadsTheMemberName(string json, SupportedFileFormat format)
        => JsonSerializer.Deserialize<SupportedFileFormat>(json).Should().Be(format);
}
