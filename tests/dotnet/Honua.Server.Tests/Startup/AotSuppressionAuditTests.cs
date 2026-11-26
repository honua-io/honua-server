// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Migration.Services;
using Honua.Plugins;
using Honua.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Startup;

public sealed class AotSuppressionAuditTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void HonuaServerProject_NativeAotAndJsonReflectionSwitch_ArePreserved()
    {
        var projectPath = RepositoryPaths.Resolve("src", "Honua.Server", "Honua.Server.csproj");
        var document = XDocument.Load(projectPath);

        GetPropertyValue(document, "PublishAot").Should().Be("true");
        GetPropertyValue(document, "JsonSerializerIsReflectionEnabledByDefault").Should().Be("false");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void DockerfileAot_PostgisProfile_PreservesSkiaNonRootAndInvariantGlobalization()
    {
        var dockerfilePath = RepositoryPaths.Resolve("docker", "Dockerfile.aot");
        var content = File.ReadAllText(dockerfilePath);

        content.Should().Contain("HonuaUseSkiaNoDependencies=true");
        content.Should().Contain("USER 1654");
        ExtractStage(content, "chiseled-base-postgis")
            .Should().Contain("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void AddPluginOptions_WithReflectionJsonDisabled_BindsAndValidatesOptions()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Plugins:audit-plugin:MaxItems"] = "42",
            ["Plugins:audit-plugin:Mode"] = "strict",
        });

        services.AddPluginOptions<AuditPluginOptions>(configuration, "audit-plugin");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditPluginOptions>>().Value;

        options.MaxItems.Should().Be(42);
        options.Mode.Should().Be("strict");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void AddPluginOptions_MissingRequiredProperty_ThrowsOptionsValidationException()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Plugins:audit-plugin:MaxItems"] = "10",
        });

        services.AddPluginOptions<AuditPluginOptions>(configuration, "audit-plugin");

        using var provider = services.BuildServiceProvider();
        var resolve = () => provider.GetRequiredService<IOptions<AuditPluginOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void MigrationReadinessArtifactHashJsonContext_ProvidesSourceGeneratedTypeInfo()
    {
        var context = MigrationReadinessArtifactHashJsonContext.Default;

        context.MigrationSourceInventoryArtifact.Should().NotBeNull();
        context.MigrationManifestArtifact.Should().NotBeNull();
        context.MigrationParityEvidenceArtifact.Should().NotBeNull();
    }

    private static string? GetPropertyValue(XDocument document, string propertyName)
        => document.Descendants(propertyName).FirstOrDefault()?.Value.Trim();

    private static string ExtractStage(string dockerfile, string stageAlias)
    {
        var marker = $"AS {stageAlias}";
        var start = dockerfile.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"stage '{stageAlias}' must exist in Dockerfile.aot");

        var nextStage = dockerfile.IndexOf("\nFROM ", start + marker.Length, StringComparison.Ordinal);
        return nextStage < 0 ? dockerfile[start..] : dockerfile[start..nextStage];
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class AuditPluginOptions
    {
        [Range(1, 1000)]
        public int MaxItems { get; set; }

        [Required]
        public string Mode { get; set; } = string.Empty;
    }
}
