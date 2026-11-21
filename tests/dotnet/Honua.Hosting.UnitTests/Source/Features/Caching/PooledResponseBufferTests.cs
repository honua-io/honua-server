// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Tests.Features.Caching;

public sealed class PooledResponseBufferTests
{
    private const string GmlMediaType = "application/gml+xml; version=3.2";

    public static TheoryData<string> SerializerProfiles => new() { "default", "indented", "relaxed", "camel" };

    [UnitTest]
    public void PooledByteBuffer_StreamAndBufferWriterWritesAcrossGrowth_KeepEveryByteInOrder()
    {
        var expected = Enumerable.Range(0, 70_000).Select(index => (byte)(index % 251)).ToArray();
        using var buffer = new PooledByteBuffer(initialCapacity: 64);

        buffer.Write(expected, 0, 30_000);
        var span = buffer.GetSpan(20_000);
        expected.AsSpan(30_000, 20_000).CopyTo(span);
        buffer.Advance(20_000);
        buffer.WriteByte(expected[50_000]);
        buffer.Write(expected.AsSpan(50_001));

        buffer.Length.Should().Be(expected.Length);
        buffer.WrittenSpan.ToArray().Should().Equal(expected);
    }

    [UnitTest]
    public void PooledByteBuffer_AfterDispose_RefusesWrites()
    {
        var buffer = new PooledByteBuffer();
        buffer.Dispose();

        var write = () => buffer.WriteByte(1);

        write.Should().Throw<ObjectDisposedException>();
    }

    [UnitTheory]
    [MemberData(nameof(SerializerProfiles))]
    public void ETagJsonPayload_MatchesSerializeToUtf8Bytes(string profile)
    {
        var options = CreateOptions(profile);
        var value = CreateLargeValue();
        var typeInfo = options.GetTypeInfo(value.GetType());

        using var payload = ETagJsonPayload.Serialize(value, typeInfo);

        payload.WrittenSpan.ToArray().Should().Equal(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
    }

    [UnitTest]
    public async Task PooledUtf8ContentResult_WritesTheSameResponseAsResultsContent()
    {
        var text = CreateXmlText();
        var expected = await ExecuteAsync(Results.Content(text, GmlMediaType, Encoding.UTF8));

        using var buffer = new PooledByteBuffer(initialCapacity: 128);
        var actual = await ExecuteAsync(CreatePooledResult(buffer, text));

        actual.ContentType.Should().Be(expected.ContentType);
        actual.ContentLength.Should().Be(expected.ContentLength);
        actual.Body.Should().Equal(expected.Body);
    }

    [UnitTest]
    public async Task SharedResponseETag_PooledContent_GetsTheContentResultETag()
    {
        var text = CreateXmlText();
        var expected = await ExecuteWithSharedETagAsync(Results.Content(text, GmlMediaType, Encoding.UTF8));

        using var buffer = new PooledByteBuffer(initialCapacity: 128);
        var actual = await ExecuteWithSharedETagAsync(CreatePooledResult(buffer, text));

        actual.ETag.Should().NotBeNullOrEmpty().And.Be(expected.ETag);
        actual.Body.Should().Equal(expected.Body);
    }

    [UnitTheory]
    [InlineData("text/xml", false)]
    [InlineData("application/vnd.ogc.wms_xml", true)]
    public async Task PooledTextBuilder_ToUtf8Result_WritesTheSameResponseAsResultsContent(string mediaType, bool utf8Charset)
    {
        var builder = CreateChunkedText();
        var text = builder.ToString();
        var expected = await ExecuteAsync(utf8Charset
            ? Results.Content(text, mediaType, Encoding.UTF8)
            : Results.Content(text, mediaType));

        var contentType = utf8Charset ? PooledUtf8ContentResult.WithUtf8Charset(mediaType) : mediaType;
        var actual = await ExecuteAsync(PooledTextBuilder.ToUtf8Result(builder, contentType));

        actual.ContentType.Should().Be(expected.ContentType);
        actual.ContentLength.Should().Be(expected.ContentLength);
        actual.Body.Should().Equal(expected.Body);
    }

    [UnitTest]
    public void PooledTextBuilder_EncodeUtf8_MatchesEncodingGetBytesAcrossChunkBoundaries()
    {
        var builder = CreateChunkedText();
        using var buffer = new PooledByteBuffer(initialCapacity: 16);

        PooledTextBuilder.EncodeUtf8(builder, buffer);

        buffer.WrittenSpan.ToArray().Should().Equal(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    [UnitTest]
    public void PooledTextBuilder_Return_ClearsTheBuilderForTheNextRent()
    {
        var builder = PooledTextBuilder.Rent();
        builder.Append("<Capabilities/>");
        PooledTextBuilder.Return(builder);

        var next = PooledTextBuilder.Rent();
        try
        {
            next.Length.Should().Be(0);
        }
        finally
        {
            PooledTextBuilder.Return(next);
        }
    }

    // One character per append into a one-character builder, so the text spans many chunks and the opening
    // surrogate pair is split between the first two. It also holds a lone low surrogate and ends on a lone high
    // surrogate, which UTF-8 encodes as U+FFFD.
    private static StringBuilder CreateChunkedText()
    {
        var builder = new StringBuilder(capacity: 1);
        var text = "\uD834\uDD1E" + CreateXmlText() + " 😀 lone \uDC00 end \uD83D";
        foreach (var character in text)
        {
            builder.Append(character);
        }

        return builder;
    }

    // The test owns the buffer; the result also disposes it when executed, and Dispose is idempotent.
    private static PooledUtf8ContentResult CreatePooledResult(PooledByteBuffer buffer, string text)
    {
        buffer.Write(Encoding.UTF8.GetBytes(text));
        return new PooledUtf8ContentResult(buffer, PooledUtf8ContentResult.WithUtf8Charset(GmlMediaType));
    }

    private static async Task<(string? ContentType, long? ContentLength, byte[] Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        await result.ExecuteAsync(context);

        return (context.Response.ContentType, context.Response.ContentLength, body.ToArray());
    }

    private static async Task<(string? ETag, byte[] Body)> ExecuteWithSharedETagAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = body;
        context.RequestServices = new ServiceCollection().AddLogging()
            .AddSingleton<IETagService, ETagService>().BuildServiceProvider();

        var conditional = (IResult)(await SharedResponseETag.ApplyAsync(context, result))!;
        await conditional.ExecuteAsync(context);

        return (context.Response.Headers[HeaderNames.ETag].ToString(), body.ToArray());
    }

    private static string CreateXmlText()
    {
        var text = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<wfs:FeatureCollection>");
        for (var index = 0; index < 400; index++)
        {
            text.Append("\n  <wfs:member><name>Höhe ").Append(index).Append(" — ✓ &amp; 東京</name></wfs:member>");
        }

        return text.Append("\n</wfs:FeatureCollection>").ToString();
    }

    private static Dictionary<string, object?> CreateLargeValue() => new()
    {
        ["type"] = "FeatureCollection",
        ["title"] = "Höhe <tag> & \"quote\" 東京",
        ["features"] = Enumerable.Range(0, 600)
            .Select(index => new Dictionary<string, object?>
            {
                ["Id"] = index,
                ["Name"] = $"feature {index} é",
                ["Value"] = index * 0.5,
                ["Tags"] = new[] { "a", "b" },
            })
            .ToList(),
    };

    private static JsonSerializerOptions CreateOptions(string profile)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        switch (profile)
        {
            case "indented":
                options.WriteIndented = true;
                options.IndentCharacter = '\t';
                options.IndentSize = 1;
                options.NewLine = "\n";
                break;
            case "relaxed":
                options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
                break;
            case "camel":
                options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
                break;
        }

        return options;
    }
}
