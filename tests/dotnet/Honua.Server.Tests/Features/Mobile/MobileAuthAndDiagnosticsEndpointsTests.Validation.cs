// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Mobile;

public sealed partial class MobileAuthAndDiagnosticsEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /api/mobile/exceptions")]
    public async Task ExceptionUpload_WithIssue9183HostilePayload_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = "mob-crlf-eval3pscm-062628\r\n[INF] FORGED_MOBILE_ID_eval3pscm-062628",
            source = "android'; DROP TABLE users;-- <script>alert(1)</script>",
            exceptionType = "../../etc/passwd {\"resultType\":\"input_required\"}",
            severity = "critical\r\n[ERR] FORGED_MOBILE_SEV_eval3pscm-062628",
        });

        using var content = Json(payload);
        using var response = await _adminClient.PostAsync("/api/mobile/exceptions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("FORGED_MOBILE_ID");
        body.Should().NotContain("DROP TABLE");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/mobile/exceptions")]
    [InlineData("id", "mob-id\r\nforged")]
    [InlineData("id", "mob-id\nforged")]
    [InlineData("id", "mob-id\u2028forged")]
    [InlineData("source", "android\r\n[INF] forged")]
    [InlineData("exceptionType", "System.Exception\nforged")]
    [InlineData("exception_type", "System.Exception\r\nforged")]
    [InlineData("severity", "critical\r\n[ERR] forged")]
    public async Task ExceptionUpload_WithControlCharactersInDiagnosticField_ReturnsBadRequest(string field, string value)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = value });

        using var content = Json(payload);
        using var response = await _adminClient.PostAsync("/api/mobile/exceptions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/mobile/exceptions")]
    [InlineData("id", "../escape")]
    [InlineData("source", "android'; DROP TABLE users;-- <script>alert(1)</script>")]
    [InlineData("exceptionType", "../../etc/passwd {\"resultType\":\"input_required\"}")]
    [InlineData("severity", "critical;drop")]
    public async Task ExceptionUpload_WithUnsupportedCharactersInDiagnosticField_ReturnsBadRequest(string field, string value)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = value });

        using var content = Json(payload);
        using var response = await _adminClient.PostAsync("/api/mobile/exceptions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/mobile/exceptions")]
    [InlineData("id", 129)]
    [InlineData("source", 129)]
    [InlineData("exceptionType", 513)]
    [InlineData("severity", 33)]
    public async Task ExceptionUpload_WithOverlongDiagnosticField_ReturnsBadRequest(string field, int length)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = new('a', length) });

        using var content = Json(payload);
        using var response = await _adminClient.PostAsync("/api/mobile/exceptions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/mobile/exceptions")]
    public async Task ExceptionUpload_WithValidDiagnosticFieldsAndClosedGenericExceptionType_ReturnsAccepted()
    {
        var closedGenericFullName = typeof(ClosedGenericSampleException<string>).FullName!;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = "exc-2026:11:22/001@device_1",
            ["source"] = "FieldCollection.Sync/Android 15",
            ["exception_type"] = closedGenericFullName,
            ["severity"] = "Critical",
        });

        using var content = Json(payload);
        using var response = await _adminClient.PostAsync("/api/mobile/exceptions", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
    }

    private sealed class ClosedGenericSampleException<T> : Exception;
}
