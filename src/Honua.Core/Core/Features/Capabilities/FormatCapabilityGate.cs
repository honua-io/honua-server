// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

// Every format-negotiation seam consults this gate before dispatch, so a format the registry reports as off is rejected.
public static class FormatCapabilityGate
{
    // A default registry backed by the same static roster CapabilityRegistry.All
    // exposes, so seams that have no registry in scope (the deep formatter/import
    // dispatch paths) can still consult the gate without new DI plumbing. Tests and
    // future DI callers can pass an explicit registry.
    private static readonly CapabilityRegistry SharedRegistry = new();

    // formatName is the registry data-format name (case-insensitive), not a protocol wire token: callers map
    // their wire token (for example GeoServices f=json) to the registry name first.
    public static FormatGateDecision Evaluate(
        string formatName,
        CapabilityGateContext? context = null,
        ICapabilityRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);

        var effectiveRegistry = registry ?? SharedRegistry;
        var effectiveContext = context ?? CapabilityGateContext.Default;
        var capabilityId = CapabilityRegistry.DataFormatIdPrefix + formatName.Trim().ToLowerInvariant();

        return FormatGateDecision.FromResolution(effectiveRegistry.Resolve(capabilityId, effectiveContext));
    }

    public static IReadOnlyList<string> FilterAdvertised(
        IEnumerable<string> formatNames,
        CapabilityGateContext? context = null,
        ICapabilityRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(formatNames);

        var kept = new List<string>();
        foreach (var name in formatNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!Evaluate(name, context, registry).IsBlocked)
            {
                kept.Add(name);
            }
        }

        return kept;
    }
}

public enum FormatGateStatus
{
    Enabled = 0,

    Unknown = 1,

    ExperimentalDisabled = 2,

    LicenseRequired = 3,

    NotImplemented = 4,
}

public readonly record struct FormatGateDecision(FormatGateStatus Status, string? ReasonCode)
{
    public bool IsEnabled => Status == FormatGateStatus.Enabled;

    public bool IsBlocked => Status is FormatGateStatus.ExperimentalDisabled or FormatGateStatus.LicenseRequired or FormatGateStatus.NotImplemented;

    internal static FormatGateDecision FromResolution(CapabilityResolution resolution)
        => resolution.Enabled
            ? new FormatGateDecision(FormatGateStatus.Enabled, null)
            : resolution.ReasonCode switch
            {
                CapabilityReasonCodes.NotImplemented =>
                    new FormatGateDecision(FormatGateStatus.NotImplemented, FormatCapabilityReasonCodes.NotImplemented),
                CapabilityReasonCodes.ExperimentalDisabled =>
                    new FormatGateDecision(FormatGateStatus.ExperimentalDisabled, FormatCapabilityReasonCodes.ExperimentalDisabled),
                CapabilityReasonCodes.LicenseRequired =>
                    new FormatGateDecision(FormatGateStatus.LicenseRequired, FormatCapabilityReasonCodes.LicenseRequired),
                _ => new FormatGateDecision(FormatGateStatus.Unknown, FormatCapabilityReasonCodes.Unknown),
            };
}

public static class FormatCapabilityReasonCodes
{
    public const string NotImplemented = "format-not-implemented";

    public const string Unknown = "format-unknown";

    public const string ExperimentalDisabled = "format-experimental-disabled";

    public const string LicenseRequired = "format-license-required";
}
