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
        var rootStatus = schema.RootElement.GetProperty("properties").GetProperty("status");
        rootStatus.TryGetProperty("const", out _).Should().BeFalse(
            "a root const of error rejects every successful review");

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

    private static void Accepts(JSchema schema, string json)
    {
        JToken.Parse(json).IsValid(schema, out IList<string> errors).Should().BeTrue(
            "published outputSchema rejected {0}: {1}", json, string.Join("; ", errors));
    }
}
