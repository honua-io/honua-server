// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Tests.Features.Security;

/// <summary>
/// Container-credentials provider coverage for <see cref="AwsSecretsManagerResolver"/>. EKS Pod
/// Identity exposes credentials at <c>AWS_CONTAINER_CREDENTIALS_FULL_URI</c> and only accepts the
/// projected token from <c>AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE</c> as the <c>Authorization</c>
/// header; without it the resolver used to fall back to the node role (honua-release run 38145777594).
/// A real loopback HTTP listener stands in for the Pod Identity / ECS agent.
/// </summary>
[Collection("Security")]
public sealed class AwsSecretsManagerResolverContainerCredentialsTests : IDisposable
{
    private const string PodIdentityAccessKey = "AKIA_POD_IDENTITY";
    private const string EcsRelativeAccessKey = "AKIA_ECS_RELATIVE";
    private const string NodeRoleAccessKey = "AKIA_NODE_ROLE";

    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "honua-pod-identity-" + Guid.NewGuid().ToString("N"));

    public AwsSecretsManagerResolverContainerCredentialsTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [SecurityTest]
    [Fact]
    public async Task TokenEnvironmentVariableOnly_SendsTokenAsAuthorization()
    {
        using var agent = new FakeCredentialsAgent();
        using var env = CredentialEnvironment(fullUri: agent.Uri, token: "env-token-123", tokenFile: null);
        using var harness = new ResolverHarness();

        var value = await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:env-only");

        value.Should().Be("resolved");
        agent.AuthorizationHeaders.Should().Equal("env-token-123");
        harness.SigningAccessKeys.Should().Equal(PodIdentityAccessKey);
        harness.ImdsRequests.Should().BeEmpty();
    }

    [SecurityTest]
    [Fact]
    public async Task TokenFileOnly_SendsTrimmedFileContents_AndRereadsTheFileAfterRotation()
    {
        using var agent = new FakeCredentialsAgent();
        var tokenFile = Path.Combine(_tempDirectory, "eks-pod-identity-token");
        await File.WriteAllTextAsync(tokenFile, "pod-identity-token-v1\n");
        using var env = CredentialEnvironment(fullUri: agent.Uri, token: null, tokenFile: tokenFile);
        using var harness = new ResolverHarness();

        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:first")).Should().Be("resolved");

        // The kubelet rotates the projected token underneath the running pod; the agent's
        // credentials expire inside the refresh window, so the next resolution must re-read it.
        await File.WriteAllTextAsync(tokenFile, "pod-identity-token-v2\r\n");
        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:second")).Should().Be("resolved");

        agent.AuthorizationHeaders.Should().Equal("pod-identity-token-v1", "pod-identity-token-v2");
        harness.SigningAccessKeys.Should().Equal(PodIdentityAccessKey, PodIdentityAccessKey);
        harness.ImdsRequests.Should().BeEmpty("the node role must never be used when Pod Identity is configured");
    }

    [SecurityTest]
    [Fact]
    public async Task TokenEnvironmentVariableAndFile_EnvironmentVariableWins()
    {
        using var agent = new FakeCredentialsAgent();
        var tokenFile = Path.Combine(_tempDirectory, "token");
        await File.WriteAllTextAsync(tokenFile, "file-token\n");
        using var env = CredentialEnvironment(fullUri: agent.Uri, token: "env-token-wins", tokenFile: tokenFile);
        using var harness = new ResolverHarness();

        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:both")).Should().Be("resolved");

        agent.AuthorizationHeaders.Should().Equal("env-token-wins");
    }

    [SecurityTest]
    [Fact]
    public async Task MissingTokenFile_LogsTheVariableName_AndFallsBackToTheNextCredentialSource()
    {
        using var agent = new FakeCredentialsAgent();
        var missingFile = Path.Combine(_tempDirectory, "does-not-exist");
        using var env = CredentialEnvironment(fullUri: agent.Uri, token: null, tokenFile: missingFile);
        using var harness = new ResolverHarness();

        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:missing-file")).Should().Be("resolved");

        agent.AuthorizationHeaders.Should().BeEmpty("an unauthenticated request to the Pod Identity agent is pointless");
        harness.SigningAccessKeys.Should().Equal(NodeRoleAccessKey);
        var warning = harness.Logger.Messages.Should().ContainSingle(m => m.Contains("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", StringComparison.Ordinal)).Subject;
        warning.Should().Contain("file not found");
    }

    [SecurityTest]
    [Fact]
    public async Task MalformedTokenFile_IsRejected_WithoutLoggingItsContents()
    {
        using var agent = new FakeCredentialsAgent();
        var tokenFile = Path.Combine(_tempDirectory, "multi-line-token");
        await File.WriteAllTextAsync(tokenFile, "super-secret-line-one\nsuper-secret-line-two\n");
        using var env = CredentialEnvironment(fullUri: agent.Uri, token: null, tokenFile: tokenFile);
        using var harness = new ResolverHarness();

        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:malformed")).Should().Be("resolved");

        agent.AuthorizationHeaders.Should().BeEmpty();
        harness.SigningAccessKeys.Should().Equal(NodeRoleAccessKey);
        harness.Logger.Messages.Should().Contain(m => m.Contains("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", StringComparison.Ordinal));
        harness.Logger.Messages.Should().NotContain(m => m.Contains("super-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelativeUriForm_StillTargetsTheEcsAgent()
    {
        using var env = new EnvironmentVariableScope(BaseEnvironment(
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "/v2/credentials/task-guid"),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", null)));
        using var harness = new ResolverHarness();

        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:relative")).Should().Be("resolved");

        harness.EcsRelativeRequests.Should().Equal(new Uri("http://169.254.170.2/v2/credentials/task-guid"));
        harness.SigningAccessKeys.Should().Equal(EcsRelativeAccessKey);
        harness.ImdsRequests.Should().BeEmpty();
    }

    private static EnvironmentVariableScope CredentialEnvironment(Uri fullUri, string? token, string? tokenFile)
        => new(BaseEnvironment(
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", fullUri.ToString()),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", token),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", tokenFile)));

    private static (string Key, string? Value)[] BaseEnvironment(params (string Key, string? Value)[] overrides)
        =>
        [
            ("AWS_ACCESS_KEY_ID", null),
            ("AWS_ACCESS_KEY", null),
            ("AWS_SECRET_ACCESS_KEY", null),
            ("AWS_SESSION_TOKEN", null),
            ("AWS_WEB_IDENTITY_TOKEN_FILE", null),
            ("AWS_REGION", "us-east-1"),
            ("AWS_DEFAULT_REGION", null),
            .. overrides,
        ];

    private static string CredentialsJson(string accessKey)
        => "{\"AccessKeyId\":\"" + accessKey + "\",\"SecretAccessKey\":\"secret\",\"Token\":\"session\",\"Expiration\":\""
           // Inside the resolver's one-minute refresh window, so every resolution refetches.
           + DateTimeOffset.UtcNow.AddSeconds(30).ToString("o", CultureInfo.InvariantCulture) + "\"}";

    /// <summary>
    /// Wires the resolver: the Secrets Manager client is stubbed (and records which access key signed
    /// each call); the metadata client reaches the loopback fake agent over real HTTP and stubs the
    /// fixed link-local ECS and IMDS addresses so no test ever leaves the machine.
    /// </summary>
    private sealed class ResolverHarness : IDisposable
    {
        private readonly HttpClient _secretsClient;
        private readonly HttpClient _metadataClient;

        public ResolverHarness()
        {
            _secretsClient = new HttpClient(new StubHandler(request =>
            {
                var authorization = request.Headers.GetValues("Authorization").Single();
                var credential = authorization.Split("Credential=", 2)[1];
                SigningAccessKeys.Enqueue(credential[..credential.IndexOf('/', StringComparison.Ordinal)]);
                return Json("{\"SecretString\":\"resolved\"}");
            }));
            _metadataClient = new HttpClient(new LinkLocalStubHandler(this));
            Resolver = new AwsSecretsManagerResolver(_secretsClient, _metadataClient, Logger);
        }

        public CapturingLogger Logger { get; } = new();

        public AwsSecretsManagerResolver Resolver { get; }

        public ConcurrentQueue<string> SigningAccessKeys { get; } = new();

        public ConcurrentQueue<Uri> ImdsRequests { get; } = new();

        public ConcurrentQueue<Uri> EcsRelativeRequests { get; } = new();

        public void Dispose()
        {
            Resolver.Dispose();
            _secretsClient.Dispose();
            _metadataClient.Dispose();
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class LinkLocalStubHandler(ResolverHarness owner) : DelegatingHandler(new SocketsHttpHandler())
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.Host == "169.254.169.254")
                {
                    owner.ImdsRequests.Enqueue(uri);
                    if (request.Method == HttpMethod.Put)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("imds-token") });
                    }

                    return Task.FromResult(uri.AbsolutePath.EndsWith("/security-credentials/", StringComparison.Ordinal)
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("honua-eks-node") }
                        : Json(CredentialsJson(NodeRoleAccessKey)));
                }

                if (uri.Host == "169.254.170.2")
                {
                    owner.EcsRelativeRequests.Enqueue(uri);
                    return Task.FromResult(Json(CredentialsJson(EcsRelativeAccessKey)));
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    /// <summary>
    /// Minimal HTTP/1.1 credentials agent on a loopback socket, behaving like the EKS Pod Identity
    /// agent: it records the <c>Authorization</c> header and rejects requests that carry none.
    /// </summary>
    private sealed class FakeCredentialsAgent : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _acceptLoop;

        public FakeCredentialsAgent()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Uri = new Uri($"http://127.0.0.1:{port}/v1/credentials");
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public Uri Uri { get; }

        public ConcurrentQueue<string> AuthorizationHeaders { get; } = new();

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Listener shutdown surfaces as a socket/cancellation fault.
            }

            _stopping.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                using (client)
                {
                    await HandleAsync(client.GetStream()).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var received = new StringBuilder();
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            string? authorization = null;
            foreach (var line in received.ToString().Split("\r\n"))
            {
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    authorization = line["Authorization:".Length..].Trim();
                }
            }

            string status;
            string body;
            if (authorization is null)
            {
                status = "401 Unauthorized";
                body = "{\"Message\":\"missing authorization\"}";
            }
            else
            {
                AuthorizationHeaders.Enqueue(authorization);
                status = "200 OK";
                body = CredentialsJson(PodIdentityAccessKey);
            }

            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stopping.Token).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, _stopping.Token).ConfigureAwait(false);
            await stream.FlushAsync(_stopping.Token).ConfigureAwait(false);
        }
    }

    private sealed class CapturingLogger : ILogger<AwsSecretsManagerResolver>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

        public EnvironmentVariableScope(params (string Key, string? Value)[] values)
        {
            foreach (var (key, value) in values)
            {
                _originalValues[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
