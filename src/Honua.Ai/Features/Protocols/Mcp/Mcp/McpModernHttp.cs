// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpModernHttp
{
    internal static async Task HandlePostAsync(HttpContext context, JsonElement body, CancellationToken cancellationToken)
    {
        context.Items[McpModernProtocol.RequestItemKey] = true;
        context.Request.Headers.Remove(McpSessionManager.SessionHeaderName);
        context.Request.Headers.Remove("Last-Event-ID");
        context.Items.Remove(Views.McpWorkflowViewNegotiation.HttpContextItemKey);
        if (!await AllowsOriginAsync(context).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var validationError = McpModernProtocol.Validate(context.Request, body);
        var response = validationError is null
            ? await DispatchAsync(context, body, cancellationToken).ConfigureAwait(false)
            : new McpJsonRpcResponse { Id = McpEndpointExtensions.ResponseIdForEarlyError(body), Error = validationError };
        if (response is null)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        ApplyResultContract(response, body);

        var status = response.Error?.Data?.RequiresReauthentication == true
            ? StatusCodes.Status401Unauthorized : response.Error?.Code switch
        {
            null => StatusCodes.Status200OK,
            McpErrorMapper.JsonRpcMethodNotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        };
        await McpEndpointExtensions.WriteSingleAsync(context, response, cancellationToken, status).ConfigureAwait(false);
    }

    private static void ApplyResultContract(McpJsonRpcResponse response, JsonElement body)
    {
        if (McpModernProtocol.ReadString(body, "method") == "resources/read"
            && response.Error?.Code == McpErrorMapper.JsonRpcResourceNotFound)
        {
            response.Error.Code = McpErrorMapper.JsonRpcInvalidParams;
        }

        if (response.Result is { } result)
        {
            response.Result = McpModernResults.Complete(result, body);
        }
    }

    internal static async Task<bool> TryRejectSessionVerbAsync(HttpContext context)
    {
        if (!McpModernProtocol.HasModernHeader(context.Request))
        {
            return false;
        }

        context.Response.StatusCode = await AllowsOriginAsync(context).ConfigureAwait(false)
            ? StatusCodes.Status405MethodNotAllowed : StatusCodes.Status403Forbidden;
        if (context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed)
        {
            context.Response.Headers.Allow = "POST";
        }

        return true;
    }

    private static Task<McpJsonRpcResponse?> DispatchAsync(HttpContext context, JsonElement body, CancellationToken cancellationToken)
    {
        var surface = context.RequestServices.GetRequiredService<McpDataAccessSurface>();
        var principalKey = McpAuthorizationHelper.ResolveSessionBindingKey(context)!;
        return McpEndpointExtensions.ProcessMessageAsync(context, surface, body, principalKey, cancellationToken);
    }

    private static async Task<bool> AllowsOriginAsync(HttpContext context)
    {
        var origins = context.Request.Headers.Origin;
        if (origins.Count == 0)
        {
            return true;
        }

        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
        {
            return false;
        }

        var provider = context.RequestServices.GetService<ICorsPolicyProvider>();
        if (provider is null)
        {
            return false;
        }

        var environment = context.RequestServices.GetService<IHostEnvironment>();
        var policyName = environment?.IsDevelopment() == true ? HttpCorsPolicyNames.Development : HttpCorsPolicyNames.Production;
        var policy = await provider.GetPolicyAsync(context, policyName).ConfigureAwait(false);
        return policy?.IsOriginAllowed(origins[0]!) == true;
    }
}
