// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Grounding;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp;

/// <summary>
/// Maps geoprocessing domain exceptions to MCP JSON-RPC error envelopes.
/// Mirrors the translation that <see cref="HonuaProcessService.MapToRpcException"/>
/// performs for gRPC so every transport surfaces the same recoverable signals
/// (approval required, idempotency conflict, authentication) without parsing
/// message strings. Numeric codes follow JSON-RPC 2.0 / MCP 2025-03-26: -32700
/// parse error, -32600 invalid request, -32601 method not found, -32602 invalid
/// params, -32002 resource not found, and -32000 for generic server errors.
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

    /// <summary>JSON-RPC 2.0 parse error for malformed JSON payloads.</summary>
    public const int JsonRpcParseError = -32700;

    /// <summary>JSON-RPC 2.0 invalid request for payloads that are valid JSON but not a valid JSON-RPC envelope.</summary>
    public const int JsonRpcInvalidRequest = -32600;

    /// <summary>JSON-RPC 2.0 method-not-found error for unknown MCP methods.</summary>
    public const int JsonRpcMethodNotFound = -32601;

    /// <summary>JSON-RPC 2.0 invalid-params error for unknown tool names or malformed method params.</summary>
    public const int JsonRpcInvalidParams = -32602;

    /// <summary>MCP reserves -32000..-32099 for implementation-defined server errors.</summary>
    public const int JsonRpcServerError = -32000;

    /// <summary>MCP 2025-03-26 reserves -32002 for <c>resources/read</c> calls whose URI is unknown.</summary>
    public const int JsonRpcResourceNotFound = -32002;

    /// <summary>
    /// Translates the supplied domain exception into a JSON-RPC error object.
    /// Used by <c>resources/read</c> (which has no result-level <c>isError</c>
    /// hook) and by <c>tools/call</c> when building the tool-level error envelope.
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
            Code = JsonRpcResourceNotFound,
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

        GroundingException groundingEx => MapGrounding(groundingEx),

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

    private static McpJsonRpcError MapGrounding(GroundingException exception) => exception.Kind switch
    {
        GroundingErrorKind.EmptyGoal or GroundingErrorKind.UnsupportedWorkflowFamily => new McpJsonRpcError
        {
            Code = JsonRpcInvalidParams,
            Message = exception.Message,
            Data = new McpErrorData { Code = Codes.InvalidArgument }
        },
        GroundingErrorKind.UnknownIntent => new McpJsonRpcError
        {
            Code = JsonRpcResourceNotFound,
            Message = exception.Message,
            Data = new McpErrorData { Code = Codes.NotFound }
        },
        GroundingErrorKind.CatalogUnavailable => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = exception.Message,
            Data = new McpErrorData { Code = Codes.Unavailable, Retryable = true }
        },
        _ => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = exception.Message,
            Data = new McpErrorData { Code = Codes.Internal }
        }
    };

    /// <summary>
    /// Creates a JSON-RPC parse-error envelope for payloads that are not valid JSON.
    /// </summary>
    public static McpJsonRpcError ParseError(string message) => new()
    {
        Code = JsonRpcParseError,
        Message = message,
        Data = new McpErrorData { Code = Codes.InvalidArgument }
    };

    /// <summary>
    /// Creates a JSON-RPC invalid-request envelope for payloads that are JSON but
    /// not a valid JSON-RPC 2.0 envelope (missing body, bad <c>jsonrpc</c> version,
    /// missing or null/non-scalar <c>id</c> on a request).
    /// </summary>
    public static McpJsonRpcError InvalidRequest(string message) => new()
    {
        Code = JsonRpcInvalidRequest,
        Message = message,
        Data = new McpErrorData { Code = Codes.InvalidArgument }
    };

    /// <summary>
    /// Creates a JSON-RPC method-not-found envelope for unknown MCP methods.
    /// </summary>
    public static McpJsonRpcError MethodNotFound(string message) => new()
    {
        Code = JsonRpcMethodNotFound,
        Message = message,
        Data = new McpErrorData { Code = Codes.NotFound }
    };

    /// <summary>
    /// Creates an invalid-params error for protocol-level param validation failures
    /// (missing tool name, unknown tool, malformed <c>params</c> shape).
    /// </summary>
    public static McpJsonRpcError InvalidArgument(string message) => new()
    {
        Code = JsonRpcInvalidParams,
        Message = message,
        Data = new McpErrorData { Code = Codes.InvalidArgument }
    };

    /// <summary>
    /// Creates a resource-not-found envelope for <c>resources/read</c> calls whose
    /// URI is unknown to the server.
    /// </summary>
    public static McpJsonRpcError ResourceNotFound(string message) => new()
    {
        Code = JsonRpcResourceNotFound,
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
