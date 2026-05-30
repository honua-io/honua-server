// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Helpers;

/// <summary>
/// Shared helpers for admin response payloads and problem details.
/// </summary>
internal static class AdminResponseWriter
{
    public static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message).ExecuteAsync(context);
    }

    public static Task WriteErrorAsync(HttpContext context, string message, int statusCode)
        => WriteErrorAsync(context, statusCode, message);

    public static Task WriteMethodNotAllowedAsync(HttpContext context)
        => WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed, "Method not allowed");

    public static async Task WriteJsonAsync<T>(
        HttpContext context,
        T response,
        JsonTypeInfo<T> typeInfo)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, typeInfo, context.RequestAborted);
    }
}
