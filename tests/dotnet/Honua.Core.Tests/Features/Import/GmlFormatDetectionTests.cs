// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class GmlFormatDetectionTests
{
    private readonly FileFormatDetectionService _service =
        new(NullLogger<FileFormatDetectionService>.Instance);

    // -----------------------------------------------------------------------
    // Extension-based detection
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("rivers.gml")]
    [InlineData("RIVERS.GML")]
    [InlineData("parcels.GmL")]
    public void DetectFormat_GmlExtension_ReturnsGml(string fileName)
    {
        _service.DetectFormat(fileName).Should().Be(SupportedFileFormat.Gml);
    }

    [Fact]
    public void GetSupportedExtensions_IncludesGml()
    {
        _service.GetSupportedExtensions().Should().Contain(".gml");
    }

    // -----------------------------------------------------------------------
    // Content-based detection
    // -----------------------------------------------------------------------

    [Fact]
    public void DetectFormatFromContent_GmlFeatureCollection_ReturnsGml()
    {
        var content = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs"
              xmlns:gml="http://www.opengis.net/gml">
            </wfs:FeatureCollection>
            """);

        _service.DetectFormatFromContent(content, "unknown.xml").Should().Be(SupportedFileFormat.Gml);
    }

    [Fact]
    public void DetectFormatFromContent_Gml32Namespace_ReturnsGml()
    {
        var content = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
              xmlns:wfs="http://www.opengis.net/wfs/2.0"
              xmlns:gml="http://www.opengis.net/gml/3.2">
            </wfs:FeatureCollection>
            """);

        _service.DetectFormatFromContent(content, "unknown.xml").Should().Be(SupportedFileFormat.Gml);
    }
}
