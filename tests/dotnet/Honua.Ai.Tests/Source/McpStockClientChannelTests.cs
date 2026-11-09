// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
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
            var names = await ListToolNamesAsync(admin, view);
            names.Should().NotBeEmpty(view);
            names.Should().OnlyHaveUniqueItems(view);
            foreach (var name in names)
            {
                // Empty arguments exercise real reads and required-input/domain refusals
                // under an authenticated administrator. This is channel coverage, not
                // a claim that each mutation succeeded or that its inputs are usable.
                var call = await CallAsync(admin, name, new { });
                call.TryGetProperty("error", out _).Should().BeFalse($"{view}/{name} must return a tool result");
                var result = call.GetProperty("result");
                result.TryGetProperty("structuredContent", out var structured).Should().BeTrue($"{view}/{name}");
                var mirroredPayloads = result.GetProperty("content").EnumerateArray()
                    .Where(block => block.GetProperty("type").GetString() == "text")
                    .Select(block => block.GetProperty("text").GetString()!)
                    .Count(text => IsSameJson(text, structured));
                mirroredPayloads.Should().Be(1, $"{view}/{name} must mirror the complete JSON payload exactly once");
                if (!result.TryGetProperty("isError", out var isError) || !isError.GetBoolean())
                {
                    if (descriptors[name].OutputSchema is { } outputSchema)
                    {
                        var valid = JToken.Parse(structured.GetRawText()).IsValid(JSchema.Parse(outputSchema.GetRawText()), out IList<ValidationError> errors);
                        valid.Should().BeTrue($"{view}/{name} must honor its output schema: {string.Join(", ", errors.Select(error => $"{error.ErrorType} at {error.Path}"))}");
                    }
                    largestPayloadBytes = Math.Max(largestPayloadBytes, Encoding.UTF8.GetByteCount(structured.GetRawText()));
                }
            }
        }

        largestPayloadBytes.Should().BeGreaterThanOrEqualTo(1024, "successful live reads must exercise the former size-elision boundary");
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
