// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Verifies that <see cref="McpErrorMapper"/> translates every geoprocessing
/// domain exception and protocol-level failure into the canonical MCP error
/// envelope so clients can react to recoverable signals (approval, idempotency,
/// auth) without parsing message strings.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpErrorMappingTests
{
    private const int JsonRpcServerError = -32000;
    private const int JsonRpcInvalidParams = -32602;
    private const int JsonRpcMethodNotFound = -32601;
    private const int JsonRpcInvalidRequest = -32600;
    private const int JsonRpcParseError = -32700;
    private const int JsonRpcResourceNotFound = -32002;

    [UnitTest]
    public void AuthorizationException_RequiringAuthentication_MapsToUnauthenticatedWithReauthFlag()
    {
        var error = McpErrorMapper.Map(new GeoprocessingAuthorizationException(requiresAuthentication: true));

        error.Code.Should().Be(JsonRpcServerError);
        error.Data.Should().NotBeNull();
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Unauthenticated);
        error.Data.RequiresReauthentication.Should().BeTrue();
    }

    [UnitTest]
    public void AuthorizationException_WithoutAuthenticationRequirement_MapsToPermissionDenied()
    {
        var error = McpErrorMapper.Map(new GeoprocessingAuthorizationException(requiresAuthentication: false));

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.PermissionDenied);
        error.Data.RequiresReauthentication.Should().BeNull();
    }

    [UnitTest]
    public void ApprovalRequiredException_MapsToFailedPreconditionWithPolicyRef()
    {
        var error = McpErrorMapper.Map(new GeoprocessingApprovalRequiredException("policy/publish"));

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.FailedPrecondition);
        error.Data.ApprovalRequired.Should().BeTrue();
        error.Data.PolicyRef.Should().Be("policy/publish");
    }

    [UnitTest]
    public void NotFoundException_MapsToResourceNotFoundJsonRpcCode()
    {
        var error = McpErrorMapper.Map(new GeoprocessingNotFoundException("job-missing"));

        // MCP 2025-03-26 reserves -32002 for resource-not-found signals so
        // clients can distinguish missing records from generic server errors.
        error.Code.Should().Be(JsonRpcResourceNotFound);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.NotFound);
    }

    [UnitTest]
    public void PreconditionFailedException_MapsToFailedPreconditionWithoutApproval()
    {
        var error = McpErrorMapper.Map(new GeoprocessingPreconditionFailedException("already terminal"));

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.FailedPrecondition);
        error.Data.ApprovalRequired.Should().BeNull();
        error.Data.PolicyRef.Should().BeNull();
    }

    [UnitTest]
    public void ValidationException_MapsToInvalidArgumentWithInvalidParamsCode()
    {
        var error = McpErrorMapper.Map(new GeoprocessingValidationException("steps cannot be empty"));

        error.Code.Should().Be(JsonRpcInvalidParams);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.InvalidArgument);
    }

    [UnitTest]
    public void StoreUnavailableException_MapsToUnavailableWithRetryableFlag()
    {
        var error = McpErrorMapper.Map(new GeoprocessingStoreUnavailableException());

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Unavailable);
        error.Data.Retryable.Should().BeTrue();
    }

    [UnitTest]
    public void IdempotencyConflictException_MapsToAlreadyExists()
    {
        var error = McpErrorMapper.Map(new GeoprocessingIdempotencyConflictException());

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.AlreadyExists);
    }

    [UnitTest]
    public void InvalidOperationException_MapsToInternal()
    {
        var error = McpErrorMapper.Map(new InvalidOperationException("bad state"));

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Internal);
        error.Message.Should().Be("bad state");
    }

    [UnitTest]
    public void UnknownException_MapsToGenericInternalWithSanitizedMessage()
    {
        var error = McpErrorMapper.Map(new TimeoutException("leaking internal detail"));

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Internal);
        error.Message.Should().NotContain("leaking internal detail");
    }

    [UnitTest]
    public void InvalidArgumentFactory_UsesInvalidParamsJsonRpcCode()
    {
        var error = McpErrorMapper.InvalidArgument("missing method");

        error.Code.Should().Be(JsonRpcInvalidParams);
        error.Message.Should().Be("missing method");
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.InvalidArgument);
    }

    [UnitTest]
    public void ResourceNotFoundFactory_UsesResourceNotFoundJsonRpcCode()
    {
        var error = McpErrorMapper.ResourceNotFound("unknown resource 'honua://jobs/missing'");

        error.Code.Should().Be(JsonRpcResourceNotFound);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.NotFound);
    }

    [UnitTest]
    public void MethodNotFoundFactory_UsesMethodNotFoundJsonRpcCode()
    {
        var error = McpErrorMapper.MethodNotFound("unknown method 'frobnicate'");

        error.Code.Should().Be(JsonRpcMethodNotFound);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.NotFound);
    }

    [UnitTest]
    public void InvalidRequestFactory_UsesInvalidRequestJsonRpcCode()
    {
        var error = McpErrorMapper.InvalidRequest("jsonrpc must be \"2.0\".");

        error.Code.Should().Be(JsonRpcInvalidRequest);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.InvalidArgument);
    }

    [UnitTest]
    public void ParseErrorFactory_UsesParseErrorJsonRpcCode()
    {
        var error = McpErrorMapper.ParseError("unexpected token at position 3");

        error.Code.Should().Be(JsonRpcParseError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.InvalidArgument);
    }

    [UnitTest]
    public void UnauthenticatedFactory_EmitsReauthSignal()
    {
        var error = McpErrorMapper.Unauthenticated();

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Unauthenticated);
        error.Data.RequiresReauthentication.Should().BeTrue();
    }
}
