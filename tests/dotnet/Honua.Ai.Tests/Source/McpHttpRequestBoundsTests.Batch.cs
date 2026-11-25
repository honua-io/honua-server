// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpHttpRequestBoundsTests
{
    public enum BatchCaller
    {
        Anonymous,
        Admin
    }

    [IntegrationTheory]
    [InlineData(BatchCaller.Anonymous, 2)]
    [InlineData(BatchCaller.Admin, 2)]
    [InlineData(BatchCaller.Admin, 3)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task Batch_WithinCallerLimit_DispatchesAllElements(BatchCaller caller, int count)
    {
        using var request = BatchRequest(count, caller);
        var before = host.Counter.Calls;

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetArrayLength().Should().Be(count);
        document.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetInt32())
            .Should().Equal(Enumerable.Range(1, count));
        AssertDispatchedTools(document.RootElement, caller);

        (host.Counter.Calls - before).Should().BeGreaterThanOrEqualTo(count);
    }

    [IntegrationTheory]
    [InlineData(BatchCaller.Anonymous, 2)]
    [InlineData(BatchCaller.Admin, 3)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task Batch_AboveCallerLimit_RejectsWholeBatchWithoutDispatch(BatchCaller caller, int limit)
    {
        using var request = BatchRequest(limit + 1, caller);
        var before = host.Counter.Calls;

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        root.GetProperty("error").GetProperty("message").GetString()
            .Should().Contain(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        host.Counter.Calls.Should().Be(before, "admission must reject the entire batch before its first element");
    }

    private static void AssertDispatchedTools(JsonElement batch, BatchCaller caller)
    {
        foreach (var item in batch.EnumerateArray())
        {
            item.TryGetProperty("error", out _).Should().BeFalse();
            var tools = item.GetProperty("result").GetProperty("tools");
            if (caller == BatchCaller.Admin)
            {
                tools.GetArrayLength().Should().BePositive();
            }
            else
            {
                tools.GetArrayLength().Should().Be(0);
            }
        }
    }

    private static HttpRequestMessage BatchRequest(int count, BatchCaller caller)
    {
        var elements = Enumerable.Range(1, count)
            .Select(id => ListRequest.Replace("\"id\":1", "\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var body = "[" + string.Join(',', elements) + "]";
        Encoding.UTF8.GetByteCount(body).Should().BeLessThan(BodyLimit, "this test must reach batch admission");
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp?view=default")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (caller == BatchCaller.Admin)
        {
            request.Headers.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
        }

        return request;
    }
}
