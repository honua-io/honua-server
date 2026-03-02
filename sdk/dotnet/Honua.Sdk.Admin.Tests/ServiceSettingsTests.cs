// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Sdk.Admin.Models;
using Honua.Sdk.Admin.Tests.Fixtures;

namespace Honua.Sdk.Admin.Tests;

public sealed class ServiceSettingsTests
{
    [Fact]
    public async Task ListServicesAsync_ReturnsServiceSummaries()
    {
        var services = new[]
        {
            new { serviceName = "svc1", description = "Test service", layerCount = 3, enabledProtocols = new[] { "FeatureServer" } },
            new { serviceName = "svc2", description = "Other", layerCount = 1, enabledProtocols = new[] { "OgcFeatures" } }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/services/", req.RequestUri!.PathAndQuery);
            Assert.Equal(HttpMethod.Get, req.Method);
            return Task.FromResult(TestHelpers.CreateJsonResponse(services));
        });

        var result = await client.ListServicesAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("svc1", result[0].ServiceName);
        Assert.Equal(3, result[0].LayerCount);
    }

    [Fact]
    public async Task GetServiceSettingsAsync_ReturnsSettings()
    {
        var settings = new
        {
            serviceName = "default",
            enabledProtocols = new[] { "FeatureServer", "MapServer" },
            availableProtocols = new[] { "FeatureServer", "MapServer", "OgcFeatures" },
            mapServer = new
            {
                maxImageWidth = 4096,
                maxImageHeight = 4096,
                defaultImageWidth = 400,
                defaultImageHeight = 300,
                defaultDpi = 96,
                defaultFormat = "png",
                defaultTransparent = true,
                maxFeaturesPerLayer = 10000
            }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/services/default/settings", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(settings));
        });

        var result = await client.GetServiceSettingsAsync("default");

        Assert.Equal("default", result.ServiceName);
        Assert.Equal(2, result.EnabledProtocols.Length);
        Assert.NotNull(result.MapServer);
        Assert.Equal(4096, result.MapServer.MaxImageWidth);
        Assert.Equal("png", result.MapServer.DefaultFormat);
    }

    [Fact]
    public async Task UpdateProtocolsAsync_SendsPutWithProtocols()
    {
        var updated = new
        {
            serviceName = "default",
            enabledProtocols = new[] { "FeatureServer" },
            availableProtocols = new[] { "FeatureServer", "MapServer" },
            mapServer = new { maxImageWidth = 4096, maxImageHeight = 4096, defaultImageWidth = 400, defaultImageHeight = 300, defaultDpi = 96, defaultFormat = "png", defaultTransparent = true, maxFeaturesPerLayer = 10000 }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/protocols", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(updated));
        });

        var result = await client.UpdateProtocolsAsync("default", ["FeatureServer"]);

        Assert.Equal("default", result.ServiceName);
        Assert.Single(result.EnabledProtocols);
    }

    [Fact]
    public async Task UpdateMapServerSettingsAsync_SendsPutWithSettings()
    {
        var updated = new
        {
            serviceName = "default",
            enabledProtocols = new[] { "FeatureServer" },
            availableProtocols = new[] { "FeatureServer" },
            mapServer = new { maxImageWidth = 8192, maxImageHeight = 8192, defaultImageWidth = 800, defaultImageHeight = 600, defaultDpi = 150, defaultFormat = "jpg", defaultTransparent = false, maxFeaturesPerLayer = 20000 }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/mapserver", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(updated));
        });

        var result = await client.UpdateMapServerSettingsAsync("default", new UpdateMapServerSettingsRequest
        {
            MaxImageWidth = 8192,
            MaxImageHeight = 8192
        });

        Assert.NotNull(result.MapServer);
        Assert.Equal(8192, result.MapServer.MaxImageWidth);
    }

    [Fact]
    public async Task UpdateAccessPolicyAsync_SendsPutWithPolicy()
    {
        var updated = new
        {
            serviceName = "default",
            enabledProtocols = new[] { "FeatureServer" },
            availableProtocols = new[] { "FeatureServer" },
            accessPolicy = new
            {
                allowAnonymous = false,
                allowAnonymousWrite = false,
                allowedRoles = new[] { "reader" },
                allowedWriteRoles = new[] { "writer" }
            },
            mapServer = new
            {
                maxImageWidth = 4096,
                maxImageHeight = 4096,
                defaultImageWidth = 400,
                defaultImageHeight = 300,
                defaultDpi = 96,
                defaultFormat = "png",
                defaultTransparent = true,
                maxFeaturesPerLayer = 10000
            }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/access-policy", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(updated));
        });

        var result = await client.UpdateAccessPolicyAsync("default", new UpdateAccessPolicyRequest
        {
            AllowAnonymous = false,
            AllowedRoles = ["reader"],
            AllowedWriteRoles = ["writer"]
        });

        Assert.NotNull(result.AccessPolicy);
        Assert.Equal(["reader"], result.AccessPolicy!.AllowedRoles ?? []);
        Assert.Equal(["writer"], result.AccessPolicy.AllowedWriteRoles ?? []);
    }

    [Fact]
    public async Task UpdateTimeInfoAsync_SendsPutWithTimeInfo()
    {
        var updated = new
        {
            serviceName = "default",
            enabledProtocols = new[] { "FeatureServer" },
            availableProtocols = new[] { "FeatureServer" },
            timeInfo = new
            {
                startTimeField = "start_utc",
                endTimeField = "end_utc",
                trackIdField = "track_id"
            },
            mapServer = new
            {
                maxImageWidth = 4096,
                maxImageHeight = 4096,
                defaultImageWidth = 400,
                defaultImageHeight = 300,
                defaultDpi = 96,
                defaultFormat = "png",
                defaultTransparent = true,
                maxFeaturesPerLayer = 10000
            }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/timeinfo", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(updated));
        });

        var result = await client.UpdateTimeInfoAsync("default", new UpdateTimeInfoRequest
        {
            StartTimeField = "start_utc",
            EndTimeField = "end_utc",
            TrackIdField = "track_id"
        });

        Assert.NotNull(result.TimeInfo);
        Assert.Equal("start_utc", result.TimeInfo!.StartTimeField);
        Assert.Equal("track_id", result.TimeInfo.TrackIdField);
    }

    [Fact]
    public async Task UpdateLayerMetadataAsync_SendsPutWithLayerPatch()
    {
        var updated = new
        {
            layerId = 7,
            layerName = "Parcels",
            accessPolicy = new
            {
                allowAnonymous = false,
                allowAnonymousWrite = false,
                allowedRoles = new[] { "reader" },
                allowedWriteRoles = new[] { "writer" }
            },
            timeInfo = new
            {
                startTimeField = "start_utc",
                endTimeField = "end_utc",
                trackIdField = "track_id"
            }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/services/default/layers/7/metadata", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(updated));
        });

        var result = await client.UpdateLayerMetadataAsync("default", 7, new UpdateLayerMetadataRequest
        {
            AccessPolicy = new UpdateAccessPolicyRequest { AllowedRoles = ["reader"] },
            TimeInfo = new UpdateTimeInfoRequest { StartTimeField = "start_utc" }
        });

        Assert.Equal(7, result.LayerId);
        Assert.Equal("Parcels", result.LayerName);
        Assert.NotNull(result.AccessPolicy);
        Assert.Equal(["reader"], result.AccessPolicy!.AllowedRoles ?? []);
    }
}
