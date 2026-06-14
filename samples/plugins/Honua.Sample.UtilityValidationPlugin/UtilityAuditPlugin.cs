// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Plugins.Abstractions;

namespace Honua.Sample.UtilityValidationPlugin;

/// <summary>
/// Phase-2 reference plugin (issue #1562). A single type that exercises the new extension points:
/// per-attribute validation (<see cref="IFieldValidator"/>), a query-time computed field
/// (<see cref="IComputedFieldProvider"/>), and a long-running background task
/// (<see cref="IPluginBackgroundService"/>). It declares a dependency on the feature-level
/// <c>utility-validation</c> plugin and requests the <see cref="PluginCapability.BackgroundExecution"/>
/// capability required to host its background service.
/// </summary>
[Plugin("utility-audit", "1.1.0",
    Description = "Field rules, a computed field, and an audit background service for utility assets.",
    Capabilities = PluginCapability.BackgroundExecution,
    DependsOnCsv = "utility-validation@>=1.0.0",
    MinimumServerVersion = "0.1.0")]
public sealed class UtilityAuditPlugin : IFieldValidator, IComputedFieldProvider, IPluginBackgroundService
{
    private const double WarningVoltageThreshold = 25_000d;

    /// <inheritdoc />
    string IFieldValidator.FieldName => "Voltage";

    /// <inheritdoc />
    string IComputedFieldProvider.FieldName => "VoltageClass";

    /// <inheritdoc />
    public ValueTask<PluginValidationResult> ValidateFieldAsync(
        object? value,
        Feature feature,
        PluginValidationContext context,
        CancellationToken cancellationToken)
    {
        if (TryConvert(value, out var voltage) && voltage < 0)
        {
            return ValueTask.FromResult(PluginValidationResult.Error("Voltage cannot be negative"));
        }

        return ValueTask.FromResult(PluginValidationResult.Success());
    }

    /// <inheritdoc />
    public ValueTask<object?> ComputeAsync(
        Feature feature,
        ComputedFieldContext context,
        CancellationToken cancellationToken)
    {
        if (!feature.Attributes.TryGetValue("Voltage", out var raw) || !TryConvert(raw, out var voltage))
        {
            return ValueTask.FromResult<object?>("unknown");
        }

        var voltageClass = voltage >= WarningVoltageThreshold ? "high" : "normal";
        return ValueTask.FromResult<object?>(voltageClass);
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A trivial heartbeat loop standing in for periodic audit/bookkeeping work. It observes
        // cancellation promptly and never throws on shutdown.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static bool TryConvert(object? raw, out double voltage)
    {
        voltage = 0d;
        switch (raw)
        {
            case double d: voltage = d; return true;
            case float f: voltage = f; return true;
            case long l: voltage = l; return true;
            case int i: voltage = i; return true;
            case decimal m: voltage = (double)m; return true;
            case string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                voltage = parsed;
                return true;
            default: return false;
        }
    }
}
