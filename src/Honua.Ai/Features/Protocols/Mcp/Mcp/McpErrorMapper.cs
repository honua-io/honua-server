// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Exceptions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Rendering;
using Honua.Ai.Grounding;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpErrorMapper
{
    private const string AuthenticationGuidance = "Use a scoped API key in X-API-Key, obtained from your Honua administrator, "
        + "or an OAuth access token in Authorization: Bearer from your configured identity provider. "
        + "Discover OAuth authorization servers at " + McpProtectedResourceMetadata.RoutePath + ".";

    public static class Codes
    {
        public const string Unauthenticated = "unauthenticated";
        public const string PermissionDenied = "permission_denied";

        public const string InsufficientScope = "insufficient_scope";
        public const string FailedPrecondition = "failed_precondition";
        public const string NotFound = "not_found";
        public const string InvalidArgument = "invalid_argument";
        public const string Unavailable = "unavailable";
        public const string AlreadyExists = "already_exists";
        public const string Internal = "internal";
    }

    // A cancelled transport request never claims tool completion or durable job cancellation.
    public static McpJsonRpcError RequestCancelled() => new()
    {
        Code = -32800,
        Message = "Request cancelled before completion.",
        Data = new McpErrorData { Code = "cancelled" }
    };

    public static McpJsonRpcError RequestRegistryUnavailable() => new()
    {
        Code = JsonRpcServerError,
        Message = "The active request registry is unavailable.",
        Data = new McpErrorData { Code = Codes.Unavailable, Retryable = true }
    };

    public const int JsonRpcParseError = -32700;

    public const int JsonRpcInvalidRequest = -32600;

    public const int JsonRpcMethodNotFound = -32601;

    public const int JsonRpcInvalidParams = -32602;

    public const int JsonRpcServerError = -32000;

    public const int JsonRpcResourceNotFound = -32002;

    public static McpJsonRpcError Map(Exception ex) => ex switch
    {
        GeoprocessingAuthorizationException authEx when authEx.RequiresAuthentication => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = $"{authEx.Message} {AuthenticationGuidance}",
            Data = new McpErrorData
            {
                Code = Codes.Unauthenticated,
                RequiresReauthentication = true,
                StudioAuthorizationCode = authEx.PolicyCode
            }
        },

        GeoprocessingAuthorizationException authEx when authEx.DenialReason == AuthorizationDenialReason.InsufficientScope => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = authEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.InsufficientScope,
                StudioAuthorizationCode = authEx.PolicyCode
            }
        },

        GeoprocessingAuthorizationException authEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = authEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.PermissionDenied,
                StudioAuthorizationCode = authEx.PolicyCode
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
                PolicyRef = approvalEx.PolicyRef,
                // When the gated plan was persisted as a proposal (ADR-0064, #2814),
                // surface its id and the honua://proposals/{id} resume resource so an
                // agent can poll until a human resolves it instead of dead-ending.
                ProposalId = approvalEx.ProposalId,
                ResourceUri = approvalEx.ProposalId == null
                    ? null
                    : McpResourceUris.ProposalUri(approvalEx.ProposalId)
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
                Code = Codes.FailedPrecondition,
                CurrentGeneration = (preconditionEx as Studio.StudioDraftGenerationConflictException)?.CurrentGeneration,
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

        // honua-release#202: when the refusal names a dependency the install never composed
        // (Redis is optional; PostGIS is not), carry the machine-readable receipt and mark it
        // NOT retryable — retrying cannot help until an operator changes the deployment.
        CapabilityUnavailableException capabilityEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = capabilityEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.Unavailable,
                Retryable = false,
                MissingDependency = capabilityEx.MissingDependency,
                Capability = capabilityEx.Capability,
                Remediation = capabilityEx.Remediation,
                RemediationRef = capabilityEx.RemediationRef,
            }
        },

        GeoprocessingStoreUnavailableException storeEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = storeEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.Unavailable,
                Retryable = !storeEx.HasDependencyReceipt,
                MissingDependency = storeEx.MissingDependency,
                MissingEntitlement = storeEx.MissingEntitlement,
                Capability = storeEx.CapabilityId,
                Remediation = storeEx.Remediation,
                RemediationRef = storeEx.RemediationRef
            }
        },

        GeoprocessingIdempotencyConflictException conflictEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = conflictEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.AlreadyExists,
                ConflictingJobId = conflictEx.ConflictingJobId
            }
        },

        GroundingException groundingEx => MapGrounding(groundingEx),

        RasterRenderingUnavailableException renderEx => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = renderEx.Message,
            Data = new McpErrorData
            {
                Code = Codes.FailedPrecondition
            }
        },

        InvalidOperationException => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = "An internal MCP operation failed.",
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
            Message = "Metadata graph is unavailable.",
            Data = new McpErrorData { Code = Codes.Unavailable, Retryable = true }
        },
        _ => new McpJsonRpcError
        {
            Code = JsonRpcServerError,
            Message = exception.Message,
            Data = new McpErrorData { Code = Codes.Internal }
        }
    };

    public static McpJsonRpcError ParseError(string message) => new()
    {
        Code = JsonRpcParseError,
        Message = message,
        Data = new McpErrorData { Code = Codes.InvalidArgument }
    };

    public static McpJsonRpcError InvalidRequest(string message) => new()
    {
        Code = JsonRpcInvalidRequest,
        Message = message,
        Data = new McpErrorData { Code = Codes.InvalidArgument }
    };

    public static McpJsonRpcError MethodNotFound(string message) => new()
    {
        Code = JsonRpcMethodNotFound,
        Message = message,
        Data = new McpErrorData { Code = Codes.NotFound }
    };

    public static McpJsonRpcError InvalidArgument(string message) => new()
    {
        Code = JsonRpcInvalidParams,
        Message = message,
        Data = new McpErrorData { Code = Codes.InvalidArgument }
    };

    public static McpJsonRpcError ResourceNotFound(string message) => new()
    {
        Code = JsonRpcResourceNotFound,
        Message = message,
        Data = new McpErrorData { Code = Codes.NotFound }
    };

    public static McpJsonRpcError Unauthenticated() => new()
    {
        Code = JsonRpcServerError,
        Message = $"Authentication is required to use the MCP operator surface. {AuthenticationGuidance}",
        Data = new McpErrorData
        {
            Code = Codes.Unauthenticated,
            RequiresReauthentication = true
        }
    };

    public static McpJsonRpcError InvalidToken() => new()
    {
        Code = JsonRpcServerError,
        Message = $"The bearer token is invalid, expired, or issued for a different resource. {AuthenticationGuidance}",
        Data = new McpErrorData
        {
            Code = Codes.Unauthenticated,
            RequiresReauthentication = true
        }
    };

    public static McpJsonRpcError SessionPrincipalMismatch() => new()
    {
        Code = JsonRpcServerError,
        Message = "The MCP session is bound to a different principal. Re-initialize to obtain a session for the current identity.",
        Data = new McpErrorData
        {
            Code = Codes.PermissionDenied,
            RequiresReauthentication = true
        }
    };

    public static McpJsonRpcError SessionActorRequired() => new()
    {
        Code = JsonRpcServerError,
        Message = "Authenticated MCP sessions require a durable subject or API-key identity.",
        Data = new McpErrorData
        {
            Code = Codes.PermissionDenied,
            RequiresReauthentication = true
        }
    };

    public static McpJsonRpcError SessionCapacityReached(int retryAfterSeconds, string correlationId) => new()
    {
        Code = JsonRpcServerError,
        Message = "The MCP server is at session capacity. Retry shortly.",
        Data = new McpErrorData
        {
            Code = Codes.Unavailable,
            Retryable = true,
            RetryAfterSeconds = retryAfterSeconds,
            CorrelationId = correlationId,
        }
    };
}
