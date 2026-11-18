// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Security;

public sealed class Cql2JsonParameterValidationTests
{
    private const string LiteralFilter = """{"op":"=","args":[{"property":"name"},"audit -- note"]}""";
    private const string ItemsPath = "/ogc/features/collections/0/items";

    [UnitTheory]
    [InlineData("/ogc/features/collections/0/items")]
    [InlineData("/stac/search")]
    public async Task Filter_JsonLiteralOnSupportedRoute_IsAdmitted(string path)
    {
        (await InvokeAsync(path, "cql2-json", LiteralFilter)).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("audit -- note")]
    [InlineData("O'Brien -- note")]
    [InlineData("audit \"quoted\" -- note")]
    [InlineData("audit /* note */")]
    [InlineData("")]
    public async Task Filter_EncodedStringLiteral_IsAdmitted(string literal)
    {
        var filter = JsonSerializer.Serialize(new { op = "=", args = new object[] { new { property = "name" }, literal } });
        (await InvokeAsync(ItemsPath, "cql2-json", filter)).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("""{"op":"and","args":[{"op":"=","args":[{"property":"name"},"audit -- note"]},{"op":"=","args":[{"property":"count"},0]}]}""")]
    [InlineData("""{"op":"in","args":[{"property":"name"},["audit -- note",""]]}""")]
    [InlineData("""{"op":"=","args":[{"property":"name"},{"value":"audit -- note"}]}""")]
    [InlineData("""{"op":"=","args":[{"property":"count"},0]}""")]
    [InlineData("""{"op":"isNull","args":[{"property":"name"},null]}""")]
    [InlineData("""{"op":"=","args":[{"property":"a\\u002d\\u002db"},"value"]}""")]
    public async Task Filter_NestedAndNonStringLiterals_IsAdmitted(string filter)
    {
        (await InvokeAsync(ItemsPath, "cql2-json", filter)).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("""{"op":"audit -- note","args":[{"property":"name"},"value"]}""")]
    [InlineData("""{"op":"=","args":[{"property":"audit -- note"},"value"]}""")]
    [InlineData("""{"op":"=","args":[{"property":"audit \u002d\u002d note"},"value"]}""")]
    [InlineData("""{"op":"=","args":[{"property":"name","extra":"audit -- note"},"value"]}""")]
    [InlineData("""{"op":"=","args":[{"property":"name"},"value"],"extra":"audit -- note"}""")]
    [InlineData("""{"op":"=","args":"audit -- note"}""")]
    [InlineData("""{"op":"=","args":[{"property":"name"},"audit -- note"]""")]
    [InlineData("""{"op":"=","args":[{"property":"name"},"value"]} audit -- note""")]
    public async Task Filter_StructuralOrMalformedContent_RemainsInspected(string filter)
    {
        (await InvokeAsync(ItemsPath, "cql2-json", filter)).Should().BeFalse();
    }

    [UnitTest]
    public async Task Filter_UndecodableEscapeWithoutSuspiciousText_IsAdmitted()
    {
        const string filter = """{"op":"=","args":[{"property":"\ud800"},"value"]}""";
        (await InvokeAsync(ItemsPath, "cql2-json", filter)).Should().BeTrue();
    }

    [UnitTest]
    public async Task Filter_UndecodableEscape_RemainsInspectedAsRawText()
    {
        const string filter = """{"op":"\ud800 -- note","args":[{"property":"name"},"value"]}""";
        (await InvokeAsync(ItemsPath, "cql2-json", filter)).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("""{"op":"=","args":[{"property":"a\u002d\u002db"},"v"],"x":"\ud800"}""")]
    [InlineData("""{"op":"=","args":[{"property":"a union\nselect b"},"v"]}""")]
    [InlineData("""{"op":"=","args":[{"property":"a union\u0009select b"},"v"]}""")]
    public async Task Filter_EscapedStructuralContent_IsInspectedDecoded(string filter)
    {
        (await InvokeAsync(ItemsPath, "cql2-json", filter)).Should().BeFalse();
    }

    // Literal content is masked without decoding; the filter parser reports an undecodable
    // literal as an invalid payload.
    [UnitTest]
    public async Task Filter_UndecodableEscapeInsideLiteral_IsMaskedLikeAnyLiteral()
    {
        const string filter = """{"op":"=","args":[{"property":"name"},"\udc00 -- note"]}""";
        (await InvokeAsync(ItemsPath, "cql2-json", filter)).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("/api/v1/admin/services")]
    [InlineData("/ogc/features-extra/collections/0/items")]
    [InlineData("/stac-extra/search")]
    [InlineData("/rest/services/test/FeatureServer/0/query")]
    public async Task Filter_JsonOnUnrelatedRoute_RemainsInspected(string path)
    {
        (await InvokeAsync(path, "cql2-json", LiteralFilter)).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("cql2-text")]
    [InlineData("unknown")]
    public async Task Filter_JsonWithoutExplicitLanguage_RemainsInspected(string? language)
    {
        (await InvokeAsync(ItemsPath, language, LiteralFilter)).Should().BeFalse();
    }

    [UnitTest]
    public async Task Filter_JsonLiteralWithOtherValidationFailure_RemainsRejected()
    {
        const string filter = """{"op":"=","args":[{"property":"name"},"<script>alert(1)</script>"]}""";
        (await InvokeAsync(ItemsPath, "cql2-json", filter)).Should().BeFalse();
    }

    private static async Task<bool> InvokeAsync(string path, string? language, string filter)
    {
        await using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
        {
            ["filter"] = filter,
            ["filter-lang"] = language
        });
        context.Response.Body = new MemoryStream();
        var passed = false;
        var middleware = new InputValidationMiddleware(
            _ => { passed = true; return Task.CompletedTask; },
            NullLogger<InputValidationMiddleware>.Instance,
            Options.Create(new InputValidationOptions()),
            Options.Create(new LimitsOptions()));
        await middleware.InvokeAsync(context);
        return passed;
    }
}
