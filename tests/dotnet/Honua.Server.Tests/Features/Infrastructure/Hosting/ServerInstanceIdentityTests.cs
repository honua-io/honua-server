// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Hosting;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Hosting;

/// <summary>
/// Resolution precedence and header behaviour of the per-instance identity the release harness
/// uses to prove a cross-replica read (owner decision 2, 2026-10-10).
/// </summary>
[Operation(Operations.TestInfrastructure)]
public sealed class ServerInstanceIdentityTests
{
    private const string FargateMetadataUri =
        "http://169.254.170.2/v4/0123456789abcdef0123456789abcdef-3226046565";

    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero));

    [UnitTest]
    public void Resolve_ConfiguredInstanceId_WinsOverEveryEnvironmentSource()
    {
        var identity = Resolve(
            configured: "replica-a",
            env: AllEnvironmentSources(),
            machineName: "machine");

        identity.InstanceId.Should().Be("replica-a");
        identity.Source.Should().Be(ServerInstanceIdentitySources.Configuration);
        identity.StartedAt.Should().Be(Clock.GetUtcNow());
    }

    [UnitTest]
    public void Resolve_LambdaLogStream_WinsOverEcsAndHostname()
    {
        var identity = Resolve(configured: null, env: AllEnvironmentSources(), machineName: "machine");

        identity.InstanceId.Should().Be("2026/10/10/[$LATEST]58d1c3f2a9b04e6f8a7c5d3e1f0b2a49");
        identity.Source.Should().Be(ServerInstanceIdentitySources.AwsLambda);
    }

    [UnitTest]
    public void Resolve_EcsFargateMetadataUri_UsesTaskArnSuffix()
    {
        var env = AllEnvironmentSources();
        env.Remove("AWS_LAMBDA_LOG_STREAM_NAME");

        var identity = Resolve(configured: null, env: env, machineName: "machine");

        identity.InstanceId.Should().Be("0123456789abcdef0123456789abcdef");
        identity.Source.Should().Be(ServerInstanceIdentitySources.AwsEcsTask);
    }

    [UnitTest]
    public void Resolve_EcsMetadataUriWithoutTaskId_HashesTheSegmentInsteadOfEchoingIt()
    {
        var env = new Dictionary<string, string?>
        {
            ["ECS_CONTAINER_METADATA_URI_V4"] = "http://169.254.170.2/v4/ec2-container-7f3a/",
            ["HOSTNAME"] = "host-a"
        };

        var identity = Resolve(configured: null, env: env, machineName: null);

        identity.Source.Should().Be(ServerInstanceIdentitySources.AwsEcsContainer);
        identity.InstanceId.Should().MatchRegex("^ecs-[0-9a-f]{16}$");
        identity.InstanceId.Should().NotContain("ec2-container-7f3a");
        Resolve(configured: null, env: env, machineName: null).InstanceId.Should().Be(identity.InstanceId);
    }

    [UnitTest]
    public void Resolve_Hostname_WinsOverMachineName()
    {
        var identity = Resolve(
            configured: null,
            env: new Dictionary<string, string?> { ["HOSTNAME"] = "honua-server-7d9f8b6c4-x2k8p" },
            machineName: "machine");

        identity.InstanceId.Should().Be("honua-server-7d9f8b6c4-x2k8p");
        identity.Source.Should().Be(ServerInstanceIdentitySources.Hostname);
    }

    [UnitTest]
    public void Resolve_NoEnvironmentSource_FallsBackToMachineName()
    {
        var identity = Resolve(configured: "  ", env: [], machineName: "BUILD-HOST-01");

        identity.InstanceId.Should().Be("BUILD-HOST-01");
        identity.Source.Should().Be(ServerInstanceIdentitySources.MachineName);
    }

    [UnitTest]
    public void Resolve_NothingAvailable_GeneratesADistinctRandomIdPerProcess()
    {
        var first = Resolve(configured: null, env: [], machineName: null);
        var second = Resolve(configured: null, env: [], machineName: null);

        first.Source.Should().Be(ServerInstanceIdentitySources.Generated);
        first.InstanceId.Should().MatchRegex("^[0-9a-f]{32}$");
        second.InstanceId.Should().NotBe(first.InstanceId);
    }

    [UnitTest]
    public void Resolve_TwoHosts_ProduceDifferentIds()
    {
        var hostA = Resolve(configured: null, env: new Dictionary<string, string?> { ["HOSTNAME"] = "pod-a" }, machineName: null);
        var hostB = Resolve(configured: null, env: new Dictionary<string, string?> { ["HOSTNAME"] = "pod-b" }, machineName: null);

        hostA.InstanceId.Should().NotBe(hostB.InstanceId);
    }

    [UnitTest]
    public void Resolve_UnsafeCharacters_AreReplacedAndLengthIsCapped()
    {
        var identity = Resolve(configured: "replica a\r\nX-Injected: 1" + new string('z', 300), env: [], machineName: null);

        identity.InstanceId.Should().StartWith("replica-a--X-Injected:-1");
        identity.InstanceId.Should().HaveLength(ServerInstanceIdentity.MaxLength);
        identity.InstanceId.Should().NotContainAny("\r", "\n", " ");
    }

    [UnitTest]
    public void AddServerInstanceIdentity_ResolvesOncePerProcess()
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddServerInstanceIdentity()
            .AddServerInstanceIdentity();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IServerInstanceIdentity>();
        var second = provider.GetRequiredService<IServerInstanceIdentity>();

        second.Should().BeSameAs(first);
        services.Count(static d => d.ServiceType == typeof(IServerInstanceIdentity)).Should().Be(1);
    }

    [UnitTest]
    public async Task HeaderMiddleware_StampsSuccessAndClearedErrorResponses()
    {
        await using var app = await StartPipelineAsync("replica-a");
        using var client = app.GetTestClient();

        using var ok = await client.GetAsync("/ok");
        ok.Headers.GetValues(ServerInstanceIdentity.HeaderName).Should().ContainSingle().Which.Should().Be("replica-a");

        // An error envelope that clears the response (as exception handlers do) still carries it.
        using var cleared = await client.GetAsync("/cleared");
        cleared.StatusCode.Should().Be(System.Net.HttpStatusCode.InternalServerError);
        cleared.Headers.GetValues(ServerInstanceIdentity.HeaderName).Should().ContainSingle().Which.Should().Be("replica-a");

        // A spoofed upstream value set by a later component is overwritten with this replica's id.
        using var spoofed = await client.GetAsync("/spoofed");
        spoofed.Headers.GetValues(ServerInstanceIdentity.HeaderName).Should().ContainSingle().Which.Should().Be("replica-a");
    }

    private static async Task<WebApplication> StartPipelineAsync(string instanceId)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ServerInstanceIdentity.ConfigurationKey] = instanceId
        });
        builder.Services.AddServerInstanceIdentity();

        var app = builder.Build();
        app.UseServerInstanceHeader();
        app.MapGet("/ok", static () => "ok");
        app.MapGet("/cleared", static async (HttpContext context) =>
        {
            context.Response.Headers["X-Before-Clear"] = "1";
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("error");
        });
        app.MapGet("/spoofed", static async (HttpContext context) =>
        {
            context.Response.Headers[ServerInstanceIdentity.HeaderName] = "other-replica";
            await context.Response.WriteAsync("ok");
        });

        await app.StartAsync();
        return app;
    }

    private static ServerInstanceIdentity Resolve(
        string? configured,
        Dictionary<string, string?> env,
        string? machineName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServerInstanceIdentity.ConfigurationKey] = configured
            })
            .Build();

        return ServerInstanceIdentity.Resolve(
            configuration,
            name => env.TryGetValue(name, out var value) ? value : null,
            machineName,
            Clock);
    }

    private static Dictionary<string, string?> AllEnvironmentSources() => new()
    {
        ["AWS_LAMBDA_LOG_STREAM_NAME"] = "2026/10/10/[$LATEST]58d1c3f2a9b04e6f8a7c5d3e1f0b2a49",
        ["ECS_CONTAINER_METADATA_URI_V4"] = FargateMetadataUri,
        ["HOSTNAME"] = "ip-10-0-1-23.ec2.internal"
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
