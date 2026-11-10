// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;

namespace Honua.Plugins.Abstractions;

public sealed record PluginEndpointResponse(
    int StatusCode,
    string? ContentType,
    ReadOnlyMemory<byte> Body,
    ImmutableDictionary<string, string?> Headers)
{
    public static PluginEndpointResponse Json(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new PluginEndpointResponse(
            StatusCode: 200,
            ContentType: "application/json",
            Body: Encoding.UTF8.GetBytes(json),
            Headers: ImmutableDictionary<string, string?>.Empty);
    }

    public static PluginEndpointResponse Text(int statusCode, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PluginEndpointResponse(
            statusCode,
            ContentType: "text/plain; charset=utf-8",
            Body: Encoding.UTF8.GetBytes(text),
            Headers: ImmutableDictionary<string, string?>.Empty);
    }

    public static PluginEndpointResponse Status(int statusCode)
        => new(statusCode, ContentType: null, Body: ReadOnlyMemory<byte>.Empty, ImmutableDictionary<string, string?>.Empty);
}
