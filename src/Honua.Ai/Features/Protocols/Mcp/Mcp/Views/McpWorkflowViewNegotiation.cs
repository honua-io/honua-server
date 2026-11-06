// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Views;

internal sealed class McpWorkflowViewOptions
{
    public const string SectionName = "Mcp:WorkflowViews";

    public string? DefaultView { get; set; } = McpWorkflowViewCatalog.DefaultViewName;
}

internal static class McpWorkflowViewNegotiation
{
    public const string ViewParameterName = "view";

    public const string MetaKey = "honua.io/workflow-view";

    public const string MetaPropertyName = "_meta";

    public const string FullCatalogViewName = "full";

    public const string HttpContextItemKey = "honua.mcp.workflow-view";

    public const string ViewHeaderName = "X-Honua-Workflow-View";

    public static string? ReadHttpView(HttpRequest request) =>
        FirstSet(request.Query[ViewParameterName].ToString(), request.Headers[ViewHeaderName].ToString());

    private const int MaxViewNameLength = 64;

    public static bool TryReadRequestedView(
        JsonElement? parameters,
        out string? requested,
        out string? error)
    {
        requested = null;
        error = null;

        if (parameters is not { ValueKind: JsonValueKind.Object } element)
        {
            return true;
        }

        if (element.TryGetProperty(ViewParameterName, out var direct)
            && !TryReadName(direct, ViewParameterName, out requested, out error))
        {
            return false;
        }

        if (requested is not null)
        {
            return true;
        }

        if (element.TryGetProperty(MetaPropertyName, out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty(MetaKey, out var metaView))
        {
            return TryReadName(metaView, $"{MetaPropertyName}.{MetaKey}", out requested, out error);
        }

        return true;
    }

    public static string? ResolveEffectiveView(
        string? requestView,
        string? sessionView,
        string? profileDefaultView,
        string? httpView = null)
    {
        var selected = FirstSet(requestView, sessionView, httpView, profileDefaultView);
        return IsFullCatalog(selected) ? null : selected;
    }

    public static bool IsFullCatalog(string? name) =>
        name is not null && string.Equals(name, FullCatalogViewName, StringComparison.Ordinal);

    private static string? FirstSet(params string?[] candidates)
    {
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
    }

    private static bool TryReadName(
        JsonElement element,
        string path,
        out string? name,
        out string? error)
    {
        name = null;
        error = null;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"'{path}' must be a string naming a server-published workflow view.";
            return false;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.Length > MaxViewNameLength)
        {
            error = $"'{path}' must be at most {MaxViewNameLength} characters.";
            return false;
        }

        name = value;
        return true;
    }
}
