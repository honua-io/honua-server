// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Shared constants for the cloud-integration trait taxonomy (#2163). Every test in this
/// project is tagged <c>[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CloudIntegration)]</c>
/// so the default PR test run — which filters <c>Category!=CloudIntegration</c> on the
/// Honua.Server.Tests projects and never invokes <c>dotnet test</c> against this project —
/// excludes it. The dedicated cloud-integration workflow opts in with
/// <c>--filter "Category=CloudIntegration"</c>.
/// </summary>
internal static class CloudIntegrationTraits
{
    /// <summary>
    /// xUnit trait key. Mirrors the <c>Category</c> key used by the rest of the suite so a
    /// single <c>Category=CloudIntegration</c> / <c>Category!=CloudIntegration</c> filter
    /// governs inclusion and exclusion uniformly.
    /// </summary>
    public const string Category = "Category";

    /// <summary>
    /// Trait value applied to every Docker-backed cloud-integration test.
    /// </summary>
    public const string CloudIntegration = "CloudIntegration";

    /// <summary>
    /// Trait key marking a test as requiring the paid LocalStack Pro tier (AWS Batch / ECS
    /// emulation). Combined with the <see cref="CloudIntegration"/> category so the optional
    /// Pro lane can be selected with <c>Category=CloudIntegration&amp;Tier=Pro</c> and the free
    /// lane can exclude it with <c>Tier!=Pro</c>.
    /// </summary>
    public const string Tier = "Tier";

    /// <summary>
    /// Trait value for tests that require a LocalStack Pro auth token. These skip (not fail)
    /// when <c>LOCALSTACK_AUTH_TOKEN</c> is absent so the default free CI tier stays green.
    /// </summary>
    public const string Pro = "Pro";

    /// <summary>
    /// Trait value for the free default tier (kind + LocalStack Community).
    /// </summary>
    public const string Free = "Free";
}
