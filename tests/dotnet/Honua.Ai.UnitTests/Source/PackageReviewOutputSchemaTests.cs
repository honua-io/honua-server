// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Inspector validates <c>structuredContent</c> against the published
/// <c>outputSchema</c>. Package review success uses <c>status</c> ready,
/// warning, or blocked. The error-envelope wrapper must not stamp
/// <c>status: const error</c> onto that success payload.
/// </summary>
public sealed class PackageReviewOutputSchemaTests
{
    [UnitTest]
    public void PublishedSchema_AcceptsReviewStatusAndTheErrorEnvelope()
    {
        var published = new McpToolDescriptor
        {
            Name = "honua_validate_package",
            OutputSchema = McpToolOutputSchemas.PackageReviewOutputSchema,
        }.OutputSchema!.Value;

        using var schema = JsonDocument.Parse(published.GetRawText());
        schema.RootElement.GetProperty("properties").TryGetProperty("status", out _)
            .Should().BeFalse("the branches carry the status constraints without a redundant root placeholder");

        var branches = schema.RootElement.GetProperty("oneOf").EnumerateArray().ToArray();
        var success = branches.Single(branch => branch.TryGetProperty("not", out _));
        var error = branches.Single(branch => !branch.TryGetProperty("not", out _));
        success.GetProperty("properties").GetProperty("status").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString())
            .Should().BeEquivalentTo(["ready", "warning", "blocked"]);
        error.GetProperty("properties").GetProperty("status").GetProperty("const").GetString()
            .Should().Be("error");

        var parsed = JSchema.Parse(published.GetRawText());
        Accepts(parsed, """
            {"status":"ready","contractVersion":"honua.package_review.v1","packageFamily":"app_package","findings":[]}
            """);
        Accepts(parsed, """
            {"status":"blocked","contractVersion":"honua.package_review.v1","packageFamily":"app_package","requiresApproval":true,"findings":[]}
            """);
        Accepts(parsed, """
            {"status":"error","code":"unauthenticated","message":"Authentication is required.","error":{"kind":"AuthorizationDenied","message":"Authentication is required."}}
            """);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncludeErrorEnvelope_OverlappingProperties_PreservesBranchValidation(bool closed)
    {
        using var document = JsonDocument.Parse("""
            {"type":"object","required":["status","value"],"properties":{"status":{"const":"ready"},"value":{"type":"integer"}}}
            """);
        var input = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!;
        input["additionalProperties"] = !closed;
        using var configured = JsonDocument.Parse(input.ToJsonString());
        var output = McpToolOutputSchemas.IncludeErrorEnvelope(configured.RootElement);
        output.GetProperty("properties").TryGetProperty("status", out _).Should().BeFalse();
        var parsed = JSchema.Parse(output.GetRawText());
        Accepts(parsed, """{"status":"ready","value":1}""");
        Accepts(parsed, """{"status":"error","code":"invalid","message":"Invalid request.","error":{}}""");
        JToken.Parse("""{"status":"other","value":1}""").IsValid(parsed).Should().BeFalse();
        JToken.Parse("""{"status":"ready","value":"wrong"}""").IsValid(parsed).Should().BeFalse();
        JToken.Parse("""{"status":"ready","value":1,"extra":true}""").IsValid(parsed).Should().Be(!closed);
        JToken.Parse("""{"status":"ready","value":1,"code":"invalid","message":"Invalid request.","error":{}}""")
            .IsValid(parsed).Should().BeFalse();
    }

    private static void Accepts(JSchema schema, string json)
    {
        JToken.Parse(json).IsValid(schema, out IList<string> errors).Should().BeTrue(
            "published outputSchema rejected {0}: {1}", json, string.Join("; ", errors));
    }
}
