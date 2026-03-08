// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as a real deployed-environment cloud validation check.
/// Skips execution when required environment variables are not present.
/// </summary>
[TraitDiscoverer("Honua.TestKit.Attributes.CloudTestDiscoverer", "Honua.TestKit")]
public sealed class CloudTestAttribute : FactAttribute, ITraitAttribute
{
    public CloudTestAttribute(params string[] requiredEnvironmentVariables)
    {
        if (requiredEnvironmentVariables.Length == 0)
        {
            return;
        }

        var missing = requiredEnvironmentVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();

        if (missing.Length > 0)
        {
            Skip = $"Missing required environment variables: {string.Join(", ", missing)}";
        }
    }
}

public sealed class CloudTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Category", "Cloud")
        ];
    }
}
