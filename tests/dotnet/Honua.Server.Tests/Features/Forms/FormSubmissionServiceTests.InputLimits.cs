// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.Server.Features.Forms;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Forms;

public sealed partial class FormSubmissionServiceTests
{
    [UnitTheory]
    [InlineData("multipart/form-data", "--b\r\nContent-Disposition: form-data; name=\"submission\"\r\n\r\n{}\r\n--b--\r\n")]
    [InlineData("multipart/form-data; boundary=b9553", "--b9553\r\nContent-Disposition: form-data; name=\"submission\"\r\n\r\n{\"opera")]
    public async Task SubmitAsync_MalformedMultipartBody_Returns400BeforeAnyWrite(string contentType, string body)
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(allowedOperations: [FormSubmissionOperations.Create]);
        var writer = new FakeFeatureWriter();
        var context = CreateJsonContext(body, requestServices);
        context.Request.ContentType = contentType;

        var result = await CreateService(store, writer).SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        store.CreateSubmissionCalls.Should().Be(0);
        writer.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTheory]
    [InlineData("idempotencyKey", FormSubmissionInputLimits.MaxIdempotencyKeyLength + 1)]
    [InlineData("clientId", FormSubmissionInputLimits.MaxClientIdLength + 1)]
    public async Task SubmitAsync_OverLongIdentifier_Returns400BeforeAnyWrite(string field, int length)
    {
        var (status, store) = await SubmitWithFieldAsync(field, new string('k', length));

        status.Should().Be(StatusCodes.Status400BadRequest);
        store.GetSubmissionByIdempotencyCalls.Should().Be(0);
        store.CreateSubmissionCalls.Should().Be(0);
    }

    [UnitTheory]
    [InlineData("idempotencyKey", FormSubmissionInputLimits.MaxIdempotencyKeyLength)]
    [InlineData("clientId", FormSubmissionInputLimits.MaxClientIdLength)]
    public async Task SubmitAsync_IdentifierAtLimit_IsAccepted(string field, int length)
    {
        var (status, store) = await SubmitWithFieldAsync(field, new string('k', length));

        status.Should().Be(StatusCodes.Status200OK);
        store.CreateSubmissionCalls.Should().Be(1);
    }

    [UnitTheory]
    [InlineData("idempotencyKey", "key\u0000tail")]
    [InlineData("clientId", "device\u0000tail")]
    [InlineData("idempotencyKey", "key\ttail")]
    public async Task SubmitAsync_ControlCharacterInIdentifier_Returns400BeforeAnyWrite(string field, string value)
    {
        var (status, store) = await SubmitWithFieldAsync(field, value);

        status.Should().Be(StatusCodes.Status400BadRequest);
        store.CreateSubmissionCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_UnpairedSurrogateInIdempotencyKey_Returns400BeforeAnyWrite()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(allowedOperations: [FormSubmissionOperations.Create]);
        var payload = SubmissionPayload("idempotencyKey", "SURROGATE").Replace("SURROGATE", "key\\ud800tail", StringComparison.Ordinal);
        var context = CreateJsonContext(payload, requestServices);

        var result = await CreateService(store, new FakeFeatureWriter()).SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        store.CreateSubmissionCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_OverLongClientIdHeader_Returns400BeforeAnyWrite()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(allowedOperations: [FormSubmissionOperations.Create]);
        var payload = SubmissionPayload("clientId", null);
        var context = CreateJsonContext(payload, requestServices);
        context.Request.Headers["X-Honua-Client-Id"] = new string('h', FormSubmissionInputLimits.MaxClientIdLength + 1);

        var result = await CreateService(store, new FakeFeatureWriter()).SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        store.CreateSubmissionCalls.Should().Be(0);
    }

    private static async Task<(int Status, FakeFormPackageStore Store)> SubmitWithFieldAsync(string field, string value)
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(allowedOperations: [FormSubmissionOperations.Create]);
        var context = CreateJsonContext(SubmissionPayload(field, value), requestServices);

        var result = await CreateService(store, new FakeFeatureWriter()).SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, store);
    }

    // Replaces (or, for a null value, removes) one property of a valid create submission.
    private static string SubmissionPayload(string field, string? value)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            CreateSubmission("input-limits-key"),
            FormPackageJsonContext.Default.FormSubmissionRequest))!.AsObject();
        node.Remove(field);
        if (value is not null)
        {
            node[field] = value;
        }

        return node.ToJsonString();
    }
}
