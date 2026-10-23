// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Performance;

/// <summary>
/// Defines load profile settings for NBomber-based load/soak tests.
/// </summary>
public sealed record LoadTestProfile(
    TimeSpan RampUp,
    TimeSpan Duration,
    TimeSpan RampDown,
    int FeatureQueryUsers,
    int SpatialQueryUsers,
    int OgcFeaturesUsers,
    int CqlUsers,
    int ConnectionPoolUsers,
    int MemoryStressUsers,
    int ODataUsers,
    int TilesUsers)
{
    public static LoadTestProfile Quick { get; } = new(
        RampUp: TimeSpan.FromSeconds(20),
        Duration: TimeSpan.FromMinutes(2),
        RampDown: TimeSpan.FromSeconds(20),
        FeatureQueryUsers: 10,
        SpatialQueryUsers: 6,
        OgcFeaturesUsers: 8,
        CqlUsers: 4,
        ConnectionPoolUsers: 30,
        MemoryStressUsers: 3,
        ODataUsers: 6,
        TilesUsers: 6);

    public static LoadTestProfile Nightly { get; } = new(
        RampUp: TimeSpan.FromMinutes(2),
        Duration: TimeSpan.FromMinutes(10),
        RampDown: TimeSpan.FromMinutes(1),
        FeatureQueryUsers: 30,
        SpatialQueryUsers: 15,
        OgcFeaturesUsers: 20,
        CqlUsers: 10,
        ConnectionPoolUsers: 60,
        MemoryStressUsers: 5,
        ODataUsers: 15,
        TilesUsers: 15);

    /// <summary>
    /// The soak profile named by the frozen 2026.1 capacity lock
    /// (<c>honua-release certification/capacity-envelope.v1.json</c>, <c>soak.profile</c>).
    /// </summary>
    /// <remarks>
    /// The virtual-user mix is the lock's declared <c>supportedEnvelope.concurrentVirtualUsers</c>
    /// (170), held for at least the locked <c>minimumSteadyStateSeconds</c> (3,600). It is
    /// deliberately the same mix the frozen thresholds were derived from — the baseline in
    /// <c>docs/CAPACITY-ENVELOPE-2026.1.md</c> is a 170-user run — so a soak measures the declared
    /// envelope rather than a different, heavier one that the frozen p95/p99/throughput limits were
    /// never taken against. Changing these counts changes what the receipt claims: keep
    /// <see cref="TotalVirtualUsers"/> equal to the lock's declared concurrency.
    /// </remarks>
    public static LoadTestProfile Soak { get; } = new(
        RampUp: TimeSpan.FromMinutes(5),
        Duration: TimeSpan.FromMinutes(60),
        RampDown: TimeSpan.FromMinutes(2),
        FeatureQueryUsers: 30,
        SpatialQueryUsers: 15,
        OgcFeaturesUsers: 20,
        CqlUsers: 10,
        ConnectionPoolUsers: 60,
        MemoryStressUsers: 5,
        ODataUsers: 15,
        TilesUsers: 15);

    /// <summary>
    /// Total concurrent virtual users this profile holds during steady state: the sum of every
    /// scenario's copy count. This is the number a capacity envelope declares as
    /// <c>concurrentVirtualUsers</c>.
    /// </summary>
    public int TotalVirtualUsers =>
        FeatureQueryUsers + SpatialQueryUsers + OgcFeaturesUsers + CqlUsers
        + ConnectionPoolUsers + MemoryStressUsers + ODataUsers + TilesUsers;

    public static LoadTestProfile FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Quick;
        }

        return name.Trim().ToLowerInvariant() switch
        {
            "quick" => Quick,
            "nightly" => Nightly,
            "soak" => Soak,
            _ => Quick
        };
    }

    public LoadTestProfile WithDuration(TimeSpan duration) => this with { Duration = duration };

    public LoadTestProfile WithRampUp(TimeSpan rampUp) => this with { RampUp = rampUp };

    public LoadTestProfile WithRampDown(TimeSpan rampDown) => this with { RampDown = rampDown };
}
