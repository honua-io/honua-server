// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

public sealed class ImageServerSoapExportRequestTests
{
    [UnitTest]
    public void CopyWithResponseFormat_KeepsTheRequestedNoDataInterpretation()
    {
        var operation = XElement.Parse("""
            <ExportImage>
              <ImageDescription>
                <Extent><XMin>-120</XMin><YMin>30</YMin><XMax>-119</XMax><YMax>31</YMax><SpatialReference><WKID>4326</WKID></SpatialReference></Extent>
                <Width>4</Width><Height>4</Height>
                <NoDataInterpretation>esriNoDataMatchAll</NoDataInterpretation>
              </ImageDescription>
              <ImageType><ImageFormat>esriImagePNG</ImageFormat></ImageType>
            </ExportImage>
            """);
        ImageServerSoapEndpoints.TryCreateExportRequest(operation, out var parsed, out _, out _, out var error)
            .Should().BeTrue(error);

        var copied = ImageServerSoapEndpoints.CopyWithResponseFormat(parsed, "image");

        copied.NoDataInterpretation.Should().Be("esriNoDataMatchAll");
        copied.F.Should().Be("image");
    }
}
