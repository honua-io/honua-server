// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// Host-tunable workflow-view options, bound from the <c>Mcp:WorkflowViews</c>
/// configuration section. This is the <b>server/profile</b> leg of the view
/// negotiation contract (honua-server#3428).
/// </summary>
internal sealed class McpWorkflowViewOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Mcp:WorkflowViews";

    /// <summary>
    /// The view a session gets when it negotiates none and asks for none.
    /// The bounded discovery view is the secure default. Set this explicitly to
    /// another published task view to select it for otherwise-unnegotiated sessions.
    /// </summary>
    public string? DefaultView { get; set; } = McpWorkflowViewCatalog.DefaultViewName;
}

/// <summary>
/// The explicit server/session/request view-negotiation contract
/// (honua-server#3428).
/// </summary>
/// <remarks>
/// <para>
/// Three legs, highest precedence first:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Request</b> — <c>tools/list</c> params carry <c>"view": "&lt;name&gt;"</c>
/// (or <c>params._meta["honua.io/workflow-view"]</c>). Per-call and explicit.
/// </description></item>
/// <item><description>
/// <b>Session</b> — <c>initialize</c> params carry
/// <c>_meta["honua.io/workflow-view"]</c>; the negotiated name is bound to the
/// session the transport issues and applies to every later <c>tools/list</c> on
/// that session.
/// </description></item>
/// <item><description>
/// <b>Server profile</b> — <see cref="McpWorkflowViewOptions.DefaultView"/>.
/// </description></item>
/// </list>
/// <para>
/// Selecting a view can only <em>narrow discovery</em>. It never grants,
/// caches, or implies authority, and it never hides the escape hatch: a
/// <c>tools/list</c> that selects no view — or explicitly selects
/// <see cref="FullCatalogViewName"/> — requests the explicit authenticated complete
/// catalog export.
/// </para>
/// </remarks>
internal static class McpWorkflowViewNegotiation
{
    /// <summary>The <c>tools/list</c> params property naming a view.</summary>
    public const string ViewParameterName = "view";

    /// <summary>
    /// The reserved <c>_meta</c> key carrying a view name on <c>initialize</c> and
    /// on <c>tools/list</c>.
    /// </summary>
    public const string MetaKey = "honua.io/workflow-view";

    /// <summary>The MCP <c>_meta</c> envelope property name.</summary>
    public const string MetaPropertyName = "_meta";

    /// <summary>
    /// Reserved name a client sends to explicitly opt back out of any negotiated
    /// or configured view and request the authenticated complete paginated catalog.
    /// </summary>
    public const string FullCatalogViewName = "full";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which the transport records the
    /// view negotiated for the current request — written by <c>initialize</c>
    /// from the client's <c>_meta</c>, and rehydrated from the bound session on
    /// every later request. Request-scoped, mirroring the elicitation-capability
    /// seam.
    /// </summary>
    public const string HttpContextItemKey = "honua.mcp.workflow-view";

    /// <summary>Maximum accepted view-name length.</summary>
    private const int MaxViewNameLength = 64;

    /// <summary>
    /// Reads the view a request explicitly selects from a JSON-RPC params object,
    /// accepting either <c>view</c> or <c>_meta["honua.io/workflow-view"]</c>.
    /// </summary>
    /// <param name="parameters">The raw JSON-RPC params element, if any.</param>
    /// <param name="requested">
    /// The requested view name, or <c>null</c> when the request selects none.
    /// </param>
    /// <param name="error">
    /// A client-facing message when the request carried a malformed selector.
    /// </param>
    /// <returns><c>false</c> only when the selector itself is malformed.</returns>
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

    /// <summary>
    /// Resolves the effective view for a <c>tools/list</c> call from the three
    /// negotiation legs. Returns <c>null</c> when no view applies, which is the
    /// full-catalog escape hatch.
    /// </summary>
    /// <param name="requestView">The per-request selection, if any.</param>
    /// <param name="sessionView">The view negotiated at <c>initialize</c>, if any.</param>
    /// <param name="profileDefaultView">The server-profile default, if any.</param>
    public static string? ResolveEffectiveView(
        string? requestView,
        string? sessionView,
        string? profileDefaultView)
    {
        var selected = FirstSet(requestView, sessionView, profileDefaultView);
        return IsFullCatalog(selected) ? null : selected;
    }

    /// <summary>Whether <paramref name="name"/> is the reserved full-catalog name.</summary>
    public static bool IsFullCatalog(string? name) =>
        name is not null && string.Equals(name, FullCatalogViewName, StringComparison.Ordinal);

    private static string? FirstSet(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
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
