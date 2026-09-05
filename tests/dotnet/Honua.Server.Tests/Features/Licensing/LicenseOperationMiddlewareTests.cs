// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

public sealed class LicenseOperationMiddlewareTests
{
    [Theory]
    [InlineData(HonuaEdition.Pro)]
    [InlineData(HonuaEdition.Enterprise)]
    [InlineData(HonuaEdition.Community)]
    [Trait("Tier", "Fast")]
    public async Task ExistingData_ReadAndExport_StopAtPaidExpiryOnly(HonuaEdition edition)
    {
        var clock = new LicenseFailureContractTests.TransitionClock();
        var license = LicenseTestSupport.CreateSignedLicense(edition, clock.GetUtcNow().AddMinutes(2));
        using var policy = new FileBackedLicenseService(Options.Create(new LicenseOptions
        {
            Edition = edition,
            LicenseContent = Encoding.UTF8.GetString(license.LicenseData),
            TrustedKeys = new() { [LicenseTestSupport.KeyId] = license.PublicKeySetting }
        }), new BouncyCastleEd25519Verifier(), NullLogger<FileBackedLicenseService>.Instance, timeProvider: clock);
        await policy.StartAsync(CancellationToken.None);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILicenseOperationPolicy>(policy);
        await using var app = builder.Build();
        app.UseMiddleware<LicenseOperationMiddleware>();
        var reads = 0;
        app.MapGet("/existing", () => { reads++; return Results.Text("persisted-feature"); });
        app.MapPost("/exports", () => { reads++; return Results.Text("existing-export"); });
        app.MapGet("/api/v1/admin/license/status", () => Results.Text("license-recovery"));
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal("persisted-feature", await client.GetStringAsync("/existing"));
        using var beforeExport = await client.PostAsync("/exports", null);
        Assert.Equal("existing-export", await beforeExport.Content.ReadAsStringAsync());

        clock.Advance(TimeSpan.FromMinutes(2));
        using var read = await client.GetAsync("/existing");
        using var export = await client.PostAsync("/exports", null);
        var expected = edition == HonuaEdition.Community ? HttpStatusCode.OK : HttpStatusCode.PaymentRequired;
        Assert.Equal(expected, read.StatusCode);
        Assert.Equal(expected, export.StatusCode);
        Assert.Equal(edition == HonuaEdition.Community ? 4 : 2, reads);
        Assert.Equal("license-recovery", await client.GetStringAsync("/api/v1/admin/license/status"));
        await policy.StopAsync(CancellationToken.None);
    }
}
