// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Scene;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Scene.Source;

public sealed class ConfigurationSceneDatasetRegistryTests
{
    [Fact]
    public async Task FindAsync_ProjectsMutableConfigurationToPolicySnapshot()
    {
        var policyOptions = new SceneAccessPolicyOptions
        {
            AllowAnonymous = false,
            AllowedRoles = ["scene-reader"]
        };
        var options = Options.Create(new SceneDatasetOptions
        {
            Datasets =
            [
                new SceneDatasetEntry
                {
                    Id = "protected-scene",
                    AssetRoot = "fixtures/protected-scene",
                    AccessPolicy = policyOptions
                }
            ]
        });
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(Path.GetTempPath());

        var registry = new ConfigurationSceneDatasetRegistry(options, environment);
        var dataset = await registry.FindAsync("protected-scene");

        Assert.NotNull(dataset);
        var accessPolicy = Assert.IsType<Honua.Core.Features.Security.Domain.AccessPolicy>(
            dataset.AccessPolicy);
        Assert.False(accessPolicy.AllowAnonymous);
        Assert.NotNull(accessPolicy.AllowedRoles);
        Assert.Equal("scene-reader", Assert.Single(accessPolicy.AllowedRoles));

        policyOptions.AllowAnonymous = true;
        policyOptions.AllowedRoles![0] = "mutated-role";

        Assert.False(accessPolicy.AllowAnonymous);
        Assert.Equal("scene-reader", Assert.Single(accessPolicy.AllowedRoles));
    }
}
