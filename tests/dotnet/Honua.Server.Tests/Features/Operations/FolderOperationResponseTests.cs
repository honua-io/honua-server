// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderOperationResponseTests
{
    [UnitTheory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(428)]
    [InlineData(503)]
    public async Task MutationRefusal_PreservesHttpStatusAndProblemBody(int status)
    {
        var refusal = new FolderMutationFailure(status, "The catalog cannot accept this mutation.");
        await using var services = new ServiceCollection().AddLogging().AddHonuaJsonContexts().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await refusal.ToResult().ExecuteAsync(context);
        body.Position = 0;
        using var reader = new StreamReader(body);
        var httpBody = await reader.ReadToEndAsync();

        var response = FolderOperationResponse.From(new FolderExecutionResult(null, null,
            new FolderExecutionFailure.Mutation(refusal)), null);

        response.Status.Should().Be(context.Response.StatusCode);
        if (string.IsNullOrEmpty(httpBody)) response.Payload.Should().BeEmpty();
        else JsonNode.DeepEquals(JsonNode.Parse(response.Payload), JsonNode.Parse(httpBody))
            .Should().BeTrue("MCP must retain the HTTP refusal contract; native: {0}; HTTP: {1}",
                response.Payload, httpBody);
    }
}
