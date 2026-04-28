// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.CrossServerConsume;

/// <summary>
/// Verifies shared cross-server consume test helpers.
/// </summary>
public sealed class CrossServerConsumeTestSupportTests
{
    [Fact]
    public void AssertWmsLayerAdvertised_WithExactNumericLayerName_Passes()
    {
        var document = XDocument.Parse(
            """
            <WMS_Capabilities>
              <Capability>
                <Layer>
                  <Title>Root layer with scale 0 metadata</Title>
                  <Layer>
                    <Name>0</Name>
                    <Title>Configured ArcGIS layer</Title>
                  </Layer>
                </Layer>
              </Capability>
            </WMS_Capabilities>
            """);

        var act = () => CrossServerConsumeTestSupport.AssertWmsLayerAdvertised(document, "0");

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertWmsLayerAdvertised_WithNumericLayerNameOnlyInOtherText_Fails()
    {
        var document = XDocument.Parse(
            """
            <WMS_Capabilities>
              <Capability>
                <Layer>
                  <Title>Root layer with scale 0 metadata</Title>
                  <Layer>
                    <Name>1</Name>
                    <Title>Layer 0 summary text</Title>
                  </Layer>
                </Layer>
              </Capability>
            </WMS_Capabilities>
            """);

        var act = () => CrossServerConsumeTestSupport.AssertWmsLayerAdvertised(document, "0");

        act.Should().Throw<XunitException>();
    }
}
