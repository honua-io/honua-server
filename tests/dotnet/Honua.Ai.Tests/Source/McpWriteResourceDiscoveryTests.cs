// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpWorkflowViewTests
{
    private static readonly Dictionary<string, string> ProducerReadbacks = new(StringComparer.Ordinal)
    {
        ["honua_execute_plan"] = "honua_list_jobs",
        ["honua_studio_create_draft"] = "honua_studio_get_draft",
        ["honua_studio_save_version"] = "honua_studio_get_version"
    };

    [UnitTest]
    public async Task LiveWorkflowViews_EveryWriteTool_ProvidesReadOnlyResourceDiscovery()
    {
        var entries = await LiveCatalogAsync();
        var catalog = entries.ToDictionary(entry => entry.Descriptor.Name, entry => entry.Descriptor, StringComparer.Ordinal);
        var views = McpWorkflowViewCatalog.All.Values.ToDictionary(view => view.Name,
            view => McpWorkflowViewProjector.Project(view, entries).Members.Select(member => member.Descriptor).ToArray(), StringComparer.Ordinal);
        views.Add("full", catalog.Values.ToArray());
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        var writes = 0;
        foreach (var (view, tools) in views)
        {
            var available = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var tool in tools.Where(tool => tool.Annotations?.ReadOnlyHint != true))
            {
                writes++;
                var audit = new WriteResourceDiscoveryAudit(catalog, available);
                audit.Inspect(tool);
                gaps.UnionWith(audit.Missing.Select(source => $"{view}/{tool.Name}: {source}"));
            }
        }
        _output.WriteLine($"Write descriptors audited: {writes}");
        writes.Should().BePositive();
        gaps.Should().BeEmpty("write resources and read prerequisites must be reachable in the selected view");
    }

    [UnitTest]
    public async Task LiveWorkflowViews_RemovedDraftReadback_IsRejected()
    {
        var entries = await LiveCatalogAsync();
        var catalog = entries.ToDictionary(entry => entry.Descriptor.Name, entry => entry.Descriptor, StringComparer.Ordinal);
        var available = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, entries).Members
            .Select(member => member.ToolName).Where(name => name != "honua_studio_get_draft").ToHashSet(StringComparer.Ordinal);
        var audit = new WriteResourceDiscoveryAudit(catalog, available);
        audit.Inspect(catalog["honua_studio_save_version"]);
        audit.Missing.Should().Contain("honua_studio_get_draft");
    }

    private sealed class WriteResourceDiscoveryAudit(Dictionary<string, McpToolDescriptor> catalog, HashSet<string> available)
    {
        private readonly Queue<string> _pending = new();
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private readonly HashSet<string> _readSchemas = new(StringComparer.Ordinal);
        public SortedSet<string> Missing { get; } = new(StringComparer.Ordinal);

        public void Inspect(McpToolDescriptor tool)
        {
            EnqueueReferences(tool.InputSchema);
            while (_pending.TryDequeue(out var producer)) Expand(producer);
        }

        private void EnqueueReferences(JsonElement schema)
        {
            var origins = new RequiredInputOriginAudit(catalog.Keys.ToHashSet(StringComparer.Ordinal));
            origins.Inspect(schema, new InputOriginPath("$", null));
            foreach (var reference in origins.ProducerReferences) _pending.Enqueue(reference);
        }

        private void Expand(string name)
        {
            if (!_visited.Add(name)) return;
            if (!catalog.TryGetValue(name, out var producer))
            {
                Missing.Add($"unknown producer {name}");
                return;
            }
            if (producer.Annotations?.ReadOnlyHint == true) RequireRead(name);
            else if (ProducerReadbacks.TryGetValue(name, out var readback)) RequireRead(readback);
            else Missing.Add($"read-back for {name}");
        }

        private void RequireRead(string name)
        {
            if (!available.Contains(name)) Missing.Add(name);
            if (!catalog.TryGetValue(name, out var read) || read.Annotations?.ReadOnlyHint != true)
            {
                Missing.Add($"read-only descriptor {name}");
                return;
            }
            if (_readSchemas.Add(name)) EnqueueReferences(read.InputSchema);
        }
    }
}
