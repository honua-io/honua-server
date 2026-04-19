// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// Maps geoprocessing domain exceptions to MCP JSON-RPC error envelopes.
/// Mirrors the translation that <see cref="HonuaProcessService.MapToRpcException"/>
/// performs for gRPC so every transport surfaces the same recoverable signals
/// (approval required, idempotency conflict, authentication) without parsing
/// message strings.
/// </summary>
internal static class McpErrorMapper
{
    /// <summary>
    /// Canonical string error codes used in <see cref="McpErrorData.Code"/>.
    /// Values match the gRPC status vocabulary so clients can reuse one mapping.
    /// </summary>
    public static class Codes
    {
        public const string Unauthenticated = "unauthenticated";
        public const string PermissionDenied = "permission_denied";
        public const string FailedPrecondition = "failed_precondition";
        public const string NotFound = "not_found";
        public const string InvalidArgument = "invalid_argument";
        public const string Unavailable = "unavailable";
        public const string AlreadyExists = "already_exists";
        public const string Internal = "internal";
    }

    /// <summary>
    /// JSON-RPC 2.0 numeric codes. MCP reserves -32000..-32099 for implementation
    /// errors. We map 1:1 from the string codes so clients can use either axis.
    /// </summary>
    private const int JsonRpcServerError = -32000;
    private const int JsonRpcInvalidParams = -32602;

    /// <summary>
    /// Translates the supplied exception into a JSON-RPC error object.
    /// </summary>
    public static McpJsonRpcError Map(Exception ex) => ex switch
    {
        GeoprocessingAuthorizationException authEx when authEx.RequiresAuthentication => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = authEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.Unauthenticated,
                RequiresReauthentication = true
            }
        },

        GeoprocessingAuthorizationException authEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = authEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.PermissionDenied
            }
        },

        GeoprocessingApprovalRequiredException approvalEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = approvalEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.FailedPrecondition,
                ApprovalRequired = true,
                PolicyRef = approvalEx.PolicyRef
            }
        },

        GeoprocessingNotFoundException notFoundEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = notFoundEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.NotFound
            }
        },

        GeoprocessingPreconditionFailedException preconditionEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = preconditionEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.FailedPrecondition
            }
        },

        GeoprocessingValidationException validationEx => new McpJsonRpcError
        {
            Code = JsonRpcInvalidParams,
            Message = validationEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.InvalidArgument
            }
        },

        GeoprocessingStoreUnavailableException storeEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = storeEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.Unavailable,
                Retryable = true
            }
        },

        GeoprocessingIdempotencyConflictException conflictEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = conflictEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.AlreadyExists
            }
        },

        InvalidOperationException opEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = opEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.Internal
            }
        },

        _ => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = "An unexpected error occurred while processing the MCP request.",
            Data = new McpErrorData
            {
                Code = Codes.Internal
            }
        }
    };

    /// <summary>
    /// Creates an invalid-argument error for MCP protocol-level validation issues
    /// (unknown tool/resource, malformed arguments) without touching domain exceptions.
    /// </summary>
    public static McpJsonRpcError InvalidArgument(string message) => new()
    {
        Code = JsonRpcInvalidParams,
        Message = message,
        Data = new McpErrorData { Code = Codes.InvalidArgument }
    };

    /// <summary>
    /// Creates a <see cref="Codes.NotFound"/> error for unknown tools or resource URIs.
    /// </summary>
    public static McpJsonRpcError NotFound(string message) => new()
    {
        Code = JsonRpcServerError,
        Message = message,
        Data = new McpErrorData { Code = Codes.NotFound }
    };

    /// <summary>
    /// Creates an unauthenticated error (e.g. when the caller has no
    /// <see cref="System.Security.Claims.ClaimsPrincipal"/>).
    /// </summary>
    public static McpJsonRpcError Unauthenticated() => new()
    {
        Code = JsonRpcServerError,
        Message = "Authentication is required to use the MCP operator surface.",
        Data = new McpErrorData
        {
            Code = Codes.Unauthenticated,
            RequiresReauthentication = true
        }
    };
}
