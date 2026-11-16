// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit.Performance;

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
