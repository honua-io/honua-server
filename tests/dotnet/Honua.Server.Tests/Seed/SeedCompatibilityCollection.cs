// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests.Seed;

/// <summary>
/// Isolates seed-sequence tests that start their own compatibility containers.
/// </summary>
[CollectionDefinition("SeedCompatibility", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is an xUnit collection definition which requires the Collection suffix")]
public sealed class SeedCompatibilityCollection
{
    // This class has no code. xUnit uses it to keep these container-heavy tests
    // out of the normal parallel collection queue.
}
