// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed class WfsLegacyEndpointsTests
{
    [UnitTheory]
    [InlineData("DuckDB")]
    [InlineData("MySQL")]
    [InlineData("MariaDB")]
    public void Wfs11_GetCapabilities_ReadOnlyProvider_AdvertisesQueryOnly(string provider)
    {
        // These are the exact capability contracts returned by the shipping
        // DuckDB and MySQL/MariaDB feature readers registered in the WFS runtime.
        var capabilities = provider == "DuckDB"
            ? FeatureProviderCapabilities.ReadOnlyAnalytical
            : FeatureProviderCapabilities.ReadOnlyMySql;
        var reader = Substitute.For<IFeatureReader, IFeatureDataProvider>();
        ((IFeatureDataProvider)reader).Capabilities.Returns(capabilities);
        var content = Wfs20Handler.BuildWfs11Capabilities(
            "https://owned.example/wfs", Array.Empty<FeatureType>(), reader);
        XNamespace wfs = "http://www.opengis.net/wfs";
        var featureTypeList = XElement.Parse(content).Element(wfs + "FeatureTypeList")!;
        var operations = featureTypeList.Element(wfs + "Operations")!;
        operations.Elements(wfs + "Operation").Select(element => element.Value)
            .Should().Equal("Query");
    }

    [UnitTest]
    public void Wfs11_GetCapabilities_UnknownReader_DoesNotAssumeWriteSupport()
    {
        var reader = Substitute.For<IFeatureReader>();
        var content = Wfs20Handler.BuildWfs11Capabilities(
            "https://owned.example/wfs", Array.Empty<FeatureType>(), reader);
        XNamespace wfs = "http://www.opengis.net/wfs";
        XElement.Parse(content).Element(wfs + "FeatureTypeList")!
            .Element(wfs + "Operations")!.Elements(wfs + "Operation").Select(element => element.Value)
            .Should().Equal("Query");
    }

    [UnitTest]
    public void Wfs11_GetCapabilities_UpdateOnlyProvider_DoesNotAdvertiseInsertOrDelete()
    {
        var reader = Substitute.For<IFeatureReader, IFeatureDataProvider>();
        ((IFeatureDataProvider)reader).Capabilities.Returns(new FeatureProviderCapabilities
        {
            Edits = new FeatureProviderEditCapabilities { SupportsUpdate = true }
        });
        var content = Wfs20Handler.BuildWfs11Capabilities(
            "https://owned.example/wfs", Array.Empty<FeatureType>(), reader);
        XNamespace wfs = "http://www.opengis.net/wfs";
        XElement.Parse(content).Element(wfs + "FeatureTypeList")!
            .Element(wfs + "Operations")!.Elements(wfs + "Operation").Select(element => element.Value)
            .Should().Equal("Query", "Update");
    }

}
