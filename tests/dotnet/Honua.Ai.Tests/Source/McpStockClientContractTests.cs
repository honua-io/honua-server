// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpWorkflowViewTests
{
    [UnitTest]
    public async Task LiveWorkflowViews_ServiceWrites_ProvideReadOnlyServiceDiscovery()
    {
        var catalog = await LiveCatalogAsync();
        var sourceName = PublishedOperationTool.ProjectName("admin.services.list");
        catalog.Single(entry => entry.Descriptor.Name == sourceName).Descriptor.Annotations!.ReadOnlyHint.Should().BeTrue();
        var consumers = new[]
        {
            "admin.services.access-policy.set", "admin.services.layer-metadata.set",
            "admin.services.protocols.set", "admin.services.timeinfo.set", "service.move"
        }.Select(PublishedOperationTool.ProjectName).ToHashSet(StringComparer.Ordinal);
        foreach (var view in McpWorkflowViewCatalog.All.Values)
        {
            var members = McpWorkflowViewProjector.Project(view, catalog).Members;
            if (members.Any(member => consumers.Contains(member.ToolName)))
                members.Should().Contain(member => member.ToolName == sourceName && member.Descriptor.Annotations!.ReadOnlyHint == true, view.Name);
        }
    }

    [UnitTest]
    public async Task LiveWorkflowViews_NativeLayerWrites_ExposeStorageLayerDiscovery()
    {
        var catalog = await LiveCatalogAsync();
        var source = catalog.Select(entry => entry.Descriptor).SingleOrDefault(descriptor => descriptor.Name == "honua_admin_connection_layers_list");
        source.Should().NotBeNull("native layer writes consume storage ids, distinct from publication indexes");
        source!.Annotations!.ReadOnlyHint.Should().BeTrue();
        catalog.Single(entry => entry.Descriptor.Name == "honua_admin_layer_set_enabled").Descriptor
            .InputSchema.GetProperty("properties").GetProperty("layerId").GetProperty("description").GetString()
            .Should().Contain("honua_admin_connection_layers_list");
        foreach (var view in McpWorkflowViewCatalog.All.Values)
        {
            var members = McpWorkflowViewProjector.Project(view, catalog).Members;
            if (members.Any(member => member.ToolName == "honua_admin_layer_set_enabled"))
                members.Should().Contain(member => member.ToolName == source.Name, view.Name);
        }
    }

    [UnitTest]
    public async Task LiveWorkflowViews_StudioDraftConsumers_NameTheDraftCreator()
    {
        var catalog = await LiveCatalogAsync();
        var consumers = catalog.Where(entry => entry.Descriptor.Name.StartsWith("honua_studio_", StringComparison.Ordinal)
            && entry.Descriptor.InputSchema.GetProperty("properties").TryGetProperty("draftId", out _)).ToArray();
        consumers.Should().NotBeEmpty();
        foreach (var consumer in consumers)
        {
            consumer.Descriptor.InputSchema.GetProperty("properties").GetProperty("draftId")
                .GetProperty("description").GetString().Should().Contain("honua_studio_create_draft", consumer.Descriptor.Name);
        }
    }

    [UnitTest]
    public async Task LiveWorkflowViews_JobConsumers_ProvideReadOnlyJobDiscovery()
    {
        var catalog = await LiveCatalogAsync();
        foreach (var view in McpWorkflowViewCatalog.All.Values)
        {
            var members = McpWorkflowViewProjector.Project(view, catalog).Members;
            if (!members.Any(member => member.ToolName is "honua_publish_result" or "honua_ingest_job_artifact"))
            {
                continue;
            }

            foreach (var source in new[] { "honua_list_jobs", "honua_get_job_results" })
            {
                var tool = members.SingleOrDefault(member => member.ToolName == source);
                tool.Should().NotBeNull($"view '{view.Name}' must discover the completed job and artifacts required by its write tools");
                tool!.Descriptor.Annotations!.ReadOnlyHint.Should().BeTrue(source);
            }
        }
    }

    [UnitTest]
    public async Task LiveWorkflowViews_ConnectionPublication_ProvidesReadOnlyConnectionAndTableDiscovery()
    {
        var catalog = await LiveCatalogAsync();
        foreach (var view in McpWorkflowViewCatalog.All.Values)
        {
            var members = McpWorkflowViewProjector.Project(view, catalog).Members;
            if (!members.Any(member => member.ToolName == "honua_publish_service"))
            {
                continue;
            }

            foreach (var source in new[] { "honua_admin_connections_list", "honua_admin_connection_tables_list" })
            {
                var tool = members.SingleOrDefault(member => member.ToolName == source);
                tool.Should().NotBeNull($"view '{view.Name}' must discover the connection/schema/table required by honua_publish_service");
                tool!.Descriptor.Annotations!.ReadOnlyHint.Should().BeTrue(source);
            }

            var inputs = members.Single(member => member.ToolName == "honua_publish_service")
                .Descriptor.InputSchema.GetProperty("properties");
            inputs.GetProperty("connectionId").GetProperty("description").GetString()
                .Should().Contain("honua_admin_connections_list");
            foreach (var field in new[] { "schema", "table" })
            {
                inputs.GetProperty(field).GetProperty("description").GetString()
                    .Should().Contain("honua_admin_connection_tables_list");
            }
        }
    }

    [UnitTest]
    public async Task LiveWorkflowViews_RequiredInputs_HaveDescriptions()
    {
        var catalog = await LiveCatalogAsync();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog)
        {
            FindUndocumentedRequiredInputs(entry.Descriptor.InputSchema, $"full/{entry.Descriptor.Name}", missing);
        }

        foreach (var view in McpWorkflowViewCatalog.All.Values)
        {
            var projection = McpWorkflowViewProjector.Project(view, catalog);
            _output.WriteLine($"{view.Name}: {projection.Members.Count} tools, {projection.AggregateCanonicalBytes} descriptor bytes");
            foreach (var member in projection.Members)
            {
                var descriptor = member.Descriptor;
                FindUndocumentedRequiredInputs(descriptor.InputSchema, $"{view.Name}/{descriptor.Name}", missing);
            }
        }

        foreach (var field in missing)
        {
            _output.WriteLine(field);
        }

        missing.Should().BeEmpty("every required input in a shipped workflow view must explain its value to stock clients");
    }

    private static void FindUndocumentedRequiredInputs(JsonElement schema, string path, ISet<string> missing)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var field in required.EnumerateArray())
            {
                var name = field.GetString()!;
                if (!schema.TryGetProperty("properties", out var properties)
                    || !properties.TryGetProperty(name, out var property)
                    || !property.TryGetProperty("description", out var description)
                    || description.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(description.GetString()))
                {
                    missing.Add($"{path}.{name}");
                }
            }
        }

        if (schema.TryGetProperty("properties", out var children))
        {
            foreach (var child in children.EnumerateObject())
            {
                FindUndocumentedRequiredInputs(child.Value, $"{path}.{child.Name}", missing);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            FindUndocumentedRequiredInputs(items, path + "[]", missing);
        }

        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema.TryGetProperty(keyword, out var variants))
            {
                foreach (var variant in variants.EnumerateArray())
                {
                    FindUndocumentedRequiredInputs(variant, path, missing);
                }
            }
        }
    }
}
