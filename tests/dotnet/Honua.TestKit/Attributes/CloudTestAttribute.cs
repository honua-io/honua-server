// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as a real deployed-environment cloud validation check.
/// Skips execution when required environment variables are not present.
/// Tier=Slow with Category=Cloud — runs only on a future cloud-dedicated workflow
/// (real cloud credentials); the existing nightly slow-tier workflow scopes to
/// Category=Emulator. See ADR-0037.
/// </summary>
[TraitDiscoverer("Honua.TestKit.Attributes.CloudTestDiscoverer", "Honua.TestKit")]
public sealed class CloudTestAttribute : FactAttribute, ITraitAttribute
{
    private readonly string[] _requiredEnvironmentVariables;
    private string[] _requiredTrueEnvironmentVariables = [];
    private string[] _skipWhenFalseEnvironmentVariables = [];

    public CloudTestAttribute(params string[] requiredEnvironmentVariables)
    {
        _requiredEnvironmentVariables = requiredEnvironmentVariables;
        RefreshSkipReason();
    }

    public string[] RequiredTrueEnvironmentVariables
    {
        get => _requiredTrueEnvironmentVariables;
        set
        {
            _requiredTrueEnvironmentVariables = value ?? [];
            RefreshSkipReason();
        }
    }

    public string[] SkipWhenFalseEnvironmentVariables
    {
        get => _skipWhenFalseEnvironmentVariables;
        set
        {
            _skipWhenFalseEnvironmentVariables = value ?? [];
            RefreshSkipReason();
        }
    }

    private void RefreshSkipReason()
    {
        var missing = _requiredEnvironmentVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        var requiredTrue = _requiredTrueEnvironmentVariables
            .Where(name => !string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var explicitlyFalse = _skipWhenFalseEnvironmentVariables
            .Where(name => string.Equals(Environment.GetEnvironmentVariable(name), "false", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var reasons = new List<string>();
        if (missing.Length > 0)
        {
            reasons.Add($"Missing required environment variables: {string.Join(", ", missing)}");
        }

        if (requiredTrue.Length > 0)
        {
            reasons.Add($"Environment variables must be set to true: {string.Join(", ", requiredTrue)}");
        }

        if (explicitlyFalse.Length > 0)
        {
            reasons.Add($"Environment variables explicitly disabled this validation: {string.Join(", ", explicitlyFalse)}");
        }

        Skip = reasons.Count == 0 ? null : string.Join("; ", reasons);
    }
}

public sealed class CloudTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Category", "Cloud"),
            new KeyValuePair<string, string>("Tier", Tiers.Slow)
        ];
    }
}
