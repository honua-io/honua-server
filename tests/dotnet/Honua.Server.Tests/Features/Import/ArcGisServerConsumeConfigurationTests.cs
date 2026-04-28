// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Features.CrossServerConsume;

namespace Honua.Server.Tests.Features.Import;

public sealed class ArcGisServerConsumeConfigurationTests
{
    [Fact]
    public void From_WithoutCrossServerGate_IsNotLicensed()
    {
        var values = new Dictionary<string, string?>
        {
            [ArcGisServerConsumeConfiguration.LicensedConsumeEnv] = "1",
            [ArcGisServerConsumeConfiguration.WmsUrlEnv] = "https://gis.example.test/arcgis/services/Honua/MapServer/WMSServer",
            [ArcGisServerConsumeConfiguration.WmsLayerEnv] = "0",
            [ArcGisServerConsumeConfiguration.WmsBboxEnv] = "18.8,-161,22.5,-154.5",
        };

        var configuration = ArcGisServerConsumeConfiguration.From(values.GetValueOrDefault);

        configuration.IsLicensed.Should().BeFalse();
        configuration.HasWms.Should().BeFalse();
    }

    [Fact]
    public void From_WithLicensedWmsValues_EnablesWmsOnly()
    {
        var values = new Dictionary<string, string?>
        {
            [CrossServerConsumeTestSupport.ExternalServicesEnv] = "1",
            [ArcGisServerConsumeConfiguration.LicensedConsumeEnv] = "1",
            [ArcGisServerConsumeConfiguration.WmsUrlEnv] = "https://gis.example.test/arcgis/services/Honua/MapServer/WMSServer",
            [ArcGisServerConsumeConfiguration.WmsLayerEnv] = "0",
            [ArcGisServerConsumeConfiguration.WmsBboxEnv] = "18.8,-161,22.5,-154.5",
        };

        var configuration = ArcGisServerConsumeConfiguration.From(values.GetValueOrDefault);

        configuration.HasWms.Should().BeTrue();
        configuration.HasWfs.Should().BeFalse();
        configuration.HasWmts.Should().BeFalse();
        configuration.HasMapServerTile.Should().BeFalse();
        configuration.WmsCrs.Should().Be("EPSG:4326");
        configuration.WmsFormat.Should().Be("image/png");
    }

    [Fact]
    public void From_WithMapServerTileOnly_EnablesTileWithoutWmts()
    {
        var values = new Dictionary<string, string?>
        {
            [CrossServerConsumeTestSupport.ExternalServicesEnv] = "1",
            [ArcGisServerConsumeConfiguration.LicensedConsumeEnv] = "1",
            [ArcGisServerConsumeConfiguration.MapServerTileUrlEnv] = "https://gis.example.test/arcgis/rest/services/Honua/MapServer/tile/0/0/0",
        };

        var configuration = ArcGisServerConsumeConfiguration.From(values.GetValueOrDefault);

        configuration.HasMapServerTile.Should().BeTrue();
        configuration.HasWmts.Should().BeFalse();
    }

    [Fact]
    public void From_WithCustomWmsCrsAndFormat_UsesConfiguredValues()
    {
        var values = new Dictionary<string, string?>
        {
            [CrossServerConsumeTestSupport.ExternalServicesEnv] = "1",
            [ArcGisServerConsumeConfiguration.LicensedConsumeEnv] = "1",
            [ArcGisServerConsumeConfiguration.WmsCrsEnv] = "EPSG:3857",
            [ArcGisServerConsumeConfiguration.WmsFormatEnv] = "image/jpeg",
        };

        var configuration = ArcGisServerConsumeConfiguration.From(values.GetValueOrDefault);

        configuration.WmsCrs.Should().Be("EPSG:3857");
        configuration.WmsFormat.Should().Be("image/jpeg");
    }
}
