// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

public static class GeoServicesErrorAssertions
{
    public static async Task AssertGeoServicesErrorAsync(HttpContext context, IResult result, params int[] expectedCodes)
    {
        if (expectedCodes is null || expectedCodes.Length == 0)
        {
            throw new ArgumentException("At least one expected error code is required.", nameof(expectedCodes));
        }

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        JsonDocument? json = null;
        try
        {
            json = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Fall through to the envelope assertion below with json == null.
        }

        using (json)
        {
            var hasErrorCode = json is not null
                && json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out _);
            hasErrorCode.Should().BeTrue(
                $"the response body should be a GeoServices error envelope {{\"error\":{{\"code\":N}}}} but was: {(body.Length > 300 ? body[..300] + "…" : body)}");

            json!.RootElement.GetProperty("error").GetProperty("code").GetInt32()
                .Should().BeOneOf(expectedCodes);
        }
    }
}
