// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Validation.Contracts;

namespace Honua.Core.Features.Publishing.Content.Services;

internal static class ContentPublicationBodyValidator
{
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

            var bindingsPresent = root.TryGetProperty("bindings", out var bindings);
            var panelsPresent = root.TryGetProperty("panels", out var panels);
            if (!bindingsPresent && !panelsPresent)
            {
                // Not a recognised bindings/panels document; accept opaquely.
                return;
            }

            // The document IS a report/dashboard bindings/panels graph, so enforce it. A present-but-wrong-type
            // bindings/panels member is itself a violation (it would otherwise silently bypass validation).
            var errors = new List<FieldValidationError>();

            HashSet<string> aliases;
            if (bindingsPresent && bindings.ValueKind != JsonValueKind.Array)
            {
                errors.Add(FieldValidationError.Create(
                    "publication.bindings.invalid",
                    "The report/dashboard document's bindings must be an array.",
                    path: "/bindings"));
                aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                aliases = ValidateBindings(bindingsPresent ? bindings : default, errors);
            }

            if (panelsPresent && panels.ValueKind != JsonValueKind.Array)
            {
                errors.Add(FieldValidationError.Create(
                    "publication.panels.invalid",
                    "The report/dashboard document's panels must be an array.",
                    path: "/panels"));
            }
            else if (panelsPresent)
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

            if (IsChartPanel(panel))
            {
                if (!DeclaresVegaLiteSchema(panel))
                {
                    errors.Add(FieldValidationError.Create(
                        "publication.panel.chartSpec.vegaLite",
                        "Chart panels must declare a Vega-Lite spec: a JSON object with a vega-lite \"$schema\".",
                        path: path + "/chartSpec"));
                }
            }
            else if (CarriesChartSpec(panel))
            {
                // Converse of publication.panel.chartSpec.vegaLite: only chart panels may carry a spec.
                // A spec on a map/table/text/filter/metric panel is contradictory authoring — the panel
                // renders as its declared kind and the chart the author wrote never appears, with nothing
                // saying why — so the publish path rejects it, matching the document validators'
                // chartSpecNotAllowed error severity (honua-server#3263) — FieldValidationError.Create
                // defaults to ValidationSeverity.Error, and there is no publish-path reason to soften it:
                // publish is the last gate before the panel is served. Keyed off the same non-empty test
                // the document validators use, so an absent, null, or empty round-tripped chartSpec never
                // trips it.
                errors.Add(FieldValidationError.Create(
                    "publication.panel.chartSpec.notAllowed",
                    $"Panel kind '{GetString(panel, "kind")?.Trim()}' must not carry a chartSpec; only chart panels may.",
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

    private static bool CarriesChartSpec(JsonElement panel)
    {
        if (panel.ValueKind != JsonValueKind.Object || !panel.TryGetProperty("chartSpec", out var spec))
        {
            return false;
        }

        return spec.ValueKind switch
        {
            JsonValueKind.Object => spec.EnumerateObject().MoveNext(),
            JsonValueKind.String => !string.IsNullOrWhiteSpace(spec.GetString()),
            _ => false,
        };
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
