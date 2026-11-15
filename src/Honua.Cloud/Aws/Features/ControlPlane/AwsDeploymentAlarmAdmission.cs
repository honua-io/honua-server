// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal interface IAwsDeploymentAlarmAdmission
{
    Task<string?> GetBlockingReasonAsync(string targetId, CancellationToken cancellationToken);
}

internal sealed class AwsDeploymentAlarmOptions
{
    public const string SectionName = "ControlPlane:AwsDeploymentAlarms";

    public bool Enabled { get; set; }

    public Dictionary<string, AwsDeploymentAlarmTarget> Targets { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class AwsDeploymentAlarmTarget
{
    public Dictionary<string, string[]> Regions { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record AwsDeploymentAlarmState(string Name, string State);

internal sealed record AwsDeploymentAlarmSnapshot
{
    public required IReadOnlyList<AwsDeploymentAlarmState> Alarms { get; init; }

    public bool IsComplete { get; init; }
}

internal interface IAwsDeploymentAlarmClient
{
    Task<AwsDeploymentAlarmSnapshot> ReadAsync(
        string region, IReadOnlyList<string> names, CancellationToken cancellationToken);
}

internal sealed class AwsDeploymentAlarmAdmission : IAwsDeploymentAlarmAdmission
{
    private const string BlockedReason = "Deployment alarms are unavailable or not all OK; retry after they recover.";
    private readonly bool _enabled;
    private readonly ImmutableDictionary<string, ImmutableArray<AlarmRegion>> _targets;
    private readonly IAwsDeploymentAlarmClient _client;

    public AwsDeploymentAlarmAdmission(IOptions<AwsDeploymentAlarmOptions> options, IAwsDeploymentAlarmClient client)
    {
        _enabled = options.Value.Enabled;
        _client = client;
        _targets = options.Value.Targets.ToImmutableDictionary(
            target => target.Key, target => SnapshotRegions(target.Value), StringComparer.Ordinal);
    }

    public async Task<string?> GetBlockingReasonAsync(string targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled)
        {
            return null;
        }
        if (!_targets.TryGetValue(targetId, out var regions) || regions.IsDefaultOrEmpty)
        {
            return BlockedReason;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var region in regions)
            {
                var snapshot = await _client.ReadAsync(region.Name, region.Alarms, deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                if (!AllAlarmsOk(region.Alarms, snapshot))
                {
                    return BlockedReason;
                }
            }
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return BlockedReason;
        }
    }

    private static ImmutableArray<AlarmRegion> SnapshotRegions(AwsDeploymentAlarmTarget target)
    {
        if (target?.Regions is not { Count: > 0 and <= 8 } regions ||
            regions.Any(region => !ValidRegion(region.Key) || !ValidNames(region.Value)))
        {
            return [];
        }
        return regions.Select(region => new AlarmRegion(region.Key, region.Value.ToImmutableArray()))
            .ToImmutableArray();
    }

    private static bool ValidRegion(string region)
        => region.Length is > 0 and <= 64 &&
           region.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private static bool ValidNames(string[] names)
        => names is { Length: > 0 and <= 100 } &&
           names.All(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 255) &&
           names.Distinct(StringComparer.Ordinal).Count() == names.Length;

    private static bool AllAlarmsOk(ImmutableArray<string> names, AwsDeploymentAlarmSnapshot snapshot)
        => snapshot is { IsComplete: true } && snapshot.Alarms.Count == names.Length &&
           snapshot.Alarms.Select(alarm => alarm.Name).Distinct(StringComparer.Ordinal).Count() == names.Length &&
           snapshot.Alarms.All(alarm => names.Contains(alarm.Name, StringComparer.Ordinal) && alarm.State == "OK");

    private sealed record AlarmRegion(string Name, ImmutableArray<string> Alarms);
}
