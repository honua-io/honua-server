// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Constants;

/// <summary>
/// Test execution tiers used by the unified CI test strategy. Maps to
/// <c>Trait("Tier", ...)</c> emitted by the TestKit attributes and consumed
/// by GitHub Actions <c>--filter</c> expressions. See
/// <c>docs/contributor/adr/0037-unified-ci-test-tier-strategy.md</c>.
/// </summary>
public static class Tiers
{
    /// <summary>
    /// Fast unit tests with no external dependencies. Runs on every PR.
    /// Target wall-clock budget: under 10 minutes total across all projects.
    /// </summary>
    public const string Fast = "Fast";

    /// <summary>
    /// Integration tests that exercise real databases, HTTP, and in-process
    /// services. Runs on the merge-to-trunk push event and on PRs scoped to
    /// the targeted shards from <c>scripts/honua-server-targeted-tests.sh</c>.
    /// </summary>
    public const string Integration = "Integration";

    /// <summary>
    /// Slow tests that require external services, emulators, scale runners,
    /// or long-running performance/comprehensive harnesses. Runs nightly via
    /// <c>.github/workflows/nightly-slow-tier.yml</c>.
    /// </summary>
    public const string Slow = "Slow";
}
