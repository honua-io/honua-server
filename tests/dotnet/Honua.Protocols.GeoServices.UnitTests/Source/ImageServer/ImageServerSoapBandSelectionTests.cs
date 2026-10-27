// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// SOAP ExportImage/GetImage band selection reads only the band-id element.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerSoapBandSelectionTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public void ReadImageDescriptionBandIds_LockRasterInts_AreNotBandIds()
    {
        var description = XElement.Parse(
            """
            <ImageDescription>
              <MosaicRule>
                <LockRasterIDs>
                  <Int>7</Int>
                  <Int>8</Int>
                </LockRasterIDs>
              </MosaicRule>
            </ImageDescription>
            """);

        ImageServerSoapEndpoints.ReadImageDescriptionBandIds(description).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void ReadImageDescriptionBandIds_BandIdsChild_IgnoresNestedMosaicInts()
    {
        var description = XElement.Parse(
            """
            <ImageDescription>
              <MosaicRule>
                <LockRasterIDs>
                  <Int>7</Int>
                  <Int>8</Int>
                </LockRasterIDs>
              </MosaicRule>
              <BandIDs>
                <Int>1</Int>
                <Int>3</Int>
              </BandIDs>
            </ImageDescription>
            """);

        ImageServerSoapEndpoints.ReadImageDescriptionBandIds(description).Should().Be("1,3");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryCreateExportRequest_ProLercExport_MapsFormatAndKeepsMaxZError()
    {
        // ArcGIS Pro 3.7 draw captured against a reference ImageServer: ExportImage asks for
        // esriImageLERC with CompressionQuality 10000 (LERC maxZError scale, not JPEG quality)
        // and esriMosaicNone. A hand-built REST PNG of the same bbox succeeds; this request
        // is what Pro sends when it actually paints (#6563).
        var operation = XElement.Parse(
            """
            <ExportImage xmlns="http://www.esri.com/schemas/ArcGIS/10.8">
              <ImageDescription>
                <Extent>
                  <XMin>-122.45</XMin>
                  <YMin>37.75</YMin>
                  <XMax>-122.40</XMax>
                  <YMax>37.80</YMax>
                  <SpatialReference><WKID>4326</WKID><LatestWKID>4326</LatestWKID></SpatialReference>
                </Extent>
                <Width>4</Width>
                <Height>4</Height>
                <PixelType>U8</PixelType>
                <Interpolation>RSP_NearestNeighbor</Interpolation>
                <Compression>LERC</Compression>
                <CompressionQuality>10000</CompressionQuality>
                <MosaicRule>
                  <MosaicMethod>esriMosaicNone</MosaicMethod>
                  <MosaicOperation>MT_FIRST</MosaicOperation>
                </MosaicRule>
                <NoDataInterpretation>esriNoDataMatchAny</NoDataInterpretation>
              </ImageDescription>
              <ImageType>
                <ImageFormat>esriImageLERC</ImageFormat>
                <ImageReturnType>esriImageReturnURL</ImageReturnType>
              </ImageType>
            </ExportImage>
            """);

        var parsed = ImageServerSoapEndpoints.TryCreateExportRequest(
            operation, out var request, out var width, out var height, out var error);

        parsed.Should().BeTrue(error);
        error.Should().BeNull();
        width.Should().Be(4);
        height.Should().Be(4);
        request.Format.Should().Be("lerc");
        request.Compression.Should().Be("LERC");
        request.CompressionQuality.Should().Be(10000);
        request.PixelType.Should().Be("U8");
        request.NoData.Should().BeNull();
        request.MosaicRule.Should().Contain("esriMosaicNone");
        ImageServerSoapEndpoints.IsLercDraw(request).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void IsLercDraw_GetImageWithoutImageType_StillRecognizesLercCompression()
    {
        var operation = XElement.Parse(
            """
            <GetImage xmlns="http://www.esri.com/schemas/ArcGIS/10.8">
              <ImageDescription>
                <Extent>
                  <XMin>-122.45</XMin><YMin>37.75</YMin><XMax>-122.40</XMax><YMax>37.80</YMax>
                  <SpatialReference><WKID>4326</WKID></SpatialReference>
                </Extent>
                <Width>8</Width>
                <Height>8</Height>
                <Compression>LERC</Compression>
                <CompressionQuality>10000</CompressionQuality>
              </ImageDescription>
            </GetImage>
            """);

        ImageServerSoapEndpoints.TryCreateExportRequest(
            operation, out var request, out _, out _, out var error, requireImageType: false)
            .Should().BeTrue(error);
        ImageServerSoapEndpoints.IsLercDraw(request).Should().BeTrue();
    }
}
