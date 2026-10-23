// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Abstractions;
using Honua.Server.Features.Admin.Services;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Admin configuration documentation masks credential-bearing current values the same way
/// discovery does, and still shows public URLs (honua-server#6025).
/// </summary>
public sealed class ConfigurationDocumentationCredentialTests
{
    private const string TokenWebhook = "https://hooks.example.com/events?token=super-secret";
    private const string PublicBaseUrl = "https://api.public.example.test";
    private const string ApprovalWebhook = "https://hooks.example.com/approvals";
    private const string ContributorToken = "https://hooks.example.com/events?token=contributor-secret";
    private const string NestedToken = "https://hooks.example.com/callback?token=nested-secret";
    private const string ContributorPublic = "https://maps.example.com";
    private const string ExampleToken = "https://hooks.example.com/events?token=example-token";

    [UnitTest]
    public void BuildDocumentation_MasksCredentialBearingUrlsAndKeepsPublicUrls()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureChangeEvents:Webhook:Url"] = TokenWebhook,
                ["FeatureChangeEvents:Webhook:Secret"] = "webhook-signing-secret",
                ["Public:BaseUrl"] = PublicBaseUrl,
                ["ManifestApproval:Webhook:Url"] = ApprovalWebhook,
            })
            .Build();
        var contributor = new TokenUrlContributor();
        var service = new ConfigurationDocumentationService(
            configuration,
            new TestWebHostEnvironment(),
            Options.Create(new LimitsOptions()),
            Options.Create(new CacheOptions()),
            Options.Create(new TileOptions()),
            Options.Create(new AdaptiveSamplingOptions()),
            Options.Create(new TracingOptions()),
            [contributor]);

        var documentation = service.BuildDocumentation();

        Property(documentation, "FeatureChangeEvents:Webhook:Url").CurrentValue.Should().Be(ConfigurationSecretRedactor.Mask);
        Property(documentation, "FeatureChangeEvents:Webhook:Secret").CurrentValue.Should().Be(ConfigurationSecretRedactor.Mask);
        Property(documentation, "Public:BaseUrl").CurrentValue.Should().Be(PublicBaseUrl);
        Property(documentation, "ManifestApproval:Webhook:Url").CurrentValue.Should().Be(ApprovalWebhook);
        Property(documentation, "Contributor:Hook").CurrentValue.Should().Be(ConfigurationSecretRedactor.Mask);
        Property(documentation, "Contributor:Public").CurrentValue.Should().Be(ContributorPublic);
        Property(documentation, "Contributor:Nested:Callback").CurrentValue.Should().Be(ConfigurationSecretRedactor.Mask);
        Property(documentation, "Contributor:Hook").DefaultValue.Should().Be(ContributorToken);

        documentation.EnvironmentVariables
            .Should().ContainSingle(variable => variable.Name == "CONTRIBUTOR_HOOK")
            .Which.Example.Should().Be(ExampleToken);

        var currentValues = AllCurrentValues(documentation.Sections);
        currentValues.Should().NotContain(TokenWebhook);
        currentValues.Should().NotContain(ContributorToken);
        currentValues.Should().NotContain(NestedToken);
        currentValues.Should().NotContain("webhook-signing-secret");
    }

    private static ConfigurationProperty Property(ConfigurationDocumentation documentation, string path)
    {
        return documentation.Sections
            .Select(section => Find(section, path))
            .FirstOrDefault(match => match is not null)
            ?? throw new InvalidOperationException($"Configuration property '{path}' was not documented.");
    }

    private static ConfigurationProperty? Find(ConfigurationSection section, string path)
    {
        return section.Properties.FirstOrDefault(property =>
                string.Equals(property.Path, path, StringComparison.Ordinal))
            ?? section.SubSections
                .Select(child => Find(child, path))
                .FirstOrDefault(match => match is not null);
    }

    private static List<string> AllCurrentValues(IEnumerable<ConfigurationSection> sections)
    {
        return sections
            .SelectMany(section => section.Properties
                .Select(property => property.CurrentValue)
                .OfType<string>()
                .Concat(AllCurrentValues(section.SubSections)))
            .ToList();
    }

    private sealed class TokenUrlContributor : IConfigurationDocumentationContributor
    {
        public IReadOnlyList<ConfigurationSection> GetSections()
            =>
            [
                new ConfigurationSection
                {
                    Name = "Contributor",
                    Properties =
                    [
                        new ConfigurationProperty
                        {
                            Name = "Hook",
                            Path = "Contributor:Hook",
                            CurrentValue = ContributorToken,
                            DefaultValue = ContributorToken,
                            IsSensitive = false,
                        },
                        new ConfigurationProperty
                        {
                            Name = "Public",
                            Path = "Contributor:Public",
                            CurrentValue = ContributorPublic,
                        },
                    ],
                    SubSections =
                    [
                        new ConfigurationSection
                        {
                            Name = "Nested",
                            Properties =
                            [
                                new ConfigurationProperty
                                {
                                    Name = "Callback",
                                    Path = "Contributor:Nested:Callback",
                                    CurrentValue = NestedToken,
                                },
                            ],
                        },
                    ],
                },
            ];

        public IReadOnlyList<EnvironmentVariableInfo> GetEnvironmentVariables()
            =>
            [
                new EnvironmentVariableInfo
                {
                    Name = "CONTRIBUTOR_HOOK",
                    ConfigPath = "Contributor",
                    Description = "Example only",
                    Example = ExampleToken,
                },
            ];
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Honua.Server.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
