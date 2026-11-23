// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

// Adds the next step an agent can take to a mapped refusal, so it asks for the right grant,
// waits on the right proposal or corrects the right argument instead of retrying blindly.
// Not-found wording never says whether the resource exists in another tenant.
internal static class McpRefusalNextAction
{
    private const string PermissionsTool = "honua_admin_api_key_effective_permissions";

    public static McpJsonRpcError Apply(McpJsonRpcError error)
    {
        if (error.Data is { NextAction: null } data)
        {
            data.NextAction = For(data);
        }

        return error;
    }

    internal static string? For(McpErrorData data) => data.Code switch
    {
        McpErrorMapper.Codes.Unauthenticated =>
            "Sign in: send a scoped API key in X-API-Key or an OAuth access token in Authorization: Bearer, then retry.",
        McpErrorMapper.Codes.InsufficientScope =>
            $"Ask an administrator for a key with the scope this call needs. {PermissionsTool} lists the grants you have.",
        McpErrorMapper.Codes.PermissionDenied =>
            $"Ask an administrator for the role or grant this call needs. {PermissionsTool} lists the grants you have.",
        McpErrorMapper.Codes.FailedPrecondition => ForPrecondition(data),
        McpErrorMapper.Codes.NotFound =>
            "Check the id with the matching list tool; an id you cannot see is reported as not found.",
        McpErrorMapper.Codes.InvalidArgument =>
            "Correct the argument named in the message. The tool's inputSchema lists the accepted values.",
        McpErrorMapper.Codes.AlreadyExists =>
            "This idempotency key was already used for a different request. Send the identical request to replay it, "
            + "or use a new key for new work.",
        McpErrorMapper.Codes.Unavailable => ForUnavailable(data),
        _ => null,
    };

    private static string ForPrecondition(McpErrorData data)
    {
        if (data.ApprovalRequired == true)
        {
            return data.ProposalId is { } proposalId
                ? $"Wait for a person to approve proposal {proposalId} in Console, and read {data.ResourceUri} until it is resolved."
                : "Submit the change as a proposal; a person approves it in Console.";
        }

        return data.CurrentGeneration is { } generation
            ? $"Read the draft again (generation {generation}) and reapply your change."
            : "Read the current state, resolve the condition named in the message, then retry.";
    }

    private static string ForUnavailable(McpErrorData data)
    {
        if (data.Retryable != true)
        {
            return data.Remediation ?? "Retrying will not help: an administrator must restore the missing dependency.";
        }

        return data.RetryAfterSeconds is { } seconds
            ? $"Retry after {seconds} seconds."
            : "Retry shortly.";
    }
}
