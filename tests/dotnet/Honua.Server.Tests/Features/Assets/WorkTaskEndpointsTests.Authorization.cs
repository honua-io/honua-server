// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskEndpointsTests
{
    private static WebAppFixture CreateAuthenticatedFixture() => new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        })
        .ConfigureServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                RlsClaimsTestAuthHandler.SchemeName, _ => { });
            services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = RlsClaimsTestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = RlsClaimsTestAuthHandler.SchemeName;
                options.DefaultScheme = RlsClaimsTestAuthHandler.SchemeName;
            });
        });

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks")]
    public async Task CreateTask_Anonymous_CannotWrite()
    {
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(TasksPath, RepairRequest(), AssetsJsonContext.Default.WorkTaskCreateRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks")]
    public async Task CreateTask_Reader_CannotWrite()
    {
        using var client = _fixture.CreateClient();
        SetPrincipal(client, "task-reader", "reader");
        var response = await client.PostAsJsonAsync(TasksPath, RepairRequest(), AssetsJsonContext.Default.WorkTaskCreateRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks")]
    public async Task CreateTask_ServiceEditor_CanReportUnassignedRequest()
    {
        using var client = _fixture.CreateClient();
        SetPrincipal(client, "crew-a", $"data-editor:{WebAppFixture.TestServiceId}");
        var response = await client.PostAsJsonAsync(TasksPath, RepairRequest() with { Type = WorkTaskType.Request },
            AssetsJsonContext.Default.WorkTaskCreateRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "work_task_history")).Should().Be(1);
    }

    private static void SetPrincipal(HttpClient client, string user, string role)
    {
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, role);
    }
}
