// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
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

        Property(documentation, "Limits:Connections:MaxConnectionPoolSize").Validation.Should().Be("Range: 1-1000");
        Property(documentation, "Limits:Connections:MaxConnectionPoolSize").Description.Should().Contain("defaults to 4");

        var currentValues = AllCurrentValues(documentation.Sections);
        currentValues.Should().NotContain(TokenWebhook);
        currentValues.Should().NotContain(ContributorToken);
        currentValues.Should().NotContain(NestedToken);
        currentValues.Should().NotContain("webhook-signing-secret");
    }

    [UnitTest]
    public void BuildDocumentation_LimitDefaultsComeFromUnconfiguredOptions()
    {
        var configured = new LimitsOptions();
        configured.Query.MaxRecordCount = 2000;
        configured.Geometry.MaxVerticesPerGeometry = 1000;
        configured.Geometry.MaxGeometrySize = 1;
        configured.Geometry.MaxOverlayCandidatePairs = 1000;
        configured.Edits.MaxFeaturesPerEdit = 2;
        configured.Edits.MaxEditsPerTransaction = 3;
        configured.Edits.MaxPayloadSize = 4;
        configured.Attachments.MaxAttachmentSize = 5;
        configured.Attachments.MaxAttachmentsPerFeature = 1;
        configured.Attachments.MaxTotalAttachmentSize = 6;
        configured.Tiles.MaxTileZoom = 5;
        configured.Tiles.MaxFeaturesPerTile = 7;
        configured.Tiles.TileTimeout = TimeSpan.FromSeconds(1);
        configured.Connections.MaxConcurrentQueries = 10;
        configured.Connections.RequestTimeout = TimeSpan.FromSeconds(11);

        var documentation = CreateService(configured).BuildDocumentation();
        var code = new LimitsOptions();

        ExpectDefault(documentation, "Limits:Query:MaxRecordCount", code.Query.MaxRecordCount, 2000);
        ExpectDefault(documentation, "Limits:Geometry:MaxVerticesPerGeometry", code.Geometry.MaxVerticesPerGeometry, 1000);
        ExpectDefault(documentation, "Limits:Geometry:MaxGeometrySize", code.Geometry.MaxGeometrySize, 1L);
        ExpectDefault(documentation, "Limits:Geometry:MaxOverlayCandidatePairs", code.Geometry.MaxOverlayCandidatePairs, 1000L);
        ExpectDefault(documentation, "Limits:Edits:MaxFeaturesPerEdit", code.Edits.MaxFeaturesPerEdit, 2);
        ExpectDefault(documentation, "Limits:Edits:MaxEditsPerTransaction", code.Edits.MaxEditsPerTransaction, 3);
        ExpectDefault(documentation, "Limits:Edits:MaxPayloadSize", code.Edits.MaxPayloadSize, 4L);
        ExpectDefault(documentation, "Limits:Attachments:MaxAttachmentSize", code.Attachments.MaxAttachmentSize, 5L);
        ExpectDefault(documentation, "Limits:Attachments:MaxAttachmentsPerFeature", code.Attachments.MaxAttachmentsPerFeature, 1);
        ExpectDefault(documentation, "Limits:Attachments:MaxTotalAttachmentSize", code.Attachments.MaxTotalAttachmentSize, 6L);
        ExpectDefault(documentation, "Limits:Tiles:MaxTileZoom", code.Tiles.MaxTileZoom, 5);
        ExpectDefault(documentation, "Limits:Tiles:MaxFeaturesPerTile", code.Tiles.MaxFeaturesPerTile, 7);
        ExpectDefault(documentation, "Limits:Tiles:TileTimeout", code.Tiles.TileTimeout, TimeSpan.FromSeconds(1));
        ExpectDefault(documentation, "Limits:Connections:MaxConcurrentQueries", code.Connections.MaxConcurrentQueries, 10);
        ExpectDefault(documentation, "Limits:Connections:RequestTimeout", code.Connections.RequestTimeout, TimeSpan.FromSeconds(11));

        EnvironmentDefault(documentation, "Limits__Query__MaxRecordCount")
            .Should().Be(code.Query.MaxRecordCount.ToString(CultureInfo.InvariantCulture));
        EnvironmentDefault(documentation, "Limits__Geometry__MaxVerticesPerGeometry")
            .Should().Be(code.Geometry.MaxVerticesPerGeometry.ToString(CultureInfo.InvariantCulture));
        EnvironmentDefault(documentation, "Limits__Connections__MaxConcurrentQueries")
            .Should().Be(code.Connections.MaxConcurrentQueries.ToString(CultureInfo.InvariantCulture));
    }

    private static void ExpectDefault(
        ConfigurationDocumentation documentation,
        string path,
        object codeDefault,
        object configuredCurrent)
    {
        var property = Property(documentation, path);
        property.DefaultValue.Should().Be(codeDefault);
        property.CurrentValue.Should().Be(configuredCurrent);
    }

    private static string? EnvironmentDefault(ConfigurationDocumentation documentation, string name)
        => documentation.EnvironmentVariables.Should().ContainSingle(variable => variable.Name == name).Which.Default;

    private static ConfigurationDocumentationService CreateService(LimitsOptions limits)
        => new(
            new ConfigurationBuilder().Build(),
            new TestWebHostEnvironment(),
            Options.Create(limits),
            Options.Create(new CacheOptions()),
            Options.Create(new TileOptions()),
            Options.Create(new AdaptiveSamplingOptions()),
            Options.Create(new TracingOptions()),
            []);

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
