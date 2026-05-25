// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Console;

/// <summary>
/// AOT-safe wire-name parsers and value whitelist checks for Console enum
/// query and body parameters. Switch-based to stay reflection-free under
/// trimming/Native AOT.
/// </summary>
internal static class ConsoleEnumParser
{
    /// <summary>
    /// Returns true when the value is a defined member of the enum. Used to
    /// reject undefined numeric values (e.g. <c>itemType: 999</c>) that the
    /// default <c>JsonStringEnumConverter</c> would otherwise accept.
    /// </summary>
    public static bool IsDefined(ConsoleContentItemType value) => value switch
    {
        ConsoleContentItemType.Service
            or ConsoleContentItemType.Layer
            or ConsoleContentItemType.SavedMap
            or ConsoleContentItemType.Dashboard
            or ConsoleContentItemType.Report
            or ConsoleContentItemType.GeneratedApp
            or ConsoleContentItemType.OpenData => true,
        _ => false,
    };

    /// <inheritdoc cref="IsDefined(ConsoleContentItemType)" />
    public static bool IsDefined(ConsoleVisibility value) => value switch
    {
        ConsoleVisibility.Personal
            or ConsoleVisibility.Team
            or ConsoleVisibility.Organization
            or ConsoleVisibility.Public => true,
        _ => false,
    };

    /// <inheritdoc cref="IsDefined(ConsoleContentItemType)" />
    public static bool IsDefined(ConsoleContentAction value) => value switch
    {
        ConsoleContentAction.View
            or ConsoleContentAction.Edit
            or ConsoleContentAction.Publish
            or ConsoleContentAction.Share
            or ConsoleContentAction.Embed
            or ConsoleContentAction.Operate
            or ConsoleContentAction.Administer => true,
        _ => false,
    };

    /// <inheritdoc cref="IsDefined(ConsoleContentItemType)" />
    public static bool IsDefined(MetadataV2LifecycleStatus value) => value switch
    {
        MetadataV2LifecycleStatus.Draft
            or MetadataV2LifecycleStatus.Active
            or MetadataV2LifecycleStatus.Deprecated
            or MetadataV2LifecycleStatus.Retired
            or MetadataV2LifecycleStatus.Archived => true,
        _ => false,
    };

    /// <inheritdoc cref="IsDefined(ConsoleContentItemType)" />
    public static bool IsDefined(MetadataV2OperationalState value) => value switch
    {
        MetadataV2OperationalState.Unknown
            or MetadataV2OperationalState.Ready
            or MetadataV2OperationalState.Pending
            or MetadataV2OperationalState.Degraded
            or MetadataV2OperationalState.Failed => true,
        _ => false,
    };

    public static bool TryParse(string raw, out ConsoleContentItemType value)
    {
        switch (raw)
        {
            case "service":
            case "Service":
                value = ConsoleContentItemType.Service; return true;
            case "layer":
            case "Layer":
                value = ConsoleContentItemType.Layer; return true;
            case "saved-map":
            case "SavedMap":
            case "savedMap":
                value = ConsoleContentItemType.SavedMap; return true;
            case "dashboard":
            case "Dashboard":
                value = ConsoleContentItemType.Dashboard; return true;
            case "report":
            case "Report":
                value = ConsoleContentItemType.Report; return true;
            case "generated-app":
            case "GeneratedApp":
            case "generatedApp":
                value = ConsoleContentItemType.GeneratedApp; return true;
            case "open-data":
            case "OpenData":
            case "openData":
                value = ConsoleContentItemType.OpenData; return true;
            default:
                value = default;
                return false;
        }
    }

    public static bool TryParse(string raw, out ConsoleVisibility value)
    {
        switch (raw)
        {
            case "personal":
            case "Personal":
                value = ConsoleVisibility.Personal; return true;
            case "team":
            case "Team":
                value = ConsoleVisibility.Team; return true;
            case "organization":
            case "Organization":
                value = ConsoleVisibility.Organization; return true;
            case "public":
            case "Public":
                value = ConsoleVisibility.Public; return true;
            default:
                value = default;
                return false;
        }
    }

    public static bool TryParse(string raw, out ConsoleContentAction value)
    {
        switch (raw)
        {
            case "view":
            case "View":
                value = ConsoleContentAction.View; return true;
            case "edit":
            case "Edit":
                value = ConsoleContentAction.Edit; return true;
            case "publish":
            case "Publish":
                value = ConsoleContentAction.Publish; return true;
            case "share":
            case "Share":
                value = ConsoleContentAction.Share; return true;
            case "embed":
            case "Embed":
                value = ConsoleContentAction.Embed; return true;
            case "operate":
            case "Operate":
                value = ConsoleContentAction.Operate; return true;
            case "administer":
            case "Administer":
                value = ConsoleContentAction.Administer; return true;
            default:
                value = default;
                return false;
        }
    }
}
