// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

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
    public void AuthorizationException_ScopeDenial_MapsToInsufficientScopeDistinctFromPermissionDenied()
    {
        // A scope denial (#2851) must be a distinct structured reason from a grant denial so an
        // operator can tell an under-scoped agent token apart from an under-privileged principal.
        var error = McpErrorMapper.Map(new GeoprocessingAuthorizationException(
            requiresAuthentication: false,
            "The access token's scopes do not permit 'Execute' on Process.",
            denialReason: AuthorizationDenialReason.InsufficientScope));

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.InsufficientScope);
        error.Data.Code.Should().NotBe(McpErrorMapper.Codes.PermissionDenied);
        error.Data.RequiresReauthentication.Should().BeNull(
            "an insufficient scope is not fixed by re-authenticating; a differently-scoped token is required");
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
    public void StoreUnavailableException_WithDependencyReceipt_MapsToUnavailableWithMissingDependency()
    {
        // honua-release#202: Redis is optional for a local install, so an absent durable job
        // store is a deployment fact, not a blip. The envelope must name the missing dependency,
        // the capability it disables, and the remediation — and must NOT be marked retryable,
        // because retrying cannot help until an operator changes the install.
        var error = McpErrorMapper.Map(new GeoprocessingStoreUnavailableException());

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Unavailable);
        error.Data.Retryable.Should().BeFalse();
        error.Data.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
        error.Data.Capability.Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        error.Data.Remediation.Should().Be(CapabilityUnavailableCodes.RedisRemediation);
        error.Data.RemediationRef.Should().Be(CapabilityUnavailableCodes.RedisRemediationRef);
    }

    [UnitTest]
    public void StoreUnavailableException_ToolResultEnvelope_CarriesCapabilityUnavailableReceipt()
    {
        // MCP tools report failures as an isError result with a structured envelope rather than
        // a JSON-RPC error, so the receipt has to survive that projection too — otherwise the
        // agent-facing surface is the one place that loses it (honua-release#202).
        var result = Honua.Ai.Protocols.Mcp.Tools.McpToolHelpers.ErrorResult(
            new GeoprocessingStoreUnavailableException());

        result.IsError.Should().BeTrue();
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.Unavailable);
        structured.GetProperty("retryable").GetBoolean().Should().BeFalse();
        structured.GetProperty("missingDependency").GetString()
            .Should().Be(CapabilityUnavailableCodes.RedisDependency);
        structured.GetProperty("capability").GetString()
            .Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        structured.GetProperty("remediationRef").GetString()
            .Should().Be(CapabilityUnavailableCodes.RedisRemediationRef);
        structured.GetProperty("error").GetProperty("kind").GetString()
            .Should().Be("PreconditionFailed", "nothing executed, so this is not an execution failure");
    }

    [UnitTest]
    public void StoreUnavailableException_UnentitledRedis_ReportsLicenseCodeAndEntitlementNotMissingRedis()
    {
        // honua-release#202 follow-up: Redis is deployed but the Pro `caching.redis` entitlement is
        // absent. Telling the caller a Redis dependency is missing would send an operator to
        // reinstall something already running, so the receipt names the entitlement instead.
        var error = McpErrorMapper.Map(
            GeoprocessingStoreUnavailableException.ForCause(DurableJobSubstrateCause.RedisNotEntitled));

        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Unavailable);
        error.Data.Retryable.Should().BeFalse();
        error.Data.MissingDependency.Should().BeNull("Redis is present; nothing is missing but a licence");
        error.Data.MissingEntitlement.Should().Be(CapabilityUnavailableCodes.RedisCacheEntitlement);
        error.Data.Capability.Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        error.Data.Remediation.Should().Be(CapabilityUnavailableCodes.EntitlementRemediation);
        error.Data.Remediation.Should().NotContain("Set ConnectionStrings__Redis");
    }

    [UnitTest]
    public void StoreUnavailableException_IncompleteSubstrate_NamesTheMissingQueue()
    {
        // Store without queue: submissions would persist and never drain, so the refusal names the
        // queue rather than blaming an absent Redis.
        var error = McpErrorMapper.Map(
            GeoprocessingStoreUnavailableException.ForCause(DurableJobSubstrateCause.RuntimeIncomplete));

        error.Data!.MissingDependency.Should().Be(CapabilityUnavailableCodes.JobQueueDependency);
        error.Data.MissingEntitlement.Should().BeNull();
    }

    [UnitTest]
    public void StoreUnavailableException_WithoutDependencyReceipt_StaysRetryableAndCarriesNoDependency()
    {
        // Other adapters reuse this exception as a generic "upstream unavailable" channel
        // (geocoding providers, style catalog). Those carry no install-level receipt, so they
        // keep the retryable semantics and advertise no missing dependency.
        var error = McpErrorMapper.Map(
            new GeoprocessingStoreUnavailableException("The geocoding provider is unavailable."));

        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Unavailable);
        error.Data.Retryable.Should().BeTrue();
        error.Data.MissingDependency.Should().BeNull();
        error.Data.Capability.Should().BeNull();
    }

    [UnitTest]
    public void SessionCapacityReached_PreservesDelayAndCorrelationMetadata()
    {
        var error = McpErrorMapper.SessionCapacityReached(5, "mcp-capacity-correlation");

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Unavailable);
        error.Data.Retryable.Should().BeTrue();
        error.Data.RetryAfterSeconds.Should().Be(5);
        error.Data.CorrelationId.Should().Be("mcp-capacity-correlation");
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
        // The raw exception message is deliberately not surfaced to MCP clients
        // (it can leak internal state); the mapper returns a sanitized message.
        error.Message.Should().Be("An internal MCP operation failed.");
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

    [UnitTest]
    public void InvalidTokenFactory_EmitsUnauthenticatedWithReauthSignal()
    {
        // A presented-but-invalid bearer token (bad signature, expired, wrong issuer, or an
        // audience minted for another resource) is an RFC 6750 invalid_token rejection. It
        // shares the unauthenticated code and re-authentication signal with the no-credential
        // case so a client reacts identically (#2850).
        var error = McpErrorMapper.InvalidToken();

        error.Code.Should().Be(JsonRpcServerError);
        error.Data!.Code.Should().Be(McpErrorMapper.Codes.Unauthenticated);
        error.Data.RequiresReauthentication.Should().BeTrue();
    }
}
