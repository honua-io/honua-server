// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as a known intermittent (flaky) candidate. The test still
/// executes — quarantining is a reporting concern handled by the
/// <c>flaky-detection.yml</c> workflow, which surfaces failures separately
/// from regressions and never auto-skips coverage. See ADR-0037.
/// </summary>
/// <remarks>
/// Pair with <see cref="IntegrationTestAttribute"/> or another tier-emitting
/// attribute on the same method. The flaky trait is additive; it does not
/// change tier scheduling. Always include the reason and a tracking issue
/// reference.
/// </remarks>
[TraitDiscoverer("Honua.TestKit.Attributes.FlakyTestDiscoverer", "Honua.TestKit")]
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FlakyTestAttribute : Attribute, ITraitAttribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="FlakyTestAttribute"/>.
    /// </summary>
    /// <param name="reason">
    /// Human-readable reason. Must include a tracking issue reference
    /// (e.g. <c>"Testcontainers startup race — tracked in #812"</c>).
    /// </param>
    public FlakyTestAttribute(string reason)
    {
        Reason = reason;
    }

    /// <summary>
    /// Reason the test is quarantined as flaky.
    /// </summary>
    public string Reason { get; }
}

public sealed class FlakyTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return [new KeyValuePair<string, string>("Flaky", "true")];
    }
}
