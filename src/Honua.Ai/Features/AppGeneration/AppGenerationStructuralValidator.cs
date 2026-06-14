// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.AppGeneration;

/// <summary>
/// Structural, DB-independent validator for a generated <c>studio-app/v1</c> app package body. The
/// Studio publish path resolves each page component's content binding (<c>content:ref@vN</c>) against
/// the live content store and the share policy against workspace policy — data that is unknowable from a
/// natural-language prompt and is bound when the app is published, not when it is generated. So, exactly
/// like <see cref="Honua.Ai.MapGeneration.MapGenerationStructuralValidator"/> (which defers layer/style/
/// source binding to publish), this validator checks only what is knowable from the prompt: a title, a
/// non-empty page list with unique non-empty routes, component kinds from the allowed set, actions whose
/// permissions are valid and whose pageRoute references an existing page (deferred when it does not), and
/// a valid share-policy visibility tier.
///
/// The body is the console's opaque <c>studio-app/v1</c> envelope, so validation reads directly from the
/// <see cref="JsonElement"/> with a null/shape guard on EVERY collection and property access (the map
/// NRE lesson) — there is no typed model to lean on. It emits issues using the same result/issue shape
/// the map family uses and tags runtime-binding gaps with "*NotResolved" codes the generation gate
/// tolerates.
/// </summary>
internal static class AppGenerationStructuralValidator
{
    /// <summary>The server-canonical app package body schema version.</summary>
    internal const string AppPackageSchemaVersion = "studio-app/v1";

    /// <summary>Component kinds a page may render (mirrors the console's StudioAppPackageMapper vocabulary).</summary>
    internal static readonly string[] ComponentKinds =
    [
        "map", "dashboard", "form", "report", "table"
    ];

    /// <summary>Action permission tiers (mirrors the console's requiredPermission vocabulary).</summary>
    internal static readonly string[] Permissions =
    [
        "viewer", "editor", "operator"
    ];

    /// <summary>Share-policy visibility tiers (mirrors the console's sharePolicy.visibility vocabulary).</summary>
    internal static readonly string[] Visibilities =
    [
        "private", "workspace", "organization", "public"
    ];

    private static readonly HashSet<string> ComponentKindSet = new(ComponentKinds, StringComparer.Ordinal);
    private static readonly HashSet<string> PermissionSet = new(Permissions, StringComparer.Ordinal);
    private static readonly HashSet<string> VisibilitySet = new(Visibilities, StringComparer.Ordinal);

    public static AppPackageValidationResult Validate(JsonElement? app)
    {
        var issues = new List<AppValidationIssue>();
        if (app is not { } body || body.ValueKind != JsonValueKind.Object)
        {
            issues.Add(Error("appMissing", "/", "The app package body is missing or is not an object."));
            return Result(issues);
        }

        // title — required, non-empty.
        if (!TryGetNonEmptyString(body, "title", out _))
        {
            issues.Add(Error("titleMissing", "/title", "title is required."));
        }

        ValidatePages(body, issues, out var routes);
        ValidateActions(body, issues, routes);
        ValidateSharePolicy(body, issues);

        return Result(issues);
    }

    private static void ValidatePages(JsonElement body, List<AppValidationIssue> issues, out HashSet<string> routes)
    {
        routes = new HashSet<string>(StringComparer.Ordinal);

        if (!body.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            issues.Add(Error("pagesEmpty", "/pages", "An app must declare at least one page."));
            return;
        }

        var count = pages.GetArrayLength();
        if (count == 0)
        {
            issues.Add(Error("pagesEmpty", "/pages", "An app must declare at least one page."));
            return;
        }

        var index = 0;
        foreach (var page in pages.EnumerateArray())
        {
            var path = $"/pages/{index}";
            if (page.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Error("pageInvalid", path, "Each page must be an object."));
                index++;
                continue;
            }

            // route — required, non-empty, unique.
            if (!TryGetNonEmptyString(page, "route", out var route))
            {
                issues.Add(Error("pageRouteMissing", $"{path}/route", "Each page must declare a non-empty route."));
            }
            else if (!routes.Add(route))
            {
                issues.Add(Error("pageRouteDuplicate", $"{path}/route", $"page route '{route}' is duplicated."));
            }

            // component.kind — required, from the allowed set.
            if (!page.TryGetProperty("component", out var component) || component.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Error("componentMissing", $"{path}/component", "Each page must declare a component."));
            }
            else
            {
                if (!TryGetNonEmptyString(component, "kind", out var kind))
                {
                    issues.Add(Error("componentKindMissing", $"{path}/component/kind", "component.kind is required."));
                }
                else if (!ComponentKindSet.Contains(kind))
                {
                    issues.Add(Error(
                        "componentKindInvalid",
                        $"{path}/component/kind",
                        $"component.kind '{kind}' must be one of: {string.Join(", ", ComponentKinds)}."));
                }

                // binding (content:ref@vN) is resolved at publish, not generation — never block on it.
                if (!TryGetNonEmptyString(component, "binding", out _))
                {
                    issues.Add(Warning(
                        "bindingNotResolved",
                        $"{path}/component/binding",
                        "component binding is not yet bound; content refs resolve at publish."));
                }
            }

            index++;
        }
    }

    private static void ValidateActions(JsonElement body, List<AppValidationIssue> issues, HashSet<string> routes)
    {
        if (!body.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            // actions are optional.
            return;
        }

        var index = 0;
        foreach (var action in actions.EnumerateArray())
        {
            var path = $"/actions/{index}";
            if (action.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Error("actionInvalid", path, "Each action must be an object."));
                index++;
                continue;
            }

            if (!TryGetNonEmptyString(action, "name", out _))
            {
                issues.Add(Error("actionNameMissing", $"{path}/name", "action.name is required."));
            }

            // requiredPermission — from the allowed set when present.
            if (TryGetNonEmptyString(action, "requiredPermission", out var permission)
                && !PermissionSet.Contains(permission))
            {
                issues.Add(Error(
                    "actionPermissionInvalid",
                    $"{path}/requiredPermission",
                    $"action.requiredPermission '{permission}' must be one of: {string.Join(", ", Permissions)}."));
            }

            // pageRoute must reference an existing page route; tolerated (deferred) when it does not so a
            // prompt that names a route descriptively still generates and binds at publish.
            if (TryGetNonEmptyString(action, "pageRoute", out var pageRoute)
                && routes.Count > 0 && !routes.Contains(pageRoute))
            {
                issues.Add(Warning(
                    "actionPageNotFound",
                    $"{path}/pageRoute",
                    $"action references unknown page route '{pageRoute}'; bound at publish."));
            }

            index++;
        }
    }

    private static void ValidateSharePolicy(JsonElement body, List<AppValidationIssue> issues)
    {
        if (!body.TryGetProperty("sharePolicy", out var policy) || policy.ValueKind != JsonValueKind.Object)
        {
            // sharePolicy is optional; the console defaults it on publish.
            return;
        }

        if (TryGetNonEmptyString(policy, "visibility", out var visibility) && !VisibilitySet.Contains(visibility))
        {
            issues.Add(Error(
                "visibilityInvalid",
                "/sharePolicy/visibility",
                $"sharePolicy.visibility '{visibility}' must be one of: {string.Join(", ", Visibilities)}."));
        }
    }

    private static bool TryGetNonEmptyString(JsonElement parent, string property, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static AppPackageValidationResult Result(List<AppValidationIssue> issues) => new()
    {
        IsValid = !issues.Any(static i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase)),
        Issues = issues.ToArray(),
    };

    private static AppValidationIssue Error(string code, string path, string message) =>
        new() { Code = code, Severity = "error", Path = path, Message = message };

    private static AppValidationIssue Warning(string code, string path, string message) =>
        new() { Code = code, Severity = "warning", Path = path, Message = message };
}

/// <summary>Validation result for a generated app package, mirroring <c>MapPackageValidationResult</c>.</summary>
public sealed class AppPackageValidationResult
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; init; }

    [JsonPropertyName("issues")]
    public AppValidationIssue[] Issues { get; init; } = [];
}

/// <summary>Single app-package validation issue, mirroring <c>MapValidationIssue</c>.</summary>
public sealed class AppValidationIssue
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
