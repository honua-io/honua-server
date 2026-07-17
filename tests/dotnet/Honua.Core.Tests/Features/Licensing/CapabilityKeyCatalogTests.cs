// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    [Fact]
    public void CommunityKeys_IsNotEmpty()
    {
        CapabilityKeyCatalog.CommunityKeys.Should().NotBeEmpty();
    }

    [Fact]
    public void CommunityKeys_AllCarryCommunityEdition()
    {
        // Deliverable 1 (#2893): Community capability keys are modeled with
        // edition=Community and must never be entitlement-gated.
        CapabilityKeyCatalog.CommunityKeys.Should().OnlyContain(
            capability => capability.Edition == HonuaEdition.Community);
    }

    [Fact]
    public void CommunityKeys_HaveUniqueKeys()
    {
        CapabilityKeyCatalog.CommunityKeys.Select(c => c.Key)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CommunityKeys_DoNotOverlapFeatureCatalogKeys()
    {
        // The entitlement vocabulary (FeatureCatalog) and the new Community keys
        // must be disjoint — each capability key has exactly one owner.
        var featureKeys = FeatureCatalog.All.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var communityKeys = CapabilityKeyCatalog.CommunityKeys.Select(c => c.Key);

        communityKeys.Should().NotContain(key => featureKeys.Contains(key));
    }

    [Theory]
    [MemberData(nameof(AllCommunityKeys))]
    public void CommunityKeys_FollowDotNamespacedLowercaseFormat(string key)
    {
        KeyFormat.IsMatch(key).Should().BeTrue(
            $"capability key '{key}' must be dot-namespaced lowercase, e.g. 'editing.feature-edits'");
    }

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

    [Fact]
    public void All_IsUnionOfCommunityKeysAndFeatureCatalog()
    {
        CapabilityKeyCatalog.All.Should().HaveCount(
            CapabilityKeyCatalog.CommunityKeys.Count + FeatureCatalog.All.Count);
    }

    [Fact]
    public void All_HasUniqueKeys()
    {
        CapabilityKeyCatalog.All.Select(c => c.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void All_DoesNotMutateFeatureCatalog()
    {
        // Deliverable 1 (#2893): this layer must not touch entitlement
        // enforcement — FeatureCatalog.All is consumed, never modified.
        var beforeCount = FeatureCatalog.All.Count;
        _ = CapabilityKeyCatalog.All;
        FeatureCatalog.All.Should().HaveCount(beforeCount);
    }

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
