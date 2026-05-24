// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console;

/// <summary>
/// AOT-safe wire-name parsers for Console enum query parameters.
/// </summary>
internal static class ConsoleEnumParser
{
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
