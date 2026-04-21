// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Security;

namespace Honua.Server.Tests.Infrastructure.Security;

/// <summary>
/// Tests for the ContentSecurityPolicyBuilder class.
/// Verifies CSP policy generation, validation, and geospatial-specific configurations.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "Security")]
[Trait("Feature", "ContentSecurityPolicy")]
public class ContentSecurityPolicyBuilderTests
{
    [Fact]
    public void Build_WithDefaultConfiguration_ReturnsBasicSecurityPolicy()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder();

        // Act
        var policy = builder.Build();

        // Assert
        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("style-src 'self' 'unsafe-inline'", policy);
        Assert.Contains("img-src 'self' data:", policy);
        Assert.Contains("connect-src 'self'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
    }

    [Fact]
    public void ForGeospatialApi_WithProductionSettings_CreatesRestrictivePolicy()
    {
        // Arrange & Act
        var builder = ContentSecurityPolicyBuilder.ForGeospatialApi(isDevelopment: false);
        var policy = builder.Build();

        // Assert
        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        Assert.DoesNotContain("localhost", policy);
        Assert.Contains("img-src 'self' data: blob:", policy);
        Assert.Contains("worker-src 'self' blob:", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
    }

    [Fact]
    public void ForGeospatialApi_WithDevelopmentSettings_AllowsDevFeatures()
    {
        // Arrange & Act
        var builder = ContentSecurityPolicyBuilder.ForGeospatialApi(isDevelopment: true);
        var policy = builder.Build();

        // Assert
        Assert.Contains("script-src 'self' 'unsafe-eval' 'unsafe-inline' localhost:* 127.0.0.1:* *.localhost", policy);
        Assert.Contains("localhost:*", policy);
        Assert.Contains("'unsafe-eval'", policy);
    }

    [Fact]
    public void ForApiOnly_CreatesVeryRestrictivePolicy()
    {
        // Arrange & Act
        var builder = ContentSecurityPolicyBuilder.ForApiOnly();
        var policy = builder.Build();

        // Assert
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("script-src 'none'", policy);
        Assert.Contains("style-src 'none'", policy);
        Assert.Contains("img-src 'none'", policy);
        Assert.Contains("connect-src 'none'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
    }

    [Fact]
    public void AllowTileServers_AddsServersToImageAndConnectDirectives()
    {
        // Arrange
        var builder = ContentSecurityPolicyBuilder.ForGeospatialApi();
        var tileServers = new[] { "example-tiles.com", "*.mapbox.com" };

        // Act
        builder.AllowTileServers(tileServers);
        var policy = builder.Build();

        // Assert
        Assert.Contains("img-src 'self' data: blob: *.openstreetmap.org *.tile.openstreetmap.org *.tiles.mapbox.com *.api.mapbox.com *.esri.com *.arcgisonline.com *.arcgis.com example-tiles.com *.mapbox.com", policy);
        Assert.Contains("connect-src 'self' *.openstreetmap.org *.tile.openstreetmap.org *.tiles.mapbox.com *.api.mapbox.com *.esri.com *.arcgisonline.com *.arcgis.com example-tiles.com *.mapbox.com", policy);
    }

    [Fact]
    public void AllowMappingCdns_AddsToScriptStyleAndFontDirectives()
    {
        // Arrange
        var builder = ContentSecurityPolicyBuilder.ForGeospatialApi();
        var cdns = new[] { "example-cdn.com" };

        // Act
        builder.AllowMappingCdns(cdns);
        var policy = builder.Build();

        // Assert
        Assert.Contains("script-src 'self' cdnjs.cloudflare.com unpkg.com cdn.jsdelivr.net example-cdn.com", policy);
        Assert.Contains("style-src 'self' 'unsafe-inline' cdnjs.cloudflare.com unpkg.com cdn.jsdelivr.net example-cdn.com", policy);
        Assert.Contains("font-src 'self' data: cdnjs.cloudflare.com unpkg.com cdn.jsdelivr.net example-cdn.com", policy);
    }

    [Fact]
    public void AllowWebSockets_ConvertsHttpToWebSocketProtocols()
    {
        // Arrange
        var builder = ContentSecurityPolicyBuilder.ForGeospatialApi();
        var urls = new[] { "https://api.example.com", "http://insecure.example.com" };

        // Act
        builder.AllowWebSockets(urls);
        var policy = builder.Build();

        // Assert
        Assert.Contains("connect-src 'self' wss://api.example.com ws://insecure.example.com", policy);
    }

    [Fact]
    public void AllowInlineStyleHashes_AddsHashesToStyleDirective()
    {
        // Arrange
        var builder = ContentSecurityPolicyBuilder.ForGeospatialApi();
        var hashes = new[] { "abc123", "'sha256-def456'" };

        // Act
        builder.AllowInlineStyleHashes(hashes);
        var policy = builder.Build();

        // Assert
        Assert.Contains("style-src 'self' 'unsafe-inline' 'sha256-abc123' 'sha256-def456'", policy);
    }

    [Fact]
    public void AllowInlineScriptHashes_AddsHashesToScriptDirective()
    {
        // Arrange
        var builder = ContentSecurityPolicyBuilder.ForGeospatialApi();
        var hashes = new[] { "script123" };

        // Act
        builder.AllowInlineScriptHashes(hashes);
        var policy = builder.Build();

        // Assert
        Assert.Contains("script-src 'self' 'sha256-script123'", policy);
    }

    [Fact]
    public void AddDirective_WithMultipleValues_AddsAllValues()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder();

        // Act
        builder.AddDirective("test-src", "value1", "value2", "value3");
        var policy = builder.Build();

        // Assert
        Assert.Contains("test-src value1 value2 value3", policy);
    }

    [Fact]
    public void RemoveDirective_RemovesEntireDirective()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder();
        builder.AddDirective("test-src", "value1");

        // Act
        builder.RemoveDirective("test-src");
        var policy = builder.Build();

        // Assert
        Assert.DoesNotContain("test-src", policy);
    }

    [Fact]
    public void RemoveFromDirective_RemovesSpecificValues()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder();
        builder.AddDirective("test-src", "value1", "value2", "value3");

        // Act
        builder.RemoveFromDirective("test-src", "value2");
        var policy = builder.Build();

        // Assert
        Assert.Contains("test-src value1 value3", policy);
        Assert.DoesNotContain("value2", policy);
    }

    [Fact]
    public void Validate_WithUnsafeEvalInProduction_ReturnsWarning()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder(isDevelopment: false);
        builder.AddDirective("script-src", "'self'", "'unsafe-eval'");

        // Act
        var warnings = builder.Validate();

        // Assert
        Assert.Contains(warnings, w => w.Contains("'unsafe-eval'") && w.Contains("dangerous"));
    }

    [Fact]
    public void Validate_WithUnsafeInlineInProduction_ReturnsWarning()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder(isDevelopment: false);
        builder.RemoveDirective("script-src");
        builder.AddDirective("script-src", "'self'", "'unsafe-inline'");

        // Act
        var warnings = builder.Validate();

        // Assert
        Assert.Contains(warnings, w => w.Contains("'unsafe-inline'") && w.Contains("dangerous"));
    }

    [Fact]
    public void Validate_WithMissingDefaultSrc_ReturnsWarning()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder();
        builder.RemoveDirective("default-src");

        // Act
        var warnings = builder.Validate();

        // Assert
        Assert.Contains(warnings, w => w.Contains("default-src") && w.Contains("Missing"));
    }

    [Fact]
    public void Validate_WithWildcardInScriptSrc_ReturnsWarning()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder();
        builder.AddDirective("script-src", "*");

        // Act
        var warnings = builder.Validate();

        // Assert
        Assert.Contains(warnings, w => w.Contains("Wildcard") && w.Contains("insecure"));
    }

    [Fact]
    public void Validate_WithUnsafeInlineInStylesInProduction_ReturnsWarning()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder(isDevelopment: false);

        // Act - default includes 'unsafe-inline' for styles
        var warnings = builder.Validate();

        // Assert
        Assert.Contains(warnings, w => w.Contains("style hashes") && w.Contains("'unsafe-inline'"));
    }

    [Fact]
    public void Validate_InDevelopmentMode_AllowsUnsafeDirectives()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder(isDevelopment: true);
        builder.AddDirective("script-src", "'self'", "'unsafe-eval'", "'unsafe-inline'");

        // Act
        var warnings = builder.Validate();

        // Assert - Should not warn about unsafe directives in development
        Assert.DoesNotContain(warnings, w => w.Contains("'unsafe-eval'") && w.Contains("dangerous"));
        Assert.DoesNotContain(warnings, w => w.Contains("'unsafe-inline'") && w.Contains("dangerous"));
    }

    [Theory]
    [InlineData("default-src 'self'; script-src 'self'", true)]
    [InlineData("", false)]
    [InlineData("script-src 'self'; style-src 'self'", true)]
    public void Build_WithVariousConfigurations_ProducesExpectedOutput(string expectedSubstring, bool shouldContain)
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder();
        if (!shouldContain)
        {
            // Remove all directives for empty test case
            builder.RemoveDirective("default-src");
            builder.RemoveDirective("script-src");
            builder.RemoveDirective("style-src");
            builder.RemoveDirective("img-src");
            builder.RemoveDirective("connect-src");
            builder.RemoveDirective("font-src");
            builder.RemoveDirective("media-src");
            builder.RemoveDirective("object-src");
            builder.RemoveDirective("frame-ancestors");
            builder.RemoveDirective("form-action");
            builder.RemoveDirective("base-uri");
        }

        // Act
        var policy = builder.Build();

        // Assert
        if (shouldContain && !string.IsNullOrEmpty(expectedSubstring))
        {
            Assert.Contains(expectedSubstring, policy);
        }
        else if (!shouldContain)
        {
            Assert.Equal(string.Empty, policy);
        }
    }

    [Fact]
    public void Build_WithDuplicateDirectiveValues_DeduplicatesValues()
    {
        // Arrange
        var builder = new ContentSecurityPolicyBuilder();
        builder.AddDirective("script-src", "'self'");
        builder.AddDirective("script-src", "'self'"); // Duplicate

        // Act
        var policy = builder.Build();

        // Assert - Should only contain 'self' once
        var scriptSrcPart = policy.Split(';')
            .FirstOrDefault(part => part.Trim().StartsWith("script-src", StringComparison.Ordinal))?.Trim();

        Assert.NotNull(scriptSrcPart);
        var selfCount = scriptSrcPart.Split(' ').Count(value => value == "'self'");
        Assert.Equal(1, selfCount);
    }
}
