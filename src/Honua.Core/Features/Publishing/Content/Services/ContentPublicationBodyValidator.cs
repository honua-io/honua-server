// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Validation.Contracts;

namespace Honua.Core.Features.Publishing.Content.Services;

/// <summary>
/// Field-addressable body validation for report and dashboard content payloads published through the
/// content publication registry. The console serializes the authored report/dashboard document into the
/// request <c>contentPayload</c> (a <c>bindings[]</c> + <c>panels[]</c> graph with embedded Vega-Lite
/// chart specs); this validator parses that document and surfaces the same business rules the console
/// enforces client-side — required bindings (alias + contentRef), unique binding aliases, every panel's
/// <c>bindingAlias</c> referencing a declared binding, and every chart panel declaring a parseable
/// Vega-Lite spec (a JSON object with a vega-lite <c>$schema</c>) — as
/// <see cref="FieldValidationError"/>s keyed by JSON Pointer so they bind back onto the offending input.
/// <para>
/// It is intentionally tolerant: a payload that is absent, not JSON, or not a recognised
/// report/dashboard document is accepted opaquely (the server still hashes the blob), so this never
/// rejects map/generated-app payloads or third-party documents — it only enforces the rules when the
/// document clearly carries the bindings/panels graph.
/// </para>
/// </summary>
internal static class ContentPublicationBodyValidator
{
    /// <summary>
    /// Validates <paramref name="contentPayload"/> for a report/dashboard publish/republish and throws a
    /// <see cref="ContentPublicationValidationException"/> carrying every field-addressable finding when
    /// the document violates a binding/panel/vega-lite rule. No-ops for non-report/dashboard kinds, an
    /// absent payload, or a payload that does not parse as a recognised document.
    /// </summary>
    public static void Validate(ContentPublicationKind kind, string? contentPayload)
    {
        if (kind is not (ContentPublicationKind.Report or ContentPublicationKind.Dashboard))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(contentPayload))
        {
            return;
        }

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(contentPayload);
        }
        catch (JsonException)
        {
            // An unparseable payload is left to the opaque-blob path; the console gates malformed JSON
            // client-side and the server still records the content hash.
            return;
        }

        using (document)
        {
            root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var hasBindings = root.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Array;
            var hasPanels = root.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Array;
            if (!hasBindings && !hasPanels)
            {
                // Not a recognised bindings/panels document; accept opaquely.
                return;
            }

            var errors = new List<FieldValidationError>();
            var aliases = ValidateBindings(hasBindings ? bindings : default, errors);
            if (hasPanels)
            {
                ValidatePanels(panels, aliases, errors);
            }

            if (errors.Count > 0)
            {
                throw new ContentPublicationValidationException(errors);
            }
        }
    }

    private static HashSet<string> ValidateBindings(JsonElement bindings, List<FieldValidationError> errors)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (bindings.ValueKind != JsonValueKind.Array)
        {
            return aliases;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var binding in bindings.EnumerateArray())
        {
            var path = $"/bindings/{index}";
            var alias = GetString(binding, "alias");
            var contentRef = GetString(binding, "contentRef");

            if (string.IsNullOrWhiteSpace(alias))
            {
                errors.Add(FieldValidationError.Create(
                    "publication.binding.alias.required",
                    "Give every data binding an alias.",
                    path: path + "/alias"));
            }
            else
            {
                aliases.Add(alias.Trim());
                if (!seen.Add(alias.Trim()))
                {
                    errors.Add(FieldValidationError.Create(
                        "publication.binding.alias.duplicate",
                        $"Binding alias '{alias.Trim()}' is already used. Binding aliases must be unique.",
                        path: path + "/alias"));
                }
            }

            if (string.IsNullOrWhiteSpace(contentRef))
            {
                errors.Add(FieldValidationError.Create(
                    "publication.binding.contentRef.required",
                    "Give every data binding a content reference.",
                    path: path + "/contentRef"));
            }

            index++;
        }

        return aliases;
    }

    private static void ValidatePanels(JsonElement panels, HashSet<string> aliases, List<FieldValidationError> errors)
    {
        if (panels.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var panel in panels.EnumerateArray())
        {
            var path = $"/panels/{index}";
            var bindingAlias = GetString(panel, "bindingAlias");

            // Every panel must reference a declared binding alias (referential integrity).
            if (string.IsNullOrWhiteSpace(bindingAlias) || !aliases.Contains(bindingAlias.Trim()))
            {
                errors.Add(FieldValidationError.Create(
                    "publication.panel.bindingAlias.unresolved",
                    string.IsNullOrWhiteSpace(bindingAlias)
                        ? "Bind this panel to a declared data binding."
                        : $"Panel binding alias '{bindingAlias.Trim()}' does not reference a declared data binding.",
                    path: path + "/bindingAlias"));
            }

            if (IsChartPanel(panel) && !DeclaresVegaLiteSchema(panel))
            {
                errors.Add(FieldValidationError.Create(
                    "publication.panel.chartSpec.vegaLite",
                    "Chart panels must declare a Vega-Lite spec: a JSON object with a vega-lite \"$schema\".",
                    path: path + "/chartSpec"));
            }

            index++;
        }
    }

    private static bool IsChartPanel(JsonElement panel)
    {
        var kind = GetString(panel, "kind");
        return string.Equals(kind, "chart", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the panel's chart spec is a JSON object declaring a vega-lite <c>$schema</c>. Mirrors the
    /// console's <c>DeclaresVegaLiteSchema</c> helper. The chart spec is embedded either as a structured
    /// object (the console parses valid JSON before publishing) or as a raw string (round-tripped when it
    /// did not parse) — only a structured object with a vega-lite schema URL passes.
    /// </summary>
    private static bool DeclaresVegaLiteSchema(JsonElement panel)
    {
        if (!panel.TryGetProperty("chartSpec", out var spec))
        {
            return false;
        }

        // A raw (unparsed) chart spec round-trips as a JSON string; that is not a declared Vega-Lite object.
        if (spec.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!spec.TryGetProperty("$schema", out var schema) || schema.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var schemaUrl = schema.GetString();
        return !string.IsNullOrEmpty(schemaUrl)
            && schemaUrl.Contains("vega-lite", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}
