// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpConnectionDiscoveryIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /mcp")]
    public async Task AuthenticatedCatalogCalls_EmptyArguments_PreserveBothContentChannels()
    {
        await using var fixture = CreateStockFixture();
        await fixture.InitializeAsync();
        using var admin = fixture.CreateAdminClient();
        var descriptors = (await fixture.Services.GetRequiredService<McpDataAccessSurface>().GetAllToolsAsync())
            .ToDictionary(tool => tool.Name, tool => McpWorkflowViewDescriptorClassifier.Describe(tool), StringComparer.Ordinal);
        var largestPayloadBytes = 0;
        var views = McpWorkflowViewCatalog.All.Keys.Append("full");
        foreach (var view in views)
        {
            largestPayloadBytes = Math.Max(largestPayloadBytes, await CheckViewChannelsAsync(admin, view, descriptors));
        }

        largestPayloadBytes.Should().BeGreaterThanOrEqualTo(1024, "successful live reads must exercise the former size-elision boundary");
    }

    private async Task<int> CheckViewChannelsAsync(HttpClient admin, string view,
        IReadOnlyDictionary<string, McpToolDescriptor> descriptors)
    {
        var names = await ListToolNamesAsync(admin, view);
        names.Should().NotBeEmpty(view);
        names.Should().OnlyHaveUniqueItems(view);
        var largest = 0;
        foreach (var name in names)
        {
            var call = await CallAsync(admin, new ToolInvocation(name, new { }, view));
            largest = Math.Max(largest, CheckChannelResult(call, new ToolChannelContext(view, name, descriptors[name])));
        }
        return largest;
    }

    private sealed record ToolChannelContext(string View, string Name, McpToolDescriptor Descriptor);

    private static int CheckChannelResult(JsonElement call, ToolChannelContext context)
    {
        var label = $"{context.View}/{context.Name}";
        call.TryGetProperty("error", out _).Should().BeFalse($"{label} must return a tool result");
        var result = call.GetProperty("result");
        result.TryGetProperty("structuredContent", out var structured).Should().BeTrue(label);
        var mirroredPayloads = result.GetProperty("content").EnumerateArray()
            .Where(block => block.GetProperty("type").GetString() == "text")
            .Select(block => block.GetProperty("text").GetString()!)
            .Count(text => IsSameJson(text, structured));
        mirroredPayloads.Should().Be(1, $"{label} must mirror the complete JSON payload exactly once");
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean()) return 0;
        if (context.Descriptor.OutputSchema is { } outputSchema)
        {
            var valid = JToken.Parse(structured.GetRawText()).IsValid(JSchema.Parse(outputSchema.GetRawText()), out IList<ValidationError> errors);
            valid.Should().BeTrue($"{label} must honor its output schema: {string.Join(", ", errors.Select(error => $"{error.ErrorType} at {error.Path}"))}");
        }
        return Encoding.UTF8.GetByteCount(structured.GetRawText());
    }

    private WebAppFixture CreateStockFixture()
    {
        IConfiguration? hostConfiguration = null;
        return new WebAppFixture().UseKestrel().WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("ConnectionStrings:redis", _redis.ConnectionString);
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.ConfigureServices((context, _) => hostConfiguration = context.Configuration);
            })
            // The test host adds its data provider after startup's conditional MCP registration.
            .ConfigureServices(services => services.AddMcpDataAccessSurface(hostConfiguration!));
    }

    private static bool IsSameJson(string text, JsonElement expected)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonElement.DeepEquals(document.RootElement, expected);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
