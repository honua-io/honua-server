// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Tests.Features.Licensing;

/// <summary>
/// Unit tests for <see cref="CapabilityKeyCatalog"/> (issue #2893): the
/// canonical customer-facing capability vocabulary that extends
/// <see cref="FeatureCatalog"/> with Community-tier capability keys.
/// </summary>
public sealed class CapabilityKeyCatalogTests
{
    private static readonly Regex KeyFormat = new(
        "^[a-z0-9]+(-[a-z0-9]+)*\\.[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled);

    [Trait("Tier", "Fast")]
    [Fact]
    public void CommunityKeys_IsNotEmpty()
    {
        CapabilityKeyCatalog.CommunityKeys.Should().NotBeEmpty();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void CommunityKeys_AllCarryCommunityEdition()
    {
        // Deliverable 1 (#2893): Community capability keys are modeled with
        // edition=Community and must never be entitlement-gated.
        CapabilityKeyCatalog.CommunityKeys.Should().OnlyContain(
            capability => capability.Edition == HonuaEdition.Community);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void CommunityKeys_HaveUniqueKeys()
    {
        CapabilityKeyCatalog.CommunityKeys.Select(c => c.Key)
            .Should().OnlyHaveUniqueItems();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void CommunityKeys_DoNotOverlapFeatureCatalogKeys()
    {
        // The entitlement vocabulary (FeatureCatalog) and the new Community keys
        // must be disjoint — each capability key has exactly one owner.
        var featureKeys = FeatureCatalog.All.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var communityKeys = CapabilityKeyCatalog.CommunityKeys.Select(c => c.Key);

        communityKeys.Should().NotContain(key => featureKeys.Contains(key));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [MemberData(nameof(AllCommunityKeys))]
    public void CommunityKeys_FollowDotNamespacedLowercaseFormat(string key)
    {
        KeyFormat.IsMatch(key).Should().BeTrue(
            $"capability key '{key}' must be dot-namespaced lowercase, e.g. 'editing.feature-edits'");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void CommunityKeys_EachHasRequiredProperties()
    {
        foreach (var capability in CapabilityKeyCatalog.CommunityKeys)
        {
            capability.Key.Should().NotBeNullOrWhiteSpace();
            capability.DisplayName.Should().NotBeNullOrWhiteSpace();
            capability.Category.Should().NotBeNullOrWhiteSpace();
            capability.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void All_IsUnionOfCommunityLifecycleDescriptiveAndFeatureCatalogKeys()
    {
        CapabilityKeyCatalog.All.Should().HaveCount(
            CapabilityKeyCatalog.CommunityKeys.Count
            + CapabilityKeyCatalog.RoutedExperimentalKeys.Count
            + CapabilityKeyCatalog.RoutedPreviewKeys.Count
            + CapabilityKeyCatalog.RoutedDependentKeys.Count
            + CapabilityKeyCatalog.DescriptiveKeys.Count
            + FeatureCatalog.All.Count
            - CapabilityKeyCatalog.RoutedPreviewKeys.Count);
    }

    private const string ResolvedByPostgresJobState =
        "resolved when durable job and approval state moves to PostgreSQL (honua-server#6809)";

    private static readonly IReadOnlyDictionary<(string Capability, string Required), string> AllowedHigherEditionRequirements =
        new Dictionary<(string, string), string>
        {
            [("jobs.durable-runtime", FeatureCatalog.RedisCacheKey)] = ResolvedByPostgresJobState,
            [("operations.proposals", FeatureCatalog.RedisCacheKey)] = ResolvedByPostgresJobState,
        };

    [Trait("Tier", "Fast")]
    [Fact]
    public void Requires_OnlyNamesKnownCapabilityKeys()
    {
        var knownKeys = CapabilityKeyCatalog.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        CapabilityKeyCatalog.All.SelectMany(c => c.Requires).Should().OnlyContain(required => knownKeys.Contains(required));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Requires_NamesAHigherEditionKeyOnlyForTheAllowedExceptions()
    {
        var editionByKey = CapabilityKeyCatalog.All.ToDictionary(c => c.Key, c => c.Edition, StringComparer.Ordinal);

        var higherEditionRequirements = CapabilityKeyCatalog.All
            .SelectMany(capability => capability.Requires
                .Where(required => editionByKey[required] > capability.Edition)
                .Select(required => (capability.Key, required)))
            .ToArray();

        higherEditionRequirements.Should().BeEquivalentTo(
            AllowedHigherEditionRequirements.Keys,
            "a capability that requires a key from a higher edition cannot work on its own edition, "
            + "and an allowed exception that is no longer a violation must be removed from the list");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Requires_DeclaresTheKnownEntitlementDependencies()
    {
        RequiredKeysOf("enrichment.datasets").Should().Equal("analytics.spatial-join");
        RequiredKeysOf(FeatureCatalog.OidcClaimsMappingKey).Should().Equal(FeatureCatalog.OidcAuthenticationKey);
        RequiredKeysOf(FeatureCatalog.OidcMultiProviderKey).Should().Equal(FeatureCatalog.OidcAuthenticationKey);
        RequiredKeysOf("jobs.durable-runtime").Should().Equal(FeatureCatalog.RedisCacheKey);
        RequiredKeysOf("operations.proposals").Should().Equal(FeatureCatalog.RedisCacheKey);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void RoutedDependentKeys_AreGatedByAtLeastOneRequiredEntitlement()
    {
        CapabilityKeyCatalog.RoutedDependentKeys.Should().OnlyContain(capability =>
            capability.Requires.Count > 0
            && capability.Requires.All(required => FeatureCatalog.All.Any(feature => feature.Key == required)));
        CapabilityKeyCatalog.RoutedDependentKeys.Single(capability => capability.Key == "enrichment.datasets")
            .Edition.Should().Be(HonuaEdition.Pro);
    }

    private static IReadOnlyList<string> RequiredKeysOf(string capabilityKey)
        => CapabilityKeyCatalog.All.Single(capability => capability.Key == capabilityKey).Requires;

    [Trait("Tier", "Fast")]
    [Fact]
    public void DescriptiveKeys_AreEditionQualifiedWithoutMutatingFeatureCatalog()
    {
        CapabilityKeyCatalog.DescriptiveKeys.Should().OnlyContain(
            capability => capability.Edition == HonuaEdition.Enterprise);
        CapabilityKeyCatalog.DescriptiveKeys.Select(capability => capability.Key).Should().Contain(
            ["provider.redshift", "provider.snowflake", "provider.databricks"]);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void All_HasUniqueKeys()
    {
        CapabilityKeyCatalog.All.Select(c => c.Key).Should().OnlyHaveUniqueItems();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void All_DoesNotMutateFeatureCatalog()
    {
        // Deliverable 1 (#2893): this layer must not touch entitlement
        // enforcement — FeatureCatalog.All is consumed, never modified.
        var beforeCount = FeatureCatalog.All.Count;
        _ = CapabilityKeyCatalog.All;
        FeatureCatalog.All.Should().HaveCount(beforeCount);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void All_EveryProEnterpriseEntryMatchesItsFeatureCatalogSource()
    {
        var featureByKey = FeatureCatalog.All.ToDictionary(f => f.Key, StringComparer.Ordinal);

        foreach (var capability in CapabilityKeyCatalog.All)
        {
            if (!featureByKey.TryGetValue(capability.Key, out var feature))
            {
                continue;
            }

            capability.DisplayName.Should().Be(feature.DisplayName);
            capability.Category.Should().Be(feature.Category);
            capability.Edition.Should().Be(feature.MinimumEdition);
            capability.Description.Should().Be(feature.Description);
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void DeployableKeys_ExcludesDescriptiveKeysButKeepsEverythingElse()
    {
        var deployable = CapabilityKeyCatalog.DeployableKeys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
        var descriptive = CapabilityKeyCatalog.DescriptiveKeys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);

        descriptive.Should().NotBeEmpty("the catalog must carry descriptive keys for this split to mean anything");
        deployable.Should().NotIntersectWith(descriptive);
        deployable.Should().BeEquivalentTo(
            CapabilityKeyCatalog.All.Select(k => k.Key).Except(descriptive, StringComparer.Ordinal));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void DeployableKeys_IncludesRoutedExperimentalKeys()
    {
        CapabilityKeyCatalog.DeployableKeys.Should().Contain(
            capability => capability.Key == "serve.i3s-scene"
                && capability.Status == CapabilityKeyCatalog.ExperimentalStatus);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void DescriptiveKeys_AreAllMarkedExperimental()
    {
        // The status string is the contract shared with capability-keys.v1.json and the deployment
        // profile generator; a descriptive key that loses it silently becomes deployable again.
        CapabilityKeyCatalog.DescriptiveKeys.Should().OnlyContain(
            k => k.Status == CapabilityKeyCatalog.ExperimentalStatus);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void CustomerAlertingKeys_AreAllMarkedPreview()
    {
        CapabilityKeyCatalog.All
            .Where(capability => capability.Category is FeatureCatalog.Categories.Alerts
                or FeatureCatalog.Categories.Channels)
            .Should().OnlyContain(capability => capability.Status == CapabilityKeyCatalog.PreviewStatus);
    }

    public static TheoryData<string> AllCommunityKeys()
    {
        var data = new TheoryData<string>();
        foreach (var capability in CapabilityKeyCatalog.CommunityKeys)
        {
            data.Add(capability.Key);
        }

        return data;
    }
}
