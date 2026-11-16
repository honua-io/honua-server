// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Server.Features.Streaming;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// Side observations of one two-host delivery run: when task B's relay moved, and the raw
// feature notifications on the database, so a missed latency target can be located.
internal static class TwoHostDeliveryProbes
{
    // Records when task B's feature relay first reached each position, sampled every few
    // milliseconds; the relay position is the generation it last handed to local sessions.
    internal static async Task<List<(long Position, long At)>> SampleRelayAsync(
        FeatureStreamRealtimeRelay relay, CancellationToken cancellationToken)
    {
        var samples = new List<(long Position, long At)>();
        var last = relay.Position;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var position = relay.Position;
                if (position != last)
                {
                    samples.Add((position, Stopwatch.GetTimestamp()));
                    last = position;
                }
                await Task.Delay(2, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Sampling ends with delivery.
        }
        return samples;
    }

    internal static async Task ProbeNotificationsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await connection.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Probing ends with delivery.
        }
    }

    internal static string DescribeRelay(List<(long Position, long At)> samples)
        => samples.Count == 0
            ? "never"
            : $"first {samples[0].Position}, last {samples[^1].Position}, " +
              $"largest step {samples.Zip(samples.Skip(1), (a, b) => b.Position - a.Position).DefaultIfEmpty(samples[0].Position).Max()}";

    internal static long RelayedAt(List<(long Position, long At)> samples, FeatureFrame frame)
        => samples.FirstOrDefault(sample => sample.Position >= frame.Generation) is { At: > 0 } sample
            ? sample.At
            : frame.ReceivedAt;
}
