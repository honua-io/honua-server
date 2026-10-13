// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
}
