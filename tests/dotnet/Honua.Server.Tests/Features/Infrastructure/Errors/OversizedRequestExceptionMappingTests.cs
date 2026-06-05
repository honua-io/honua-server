// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

/// <summary>
/// BUG A regression: oversized request bodies / form values must map to a clean HTTP 413
/// envelope without leaking the framework's internal limit message (for example
/// "Form value length limit 4194304 exceeded."), even when debug details are enabled.
/// </summary>
public sealed class OversizedRequestExceptionMappingTests
{
    // Framework FormReader/FormPipeReader message shape for form key/value length limits.
    private const string FormLimitMessage =
        "Form key length limit 2048 or value length limit 4194304 exceeded.";

    [UnitTest]
    public void FromException_FormValueLengthLimitExceeded_MapsTo413()
    {
        var exception = new InvalidDataException(FormLimitMessage);

        var response = StandardErrorResponse.FromException(exception, includeDebugDetails: false);

        response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        response.Title.Should().Be("Payload Too Large");
    }

    [UnitTest]
    public void FromException_FormValueLengthLimitExceeded_DoesNotLeakInternalLimitMessage()
    {
        var exception = new InvalidDataException(FormLimitMessage);

        // Even with debug details enabled (development mode), the internal limit message
        // must never be surfaced to clients.
        var response = StandardErrorResponse.FromException(exception, includeDebugDetails: true);

        response.Detail.Should().NotContain("length limit");
        response.Detail.Should().NotContain("4194304");
        response.Detail.Should().NotContain("exceeded");
        response.DebugInfo.Should().BeNull();
    }

    [UnitTest]
    public void ToServiceError_FormValueLengthLimitExceeded_HasNoLimitLeakInAnyDetail()
    {
        var exception = new InvalidDataException(FormLimitMessage);

        ServiceError error = ExceptionMapper.ToServiceError(exception, includeDebugDetails: true);

        error.Code.Should().Be("413");
        var allText = string.Join(" ", new[] { error.Message }
            .Concat(error.Details ?? Array.Empty<string>()));
        allText.Should().NotContain("4194304");
        allText.Should().NotContain("value length limit");
        // The framework's "Debug: ... exceeded." string must not appear.
        allText.Should().NotContain("Debug:");
    }

    [UnitTest]
    public void FromException_RequestBodyTooLarge_MapsTo413WithCleanDetail()
    {
        // MaxRequestBodySize surfaces as BadHttpRequestException with StatusCode 413.
        var exception = new BadHttpRequestException(
            "Request body too large.",
            StatusCodes.Status413PayloadTooLarge);

        var response = StandardErrorResponse.FromException(exception, includeDebugDetails: true);

        response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        response.Detail.Should().Be("The request body exceeds the maximum allowed size.");
        response.DebugInfo.Should().BeNull();
    }

    [UnitTest]
    public void FromException_GenericBadHttpRequest_StillMapsTo400()
    {
        // A non-size BadHttpRequestException (e.g. malformed body) must keep its 400 mapping
        // and must not be misclassified as a payload-too-large error.
        var exception = new BadHttpRequestException("Unexpected end of request content.");

        var response = StandardErrorResponse.FromException(exception, includeDebugDetails: false);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
