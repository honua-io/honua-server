// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class PluginOptionsBindingTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void AddPluginOptions_BindsPerPluginSection()
    {
        var services = new ServiceCollection();
        var configuration = Config(
            ("Plugins:utility-audit:Threshold", "42"),
            ("Plugins:utility-audit:Label", "high"));
        services.AddPluginOptions<SampleOptions>(configuration, "utility-audit");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SampleOptions>>().Value;

        options.Threshold.Should().Be(42);
        options.Label.Should().Be("high");
    }

    [Fact]
    public void AddPluginOptions_IsolatesPluginsByIdSection()
    {
        var services = new ServiceCollection();
        var configuration = Config(("Plugins:other-plugin:Threshold", "99"));
        services.AddPluginOptions<SampleOptions>(configuration, "utility-audit");

        using var provider = services.BuildServiceProvider();
        // No section for utility-audit -> defaults, not the other plugin's value.
        provider.GetRequiredService<IOptions<SampleOptions>>().Value.Threshold.Should().Be(0);
    }

    [Fact]
    public void AddPluginOptions_ValidatesDataAnnotations_OnStart()
    {
        var services = new ServiceCollection();
        // Label is [Required]; omitting it must fail eager ValidateOnStart.
        var configuration = Config(("Plugins:utility-audit:Threshold", "5"));
        services.AddPluginOptions<RequiredLabelOptions>(configuration, "utility-audit");

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<RequiredLabelOptions>>().Value;
        act.Should().Throw<OptionsValidationException>();
    }

    private sealed class SampleOptions
    {
        public int Threshold { get; set; }

        public string? Label { get; set; }
    }

    private sealed class RequiredLabelOptions
    {
        public int Threshold { get; set; }

        [Required]
        public string Label { get; set; } = null!;
    }
}
