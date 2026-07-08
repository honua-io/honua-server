// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Local-substrate bridge coverage for the full server host: MCP proposal, admin approval,
/// durable Redis workflow storage, the deploy reconciler, release timeline events, and the
/// YARP/docker self-hosted rolling backend all participate in one rollback cell (#2569).
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.LocalSubstrate)]
public sealed class LocalSubstrateAgentRollbackBridgeTests : IClassFixture<LocalSubstrateDockerFixture>
{
    private const string AdminPassword = "local-substrate-rollback-admin-key";
    private const string RedisConnectionStringEnvVar = "HONUA_TEST_REDIS_URL";
    private const string McpSessionHeaderName = "Mcp-Session-Id";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly LocalSubstrateDockerFixture _docker;

    public LocalSubstrateAgentRollbackBridgeTests(LocalSubstrateDockerFixture docker)
    {
        _docker = docker;
    }

    [SkippableFact]
    public async Task AgentRollback_ServerHostBridge_ApprovesForwardDeployToPreviousRevisionWithZeroDowntime()
    {
        Skip.IfNot(_docker.Available, "Docker is not available for the local-substrate serving lane.");

        var redisConnectionString = await ResolveReachableRedisConnectionStringAsync();
        Skip.If(
            string.IsNullOrWhiteSpace(redisConnectionString),
            $"Set {RedisConnectionStringEnvVar}=host:port to run the Redis-backed local-substrate server bridge cell.");

        await using var env = await ServerHostBridgeEnvironment.StartAsync(_docker, redisConnectionString!);
        using var verificationClient = new HttpClient { BaseAddress = new Uri(env.ProxyBaseUrl) };
        using var trafficClient = new HttpClient { BaseAddress = new Uri(env.ProxyBaseUrl) };
        using var adminClient = env.Factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        var proxyServesV1 = await WaitForClientBodyAsync(
            verificationClient,
            LocalSubstrateDockerFixture.V1Marker,
            TimeSpan.FromSeconds(20));
        proxyServesV1.Should().BeTrue("the bridged local-substrate proxy should serve the initial revision");

        using var loop = RequestLoop.Start(trafficClient);

        var initialOperationId = await env.CreateInitialDeployAsync(_docker);
        var initialTerminal = await env.DriveDeployToSucceededAsync(
            initialOperationId,
            _docker.V2Image,
            TimeSpan.FromSeconds(90));
        initialTerminal.Deploy!.DesiredRevision.Should().Be(_docker.V2Image);
        env.ActiveProxyDestination().Should().Be(env.InitialStandbyDestination);

        var proxyServesV2 = await WaitForClientBodyAsync(
            verificationClient,
            LocalSubstrateDockerFixture.V2Marker,
            TimeSpan.FromSeconds(20));
        proxyServesV2.Should().BeTrue("revision B should be serving before the agent proposes rollback");

        var agentApiKey = await CreateAgentApiKeyAsync(adminClient, env.AgentName);
        using var mcpClient = env.Factory.CreateClient();
        mcpClient.DefaultRequestHeaders.Add("X-API-Key", agentApiKey);

        var proposalId = await ProposeRollbackDeployAsync(env, mcpClient);
        var rollbackOperationId = await ApproveProposalAsync(adminClient, proposalId);

        var rollbackTerminal = await env.DriveDeployToSucceededAsync(
            rollbackOperationId,
            _docker.V1Image,
            TimeSpan.FromSeconds(90));
        rollbackTerminal.Deploy!.DesiredRevision.Should().Be(_docker.V1Image);
        env.ActiveProxyDestination().Should().Be(env.InitialActiveDestination);

        var proxyServesV1Again = await WaitForClientBodyAsync(
            verificationClient,
            LocalSubstrateDockerFixture.V1Marker,
            TimeSpan.FromSeconds(20));
        proxyServesV1Again.Should().BeTrue("the approved rollback deploy should put revision A back behind the bridged proxy");

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var loopResult = await loop.StopAsync();

        loopResult.Failures.Should().Be(0, "the server-host bridge should keep serving while B promotes and A is redeployed");
        loopResult.Total.Should().BeGreaterThan(0);
        loopResult.SawV1.Should().BeTrue();
        loopResult.SawV2.Should().BeTrue();
        loopResult.LastBody.Should().Contain(LocalSubstrateDockerFixture.V1Marker);

        env.ReleaseEvents()
            .Where(e => e.OperationId == rollbackOperationId)
            .Select(e => e.Title)
            .Should()
            .Contain(title => title.StartsWith("Deploy submitted", StringComparison.Ordinal))
            .And.Contain(title => title.StartsWith("Deploy promoted", StringComparison.Ordinal));
    }

    private static async Task<string?> ResolveReachableRedisConnectionStringAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(RedisConnectionStringEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        return await CanOpenTcpConnectionAsync(connectionString).ConfigureAwait(false)
            ? connectionString
            : null;
    }

    private static async Task<bool> CanOpenTcpConnectionAsync(string connectionString)
    {
        if (!TryParseRedisEndpoint(connectionString, out var host, out var port))
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool TryParseRedisEndpoint(string connectionString, out string host, out int port)
    {
        host = string.Empty;
        port = 6379;

        var endpoint = connectionString.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            port = uri.Port > 0 ? uri.Port : 6379;
            return true;
        }

        var separator = endpoint.LastIndexOf(':');
        if (separator > 0 && separator < endpoint.Length - 1)
        {
            host = endpoint[..separator];
            return int.TryParse(
                endpoint[(separator + 1)..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out port);
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        host = endpoint;
        return true;
    }

    private static async Task<string> CreateAgentApiKeyAsync(HttpClient adminClient, string name)
    {
        var request = new CreateAdminApiKeyRequest
        {
            Name = name,
            Permissions = ["read:layers"]
        };

        using var response = await adminClient.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            request,
            JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = JsonSerializer.Deserialize<ApiResponse<AdminApiKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(),
            JsonOptions);

        result?.Data?.Key.Should().NotBeNullOrWhiteSpace();
        return result!.Data!.Key;
    }

    private static async Task<string> ProposeRollbackDeployAsync(
        ServerHostBridgeEnvironment env,
        HttpClient mcpClient)
    {
        var sessionId = await InitializeMcpSessionAsync(mcpClient);
        var executionPayload = JsonSerializer.Serialize(
            new
            {
                targetId = env.TargetId,
                desiredRevision = env.PreviousRevision,
                currentRevision = env.CurrentRevision,
                parameterOverrides = env.RollbackParameterOverrides
            },
            JsonOptions);

        var request = new
        {
            jsonrpc = "2.0",
            id = "rollback-proposal",
            method = "tools/call",
            @params = new
            {
                name = "honua_propose_operation",
                arguments = new
                {
                    kind = "Deploy",
                    reason = "Rollback revision B to revision A from the local-substrate bridge cell.",
                    idempotencyKey = $"rollback-{env.Suffix}",
                    executionPayload
                }
            }
        };

        using var response = await PostMcpAsync(mcpClient, request, sessionId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"MCP rollback proposal returned {error.GetRawText()}.");
        }

        var structured = document.RootElement
            .GetProperty("result")
            .GetProperty("structuredContent");

        structured.GetProperty("outcome").GetString().Should().Be("ProposalCreated");
        structured.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        structured.GetProperty("supportedKinds")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Contain("Deploy");

        var proposalId = structured.GetProperty("proposalId").GetString();
        proposalId.Should().NotBeNullOrWhiteSpace();
        return proposalId!;
    }

    private static async Task<string> InitializeMcpSessionAsync(HttpClient mcpClient)
    {
        var initialize = new
        {
            jsonrpc = "2.0",
            id = "init",
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new
                {
                    name = "local-substrate-rollback-bridge",
                    version = "1.0"
                }
            }
        };

        using var response = await PostMcpAsync(mcpClient, initialize, sessionId: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(McpSessionHeaderName, out var values).Should().BeTrue();

        var sessionId = values!.Single();
        var initialized = new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        };

        using var notification = await PostMcpAsync(mcpClient, initialized, sessionId);
        notification.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return sessionId;
    }

    private static async Task<HttpResponseMessage> PostMcpAsync(
        HttpClient client,
        object request,
        string? sessionId)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            message.Headers.Add(McpSessionHeaderName, sessionId);
        }

        return await client.SendAsync(message);
    }

    private static async Task<string> ApproveProposalAsync(HttpClient adminClient, string proposalId)
    {
        using var response = await adminClient.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Submitted");

        var operationId = document.RootElement.GetProperty("executionOperationId").GetString();
        operationId.Should().NotBeNullOrWhiteSpace();
        return operationId!;
    }

    private static async Task<bool> WaitForClientBodyAsync(
        HttpClient client,
        string expectedBody,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/");
                if (response.IsSuccessStatusCode)
                {
                    var body = (await response.Content.ReadAsStringAsync()).Trim();
                    if (body.Contains(expectedBody, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The proxy or replica is still starting.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        return false;
    }

    private sealed class ServerHostBridgeEnvironment : IAsyncDisposable
    {
        private readonly LocalSubstrateDockerFixture _docker;
        private readonly EnvironmentVariableScope _environmentScope;
        private readonly WebApplication _proxy;
        private readonly int _initialActivePort;
        private readonly int _initialStandbyPort;

        private ServerHostBridgeEnvironment(
            LocalSubstrateDockerFixture docker,
            EnvironmentVariableScope environmentScope,
            WebApplication proxy,
            WebApplicationFactory<Program> factory,
            string suffix,
            string targetId,
            string proxyBaseUrl,
            int initialActivePort,
            int initialStandbyPort)
        {
            _docker = docker;
            _environmentScope = environmentScope;
            _proxy = proxy;
            Factory = factory;
            Suffix = suffix;
            TargetId = targetId;
            ProxyBaseUrl = proxyBaseUrl;
            _initialActivePort = initialActivePort;
            _initialStandbyPort = initialStandbyPort;
            AgentName = $"local-substrate-agent-{suffix}";
        }

        public WebApplicationFactory<Program> Factory { get; }

        public string Suffix { get; }

        public string TargetId { get; }

        public string ProxyBaseUrl { get; }

        public string AgentName { get; }

        public string PreviousRevision => _docker.V1Image;

        public string CurrentRevision => _docker.V2Image;

        public string InitialActiveDestination =>
            $"http://127.0.0.1:{_initialActivePort.ToString(CultureInfo.InvariantCulture)}/";

        public string InitialStandbyDestination =>
            $"http://127.0.0.1:{_initialStandbyPort.ToString(CultureInfo.InvariantCulture)}/";

        public IReadOnlyDictionary<string, string> InitialParameterOverrides =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SelfHostedDeployParameterKeys.Image] = _docker.V2Image,
                [SelfHostedDeployParameterKeys.ActivePort] = _initialActivePort.ToString(CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.StandbyPort] = _initialStandbyPort.ToString(CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.ContainerPort] =
                    LocalSubstrateDockerFixture.ReplicaContainerPort.ToString(CultureInfo.InvariantCulture)
            };

        public IReadOnlyDictionary<string, string> RollbackParameterOverrides =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SelfHostedDeployParameterKeys.Image] = _docker.V1Image,
                [SelfHostedDeployParameterKeys.ActivePort] = _initialStandbyPort.ToString(CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.StandbyPort] = _initialActivePort.ToString(CultureInfo.InvariantCulture),
                [SelfHostedDeployParameterKeys.ContainerPort] =
                    LocalSubstrateDockerFixture.ReplicaContainerPort.ToString(CultureInfo.InvariantCulture)
            };

        public static async Task<ServerHostBridgeEnvironment> StartAsync(
            LocalSubstrateDockerFixture docker,
            string redisConnectionString)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var targetId = $"honua-ci-agent-rollback-{suffix}";
            var activePort = LocalSubstrateDockerFixture.GetFreeTcpPort();
            int standbyPort;
            do
            {
                standbyPort = LocalSubstrateDockerFixture.GetFreeTcpPort();
            }
            while (standbyPort == activePort);

            var containerPrefix = $"honua-ci-agent-rollback-{suffix}";
            await docker.Runtime.RunAsync(
                new ContainerRunRequest
                {
                    Executable = docker.RuntimeExecutable,
                    Image = docker.V1Image,
                    ContainerName = $"{containerPrefix}-{activePort.ToString(CultureInfo.InvariantCulture)}",
                    HostPort = activePort,
                    ContainerPort = LocalSubstrateDockerFixture.ReplicaContainerPort,
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [YarpRollingDeployBackend.LabelTarget] = targetId,
                        [YarpRollingDeployBackend.LabelRole] = YarpRollingDeployBackend.RoleActive,
                        [YarpRollingDeployBackend.LabelRevision] = docker.V1Image
                    }
                },
                CancellationToken.None);

            var v1Ready = await LocalSubstrateDockerFixture.WaitForBodyAsync(
                $"http://127.0.0.1:{activePort.ToString(CultureInfo.InvariantCulture)}/",
                LocalSubstrateDockerFixture.V1Marker,
                TimeSpan.FromSeconds(30));
            if (!v1Ready)
            {
                await docker.CleanupTargetAsync(targetId);
                throw new InvalidOperationException("The v1 active replica did not become ready.");
            }

            var options = new SelfHostedDeployOptions
            {
                Enabled = true,
                ContainerRuntime = docker.RuntimeExecutable,
                ContainerNamePrefix = containerPrefix,
                Host = "127.0.0.1",
                HealthPath = "/",
                ActivePort = activePort,
                StandbyPort = standbyPort,
                ContainerPort = LocalSubstrateDockerFixture.ReplicaContainerPort,
                HealthProbeSamples = 3,
                HealthProbeTimeoutSeconds = 3,
                HealthProbeExpectedStatusCode = 200,
                DrainDelaySeconds = 1
            };

            WebApplication? proxy = null;
            EnvironmentVariableScope? environmentScope = null;
            var settings = BuildHostSettings(
                redisConnectionString,
                targetId,
                containerPrefix,
                docker.RuntimeExecutable,
                activePort,
                standbyPort);
            try
            {
                var proxyBuilder = WebApplication.CreateBuilder();
                proxyBuilder.Logging.ClearProviders();
                proxyBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
                proxyBuilder.WebHost.UseUrls("http://127.0.0.1:0");

                var initialDestination = YarpInMemoryProxyStateSwapper.InitialActiveAddress(options);
                proxyBuilder.Services.AddReverseProxy().LoadFromMemory(
                    SelfHostedProxyConfig.BuildRoutes(options),
                    SelfHostedProxyConfig.BuildClusters(options, initialDestination));

                proxy = proxyBuilder.Build();
                proxy.MapReverseProxy();
                await proxy.StartAsync();

                var configProvider = (InMemoryConfigProvider)proxy.Services.GetRequiredService<IProxyConfigProvider>();
                var swapper = new YarpInMemoryProxyStateSwapper(configProvider, docker.Runtime, Options.Create(options));
                var proxyBaseUrl = proxy.Urls.First().TrimEnd('/');

                environmentScope = EnvironmentVariableScope.Apply(settings);
                var factory = new TestWebApplicationFactory()
                    .WithWebHostBuilder(builder =>
                    {
                        builder.UseEnvironment("Test");
                        builder.ConfigureAppConfiguration((_, config) =>
                        {
                            config.AddInMemoryCollection(settings);
                        });
                        builder.ConfigureServices(services =>
                        {
                            services.RemoveAll<IProxyStateSwapper>();
                            services.AddSingleton<IProxyStateSwapper>(swapper);
                        });
                    });

                return new ServerHostBridgeEnvironment(
                    docker,
                    environmentScope,
                    proxy,
                    factory,
                    suffix,
                    targetId,
                    proxyBaseUrl,
                    activePort,
                    standbyPort);
            }
            catch
            {
                environmentScope?.Dispose();
                if (proxy != null)
                {
                    await proxy.DisposeAsync();
                }

                await docker.CleanupTargetAsync(targetId);
                throw;
            }
        }

        public async Task<string> CreateInitialDeployAsync(LocalSubstrateDockerFixture docker)
        {
            var service = Factory.Services.GetRequiredService<DeployWorkflowService>();
            var record = await service.CreateAsync(
                TargetId,
                docker.V2Image,
                docker.V1Image,
                requestedBy: "local-substrate-operator",
                reason: "Deploy revision B over revision A before rollback.",
                idempotencyKey: $"initial-{Suffix}",
                correlationId: $"local-substrate-{Suffix}",
                OperationPriority.Normal,
                submitImmediately: true,
                InitialParameterOverrides,
                principal: null,
                cancellationToken: CancellationToken.None);

            record.Should().NotBeNull("the configured self-hosted rolling target must be resolvable");
            record!.Status.Should().NotBe(WorkflowOperationStatus.Failed, record.ErrorMessage);
            return record.OperationId;
        }

        public async Task<WorkflowOperationRecord> DriveDeployToSucceededAsync(
            string operationId,
            string expectedDesiredRevision,
            TimeSpan timeout)
        {
            var dispatcher = Factory.Services.GetRequiredService<IOperationReconcileDispatcher>();
            var service = Factory.Services.GetRequiredService<DeployWorkflowService>();
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            WorkflowOperationRecord? last = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                await dispatcher.ReconcileOnceAsync(new OperationRef(OperationKind.DeployWorkflow, operationId));
                last = await service.GetAsync(operationId);
                if (last is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300));
                    continue;
                }

                if (last.Status == WorkflowOperationStatus.Succeeded)
                {
                    last.Deploy!.DesiredRevision.Should().Be(expectedDesiredRevision);
                    return last;
                }

                last.Status.Should().NotBe(WorkflowOperationStatus.Failed, last.ErrorMessage);
                last.Status.Should().NotBe(WorkflowOperationStatus.ManualInterventionRequired, last.ErrorMessage);
                last.Status.Should().NotBe(WorkflowOperationStatus.RolledBack, last.CurrentPhase);

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            throw new TimeoutException(
                $"Deploy operation '{operationId}' did not succeed within {timeout}. " +
                $"Last status: {last?.Status.ToString() ?? "(missing)"} ({last?.CurrentPhase}).");
        }

        public OperateEvent[] ReleaseEvents()
            => Factory.Services.GetRequiredService<ReleaseTimelineBuffer>()
                .Snapshot()
                .Where(e => e.Kind == OperateEventKind.Release)
                .ToArray();

        public string? ActiveProxyDestination()
            => Factory.Services.GetRequiredService<IProxyStateSwapper>().ActiveDestinationAddress;

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            _environmentScope.Dispose();
            try
            {
                await _proxy.StopAsync();
            }
            catch
            {
                // Best-effort proxy shutdown.
            }

            await _proxy.DisposeAsync();
            await _docker.CleanupTargetAsync(TargetId);
        }

        private static Dictionary<string, string?> BuildHostSettings(
            string redisConnectionString,
            string targetId,
            string containerPrefix,
            string containerRuntime,
            int activePort,
            int standbyPort)
        {
            var active = activePort.ToString(CultureInfo.InvariantCulture);
            var standby = standbyPort.ToString(CultureInfo.InvariantCulture);
            var containerPort = LocalSubstrateDockerFixture.ReplicaContainerPort.ToString(CultureInfo.InvariantCulture);

            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HONUA_DEV_AUTH"] = "false",
                ["HONUA_ADMIN_PASSWORD"] = AdminPassword,
                ["ConnectionStrings:redis"] = redisConnectionString,
                ["Cache:KeyPrefix"] = $"honua:{targetId}:",
                ["Licensing:DevGrantEdition"] = "Pro",
                ["Guardrails:Overrides:Deploy"] = "RequiresApproval",
                ["ControlPlane:SelfHosted:Enabled"] = "false",
                ["ControlPlane:SelfHosted:ContainerRuntime"] = containerRuntime,
                ["ControlPlane:SelfHosted:ContainerNamePrefix"] = containerPrefix,
                ["ControlPlane:SelfHosted:Host"] = "127.0.0.1",
                ["ControlPlane:SelfHosted:HealthPath"] = "/",
                ["ControlPlane:SelfHosted:ActivePort"] = active,
                ["ControlPlane:SelfHosted:StandbyPort"] = standby,
                ["ControlPlane:SelfHosted:ContainerPort"] = containerPort,
                ["ControlPlane:SelfHosted:HealthProbeSamples"] = "3",
                ["ControlPlane:SelfHosted:HealthProbeTimeoutSeconds"] = "3",
                ["ControlPlane:SelfHosted:HealthProbeExpectedStatusCode"] = "200",
                ["ControlPlane:SelfHosted:DrainDelaySeconds"] = "1",
                ["ControlPlane:DeployTargets:0:TargetId"] = targetId,
                ["ControlPlane:DeployTargets:0:TargetKind"] = DeployTargetKind.SelfHostedRolling.ToString(),
                ["ControlPlane:DeployTargets:0:Backend"] = YarpRollingDeployBackend.AdapterBackendName,
                ["ControlPlane:DeployTargets:0:Environment"] = "local-substrate",
                ["ControlPlane:DeployTargets:0:TargetName"] = "honua-serving",
                ["ControlPlane:DeployTargets:0:RequiresApproval"] = "false",
                ["ControlPlane:DeployTargets:0:ParameterEntries:0:Key"] = SelfHostedDeployParameterKeys.ActivePort,
                ["ControlPlane:DeployTargets:0:ParameterEntries:0:Value"] = active,
                ["ControlPlane:DeployTargets:0:ParameterEntries:1:Key"] = SelfHostedDeployParameterKeys.StandbyPort,
                ["ControlPlane:DeployTargets:0:ParameterEntries:1:Value"] = standby,
                ["ControlPlane:DeployTargets:0:ParameterEntries:2:Key"] = SelfHostedDeployParameterKeys.ContainerPort,
                ["ControlPlane:DeployTargets:0:ParameterEntries:2:Value"] = containerPort
            };
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        private EnvironmentVariableScope()
        {
        }

        public static EnvironmentVariableScope Apply(IReadOnlyDictionary<string, string?> settings)
        {
            var scope = new EnvironmentVariableScope();
            foreach (var (key, value) in settings)
            {
                scope.Set(key.Replace(":", "__", StringComparison.Ordinal), value);
            }

            return scope;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (key, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            _disposed = true;
        }

        private void Set(string key, string? value)
        {
            _previousValues.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class RequestLoop : IDisposable
    {
        private readonly HttpClient _client;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _total;
        private int _failures;
        private volatile bool _sawV1;
        private volatile bool _sawV2;
        private volatile string? _lastBody;

        private RequestLoop(HttpClient client)
        {
            _client = client;
            _loop = Task.Run(RunAsync);
        }

        public static RequestLoop Start(HttpClient client) => new(client);

        public async Task<RequestLoopResult> StopAsync()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            return new RequestLoopResult(_total, _failures, _sawV1, _sawV2, _lastBody);
        }

        public void Dispose()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Interlocked.Increment(ref _total);
                try
                {
                    using var response = await _client.GetAsync("/", _cts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref _failures);
                    }
                    else
                    {
                        var body = (await response.Content.ReadAsStringAsync(_cts.Token)).Trim();
                        _lastBody = body;
                        if (body.Contains(LocalSubstrateDockerFixture.V1Marker, StringComparison.Ordinal))
                        {
                            _sawV1 = true;
                        }

                        if (body.Contains(LocalSubstrateDockerFixture.V2Marker, StringComparison.Ordinal))
                        {
                            _sawV2 = true;
                        }
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    Interlocked.Increment(ref _failures);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private sealed record RequestLoopResult(int Total, int Failures, bool SawV1, bool SawV2, string? LastBody);
}
