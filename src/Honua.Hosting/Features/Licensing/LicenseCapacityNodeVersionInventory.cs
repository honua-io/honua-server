// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;

namespace Honua.Infrastructure.Licensing;

/// <summary>
/// Supplies the migration node-version barrier (#2812) with a live cluster version snapshot by composing
/// the existing coordinated live-node inventory the <see cref="LicenseCapacityMeter"/> already maintains
/// (a TTL'd, heartbeated Redis set of serving instances). No new coordination primitive is introduced:
/// each serving node already advertises its binary version into the meter, so the minimum-active-version
/// signal is read straight off that inventory.
/// </summary>
internal sealed class LicenseCapacityNodeVersionInventory : IActiveNodeVersionInventory
{
    private readonly LicenseCapacityMeter _meter;

    public LicenseCapacityNodeVersionInventory(LicenseCapacityMeter meter)
    {
        _meter = meter;
    }

    /// <inheritdoc />
    public async Task<ActiveNodeVersionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var reading = await _meter.GetActiveNodeVersionsAsync(cancellationToken).ConfigureAwait(false);
        return BuildSnapshot(reading);
    }

    /// <summary>
    /// Maps a meter reading to a barrier snapshot. Other-node versions exclude this instance and any node
    /// whose version is unknown, so a mixed-version verdict is only ever raised from a positively observed
    /// divergent version — never from a node that simply did not advertise one (an older binary from
    /// before version advertising shipped). This keeps the barrier free of upgrade-wedging false positives.
    /// </summary>
    internal static ActiveNodeVersionSnapshot BuildSnapshot(ActiveNodeVersionReading reading)
    {
        if (!reading.Coordinated)
        {
            return ActiveNodeVersionSnapshot.NotCoordinated;
        }

        var otherVersions = reading.Instances
            .Where(entry => !string.Equals(entry.InstanceId, reading.LocalInstanceId, StringComparison.Ordinal))
            .Select(entry => entry.Version)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version!)
            .ToArray();

        return new ActiveNodeVersionSnapshot
        {
            Coordinated = true,
            LocalVersion = reading.LocalVersion,
            OtherActiveVersions = otherVersions,
        };
    }
}
