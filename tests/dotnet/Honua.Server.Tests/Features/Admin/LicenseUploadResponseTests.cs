// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public class LicenseUploadResponseTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("License applied.")]
    [InlineData("License applied to the persisted upload override; the LicensePath mirror could not be updated. See server logs for details.")]
    public async Task UploadLicense_PreservesPersistenceMessageAlongsideAppliedStatus(string message)
    {
        var provider = Substitute.For<ILicenseStatusProvider>();
        provider.UploadLicenseAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new LicenseUploadResult(true, message));
        provider.GetCurrentStatus().Returns(new LicenseStatus(
            HonuaEdition.Enterprise, true, DateTimeOffset.UtcNow.AddDays(30), "Test operator"));
        var capacity = Substitute.For<ILicenseCapacityMeter>();
        using var body = new MemoryStream([1, 2, 3]);
        var context = new DefaultHttpContext();
        context.Request.Body = body;

        var handler = typeof(LicenseEndpoints).GetMethod("HandleUploadLicense",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(handler);
        var task = Assert.IsType<Task<Results<Ok<ApiResponse<LicenseStatusResponse>>,
            BadRequest<ApiResponse<object>>, ProblemHttpResult>>>(handler.Invoke(null,
            [provider, capacity, Options.Create(new LicenseOptions()),
                NullLogger<LicenseEndpoints.LicenseEndpointsLog>.Instance, context]), exactMatch: false);
        var result = await task;
        var response = Assert.IsType<Ok<ApiResponse<LicenseStatusResponse>>>(result.Result);
        Assert.NotNull(response.Value);
        Assert.True(response.Value.Success);
        Assert.Equal(message, response.Value.Message);
        Assert.NotNull(response.Value.Data);
        Assert.Equal("Enterprise", response.Value.Data.Edition);
        Assert.True(response.Value.Data.IsValid);
        await provider.Received(1).UploadLicenseAsync(body, context.RequestAborted);
    }
}
