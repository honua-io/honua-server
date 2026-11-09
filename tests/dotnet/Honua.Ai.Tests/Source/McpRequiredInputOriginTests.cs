// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpWorkflowViewTests
{
    [UnitTest]
    public async Task LiveWorkflowViews_RequiredInputs_NameValueOrigins()
    {
        var catalog = await LiveCatalogAsync();
        var names = catalog.Select(entry => entry.Descriptor.Name).ToHashSet(StringComparer.Ordinal);
        var audit = new RequiredInputOriginAudit(names);
        foreach (var entry in catalog)
            audit.Inspect(entry.Descriptor.InputSchema, new InputOriginPath(entry.Descriptor.Name, null));
        foreach (var missing in audit.Missing) _output.WriteLine(missing);
        audit.Missing.Should().BeEmpty("required values must name a real producer tool or a caller-owned source");
    }

    private sealed record InputOriginPath(string Path, string? SourceDescription);

    private sealed class RequiredInputOriginAudit(HashSet<string> toolNames)
    {
        private static readonly Regex ToolReference = new(@"\bhonua_[a-z0-9_]+\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        public SortedSet<string> Missing { get; } = new(StringComparer.Ordinal);

        public void Inspect(JsonElement schema, InputOriginPath context)
        {
            if (schema.ValueKind != JsonValueKind.Object) return;
            var source = HasNamedOrigin(schema) ? ReadDescription(schema) : context.SourceDescription;
            InspectRequired(schema, context with { SourceDescription = source });
            InspectChildren(schema, context with { SourceDescription = source });
        }

        private void InspectRequired(JsonElement schema, InputOriginPath context)
        {
            if (!schema.TryGetProperty("required", out var required)) return;
            foreach (var field in required.EnumerateArray())
            {
                var name = field.GetString()!;
                var hasSource = schema.TryGetProperty("properties", out var properties)
                    && properties.TryGetProperty(name, out var property) && HasNamedOrigin(property);
                if (!hasSource && context.SourceDescription is null) Missing.Add($"{context.Path}.{name}");
            }
        }

        private void InspectChildren(JsonElement schema, InputOriginPath context)
        {
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var child in properties.EnumerateObject())
                    Inspect(child.Value, context with { Path = $"{context.Path}.{child.Name}" });
            if (schema.TryGetProperty("items", out var items)) Inspect(items, context with { Path = context.Path + "[]" });
            foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
                InspectVariants(schema, keyword, context);
        }

        private void InspectVariants(JsonElement schema, string keyword, InputOriginPath context)
        {
            if (!schema.TryGetProperty(keyword, out var variants)) return;
            foreach (var variant in variants.EnumerateArray()) Inspect(variant, context);
        }

        private bool HasNamedOrigin(JsonElement schema)
        {
            var description = ReadDescription(schema);
            var references = ToolReference.Matches(description).Select(match => match.Value).ToArray();
            return references.All(toolNames.Contains)
                && (description.Contains("caller", StringComparison.OrdinalIgnoreCase) || references.Length > 0);
        }

        private static string ReadDescription(JsonElement schema) =>
            schema.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                ? description.GetString()! : string.Empty;
    }
}
