// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as an emulator-backed integration test.
/// Skips execution when required environment variables are not present.
/// Tier=Slow with Category=Emulator — runs nightly via
/// <c>nightly-slow-tier.yml</c> (filter <c>Tier=Slow&amp;Category=Emulator</c>).
/// See ADR-0037.
/// </summary>
[TraitDiscoverer("Honua.TestKit.Attributes.EmulatorTestDiscoverer", "Honua.TestKit")]
public sealed class EmulatorTestAttribute : FactAttribute, ITraitAttribute
{
    private static readonly Dictionary<string, string> _defaultEmulatorValues =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["HONUA_TEST_S3_BUCKET"] = "honua-test",
            ["HONUA_TEST_S3_REGION"] = "us-east-1",
            ["HONUA_TEST_S3_ACCESS_KEY"] = "test",
            ["HONUA_TEST_S3_SECRET_KEY"] = "test",
            ["HONUA_TEST_S3_SERVICE_URL"] = "http://localhost:4566",
            ["HONUA_TEST_S3_FORCE_PATH_STYLE"] = "true",
            ["HONUA_TEST_AZURE_BLOB_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
            ["HONUA_TEST_AZURE_BLOB_CONTAINER"] = "honua-test"
        };

    public EmulatorTestAttribute(params string[] requiredEnvironmentVariables)
    {
        if (requiredEnvironmentVariables.Length == 0)
        {
            return;
        }

        foreach (var name in requiredEnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) &&
                _defaultEmulatorValues.TryGetValue(name, out var defaultValue))
            {
                Environment.SetEnvironmentVariable(name, defaultValue);
            }
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

public sealed class EmulatorTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Category", "Emulator"),
            new KeyValuePair<string, string>("Tier", Tiers.Slow)
        ];
    }
}
