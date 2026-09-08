// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a unit test that shells out to a GNU/Linux toolchain — Bash plus the coreutils
/// the shipped operations scripts call (<c>sha256sum</c>, GNU <c>sort</c>, <c>paste</c>) —
/// and reports it as skipped on every other platform.
/// </summary>
/// <remarks>
/// Tier=Fast, so the PR gate still runs it: every CI job is <c>ubuntu-latest</c>. The skip
/// only takes effect on a contributor's Windows or macOS box, where the documented local
/// <c>--filter "Tier=Fast"</c> run (AGENTS.md) would otherwise fail on the toolchain rather
/// than on product behavior — a Windows host hands the script a <c>C:\…</c> path its
/// absolute-POSIX-root check rejects, and macOS ships <c>shasum</c> rather than
/// <c>sha256sum</c>. Use <see cref="UnitTestAttribute"/> for portable unit tests.
/// See ADR-0037.
/// </remarks>
[TraitDiscoverer("Honua.TestKit.Attributes.GnuLinuxUnitTestDiscoverer", "Honua.TestKit")]
public sealed class GnuLinuxUnitTestAttribute : FactAttribute, ITraitAttribute
{
    public GnuLinuxUnitTestAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires a GNU/Linux shell toolchain (bash plus coreutils); the PR gate runs this on ubuntu-latest.";
        }
    }
}

public sealed class GnuLinuxUnitTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Unit"),
            new KeyValuePair<string, string>("Tier", Tiers.Fast)
        ];
    }
}
