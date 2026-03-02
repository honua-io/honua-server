// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Sdk.Admin.Models;
using Honua.Sdk.Admin.Tests.Fixtures;

namespace Honua.Sdk.Admin.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task GetVersionAsync_ReturnsVersion()
    {
        var version = new
        {
            version = "1.0.0",
            metadataApiVersion = "honua.io/v1alpha1",
            serverTime = DateTimeOffset.UtcNow
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/version", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(version));
        });

        var result = await client.GetVersionAsync();

        Assert.Equal("1.0.0", result.Version);
        Assert.Equal("honua.io/v1alpha1", result.MetadataApiVersion);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_ReturnsCapabilities()
    {
        var capabilities = new
        {
            metadataApiVersions = new[] { "honua.io/v1alpha1" },
            resourceKinds = new[] { "Layer", "Service" },
            manifestSupported = true,
            manifestDryRunSupported = true,
            manifestPruneSupported = true
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/capabilities", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(capabilities));
        });

        var result = await client.GetCapabilitiesAsync();

        Assert.True(result.ManifestSupported);
        Assert.True(result.ManifestDryRunSupported);
        Assert.Contains("Layer", result.ResourceKinds);
    }

    [Fact]
    public async Task GetManifestAsync_ReturnsManifest()
    {
        var manifest = new
        {
            apiVersion = "honua.io/v1alpha1",
            generatedAt = DateTimeOffset.UtcNow,
            resources = new[]
            {
                new
                {
                    apiVersion = "honua.io/v1alpha1",
                    kind = "Layer",
                    metadata = new { name = "test", @namespace = "default" },
                    spec = new { type = "Feature" }
                }
            },
            driftedResources = Array.Empty<object>(),
            manifestHash = "abc123"
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/manifest", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(manifest));
        });

        var result = await client.GetManifestAsync();

        Assert.Single(result.Resources);
        Assert.Equal("abc123", result.ManifestHash);
    }

    [Fact]
    public async Task GetManifestAsync_WithNamespace_PassesQueryParam()
    {
        var manifest = new
        {
            apiVersion = "honua.io/v1alpha1",
            generatedAt = DateTimeOffset.UtcNow,
            resources = Array.Empty<object>(),
            driftedResources = Array.Empty<object>(),
            manifestHash = ""
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("namespace=prod", req.RequestUri!.Query);
            return Task.FromResult(TestHelpers.CreateJsonResponse(manifest));
        });

        await client.GetManifestAsync(ns: "prod");
    }

    [Fact]
    public async Task ApplyManifestAsync_SendsPostAndReturnsResult()
    {
        var applyResult = new
        {
            dryRun = false,
            summary = new { created = 1, updated = 0, deleted = 0, skipped = 0 },
            entries = new[]
            {
                new
                {
                    action = "create",
                    resource = new { kind = "Layer", @namespace = "default", name = "test" },
                    message = (string?)null
                }
            }
        };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/admin/manifest/apply", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(applyResult));
        });

        var result = await client.ApplyManifestAsync(new ManifestApplyRequest
        {
            Resources = [new MetadataResource
            {
                ApiVersion = "honua.io/v1alpha1",
                Kind = "Layer",
                Metadata = new ResourceMetadata { Name = "test", Namespace = "default" },
                Spec = JsonDocument.Parse("{\"type\":\"Feature\"}").RootElement
            }]
        });

        Assert.Equal(1, result.Summary.Created);
        Assert.Single(result.Entries);
        Assert.Equal("create", result.Entries[0].Action);
    }
}
