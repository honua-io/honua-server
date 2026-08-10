// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Plugins.Abstractions;

namespace Honua.Sample.UtilityValidationPlugin;

/// <summary>
/// Example feature validator plugin (issue #347). Demonstrates a custom business rule:
/// high-voltage transformers require special approval before they can be created or updated.
/// This is the reference example from the plugin SDK ticket.
/// </summary>
[Plugin("utility-validation", "1.0.0",
    Description = "Rejects high-voltage transformer assets that require special approval.")]
public sealed class UtilityValidationPlugin : IFeatureValidator
{
    private const double HighVoltageThreshold = 50_000d;

    /// <inheritdoc />
    public ValueTask<PluginValidationResult> ValidateAsync(
        Feature feature,
        PluginValidationContext context,
        CancellationToken cancellationToken)
    {
        if (IsTransformer(feature) && TryGetVoltage(feature, out var voltage) && voltage > HighVoltageThreshold)
        {
            return ValueTask.FromResult(
                PluginValidationResult.Error("High voltage transformers require special approval"));
        }

        return ValueTask.FromResult(PluginValidationResult.Success());
    }

    private static bool IsTransformer(Feature feature)
        => feature.Attributes.TryGetValue("AssetType", out var assetType)
            && assetType is string text
            && string.Equals(text, "Transformer", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetVoltage(Feature feature, out double voltage)
    {
        voltage = 0d;
        if (!feature.Attributes.TryGetValue("Voltage", out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case double d:
                voltage = d;
                return true;
            case float f:
                voltage = f;
                return true;
            case long l:
                voltage = l;
                return true;
            case int i:
                voltage = i;
                return true;
            case decimal m:
                voltage = (double)m;
                return true;
            case string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                voltage = parsed;
                return true;
            default:
                return false;
        }
    }
}
