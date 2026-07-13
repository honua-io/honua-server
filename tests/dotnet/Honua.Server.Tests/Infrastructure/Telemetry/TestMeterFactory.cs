// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;

namespace Honua.Server.Tests.Infrastructure.Telemetry;

/// <summary>
/// Minimal, self-contained <see cref="IMeterFactory"/> for unit tests. Each instance owns the
/// meters it creates and disposes them, giving every test a fully isolated meter graph so
/// instrument observations never leak across parallel tests (#2802). Metrics classes under test
/// create their instruments from this factory; the test filters a <see cref="MeterListener"/> to
/// the specific meter instance the class exposes for deterministic, poll-free assertions.
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = new();

    /// <inheritdoc />
    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The factory owns the meter's scope so consumers must not dispose it directly.
        options.Scope = this;
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }

        _meters.Clear();
    }
}
