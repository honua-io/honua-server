// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class GmlFormatDetectionTests
{
    private static FileFormatDetectionService CreateService() =>
        new(NullLogger<FileFormatDetectionService>.Instance);

    [Theory]
    [InlineData("states.gml")]
    [InlineData("EXPORT.GML")]
    public void DetectFormat_GmlExtension_ReturnsGml(string fileName)
    {
        CreateService().DetectFormat(fileName).Should().Be(SupportedFileFormat.Gml);
    }

    [Fact]
    public void GetSupportedExtensions_IncludesGml()
    {
        CreateService().GetSupportedExtensions().Should().Contain(".gml");
    }

    [Fact]
    public void DetectFormatFromContent_GmlNamespacedDocument_ReturnsGml()
    {
        var content = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs"
                                   xmlns:gml="http://www.opengis.net/gml">
              <gml:featureMember>
                <topp:state><topp:name>Illinois</topp:name></topp:state>
              </gml:featureMember>
            </wfs:FeatureCollection>
            """);

        CreateService().DetectFormatFromContent(content, "unknown.bin")
            .Should().Be(SupportedFileFormat.Gml);
    }

    [Fact]
    public void DetectFormatFromContent_KmlDocument_IsNotMisclassifiedAsGml()
    {
        var content = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Placemark><Point><coordinates>-122,37</coordinates></Point></Placemark>
            </kml>
            """);

        CreateService().DetectFormatFromContent(content, "unknown.bin")
            .Should().Be(SupportedFileFormat.Kml);
    }
}
