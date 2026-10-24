// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    private readonly string _tempDirectory = Path.Join(Path.GetTempPath(), "honua-pod-identity-" + Guid.NewGuid().ToString("N"));

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
        var tokenFile = Path.Join(_tempDirectory, "eks-pod-identity-token");
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
        var tokenFile = Path.Join(_tempDirectory, "token");
        await File.WriteAllTextAsync(tokenFile, "file-token\n");
        using var env = CredentialEnvironment(fullUri: agent.Uri, token: "env-token-wins", tokenFile: tokenFile);
        using var harness = new ResolverHarness();

        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:both")).Should().Be("resolved");

        agent.AuthorizationHeaders.Should().Equal("env-token-wins");
    }

    [SecurityTest]
    [Fact]
    public async Task MissingTokenFile_LogsTheVariableName_AndRefusesNodeRoleFallback()
    {
        using var agent = new FakeCredentialsAgent();
        var missingFile = Path.Join(_tempDirectory, "does-not-exist");
        using var env = CredentialEnvironment(fullUri: agent.Uri, token: null, tokenFile: missingFile);
        using var harness = new ResolverHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:missing-file"));

        agent.AuthorizationHeaders.Should().BeEmpty("an unauthenticated request to the Pod Identity agent is pointless");
        harness.SigningAccessKeys.Should().BeEmpty();
        harness.ImdsRequests.Should().BeEmpty();
        var warning = harness.Logger.Messages.Should().ContainSingle(m => m.Contains("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", StringComparison.Ordinal)).Subject;
        warning.Should().Contain("file not found");
    }

    [SecurityTest]
    [Fact]
    public async Task MalformedTokenFile_IsRejected_WithoutLoggingItsContents()
    {
        using var agent = new FakeCredentialsAgent();
        var tokenFile = Path.Join(_tempDirectory, "multi-line-token");
        await File.WriteAllTextAsync(tokenFile, "super-secret-line-one\nsuper-secret-line-two\n");
        using var env = CredentialEnvironment(fullUri: agent.Uri, token: null, tokenFile: tokenFile);
        using var harness = new ResolverHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:malformed"));

        agent.AuthorizationHeaders.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
        harness.ImdsRequests.Should().BeEmpty();
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

    [SecurityTest]
    [Theory]
    [InlineData("empty")]
    [InlineData("directory")]
    [InlineData("blank-path")]
    public async Task UnusableTokenFile_RefusesWithoutNodeOrSecretRequests(string kind)
    {
        using var agent = new FakeCredentialsAgent();
        var tokenFile = kind == "blank-path" ? "  " : Path.Join(_tempDirectory, "unusable-token");
        if (kind == "directory") Directory.CreateDirectory(tokenFile);
        else if (kind == "empty") await File.WriteAllTextAsync(tokenFile, "  ");
        using var env = CredentialEnvironment(agent.Uri, null, tokenFile);
        using var harness = new ResolverHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:unusable"));

        agent.AuthorizationHeaders.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
        harness.ImdsRequests.Should().BeEmpty();
    }

    [SecurityTest]
    [Theory]
    [InlineData("  ")]
    [InlineData("synthetic-private-first\nsynthetic-private-second")]
    public async Task UnusableEnvironmentToken_RefusesWithoutLeakingOrBorrowingCredentials(string token)
    {
        using var agent = new FakeCredentialsAgent();
        using var env = CredentialEnvironment(agent.Uri, token, null);
        using var harness = new ResolverHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:bad-token"));

        agent.AuthorizationHeaders.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
        harness.ImdsRequests.Should().BeEmpty();
        harness.Logger.Messages.Should().NotContain(message => message.Contains("synthetic-private", StringComparison.Ordinal));
    }

    [SecurityTest]
    [Fact]
    public async Task CancelledContainerRequest_PreservesCancellationWithoutNodeFallback()
    {
        using var agent = new FakeCredentialsAgent { PauseResponses = true };
        using var env = CredentialEnvironment(agent.Uri, "synthetic-token", null);
        using var harness = new ResolverHarness();
        using var cancellation = new CancellationTokenSource();
        var request = harness.Resolver.ResolveSecretAsync("aws:secretsmanager:cancelled", cancellation.Token);
        await agent.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        harness.ImdsRequests.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
    }

    [SecurityTest]
    [Theory]
    [InlineData("denied")]
    [InlineData("malformed")]
    [InlineData("missing-fields")]
    [InlineData("expired")]
    public async Task FailedContainerResponse_RefusesWithoutBorrowingNodeCredentials(string failure)
    {
        using var agent = new FakeCredentialsAgent
        {
            ResponseStatus = failure == "denied" ? "403 Forbidden" : "200 OK",
            ResponseBody = failure switch
            {
                "malformed" => "synthetic-private-response-must-not-leak",
                "missing-fields" => "{\"AccessKeyId\":\"synthetic-private-access-key\"}",
                "expired" => CredentialsJson(PodIdentityAccessKey, DateTimeOffset.UtcNow.AddMinutes(-2)),
                _ => "{\"Message\":\"synthetic-private-response-must-not-leak\"}"
            }
        };
        using var env = CredentialEnvironment(agent.Uri, "synthetic-token-must-not-leak", null);
        using var harness = new ResolverHarness();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:refused"));

        harness.ImdsRequests.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
        error.Message.Should().NotContain("synthetic-private").And.NotContain("synthetic-token");
        harness.Logger.Messages.Should().NotContain(message => message.Contains("synthetic-", StringComparison.Ordinal));
    }

    [SecurityTest]
    [Theory]
    [InlineData("AWS_ACCESS_KEY_ID")]
    [InlineData("AWS_ACCESS_KEY")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("AWS_SESSION_TOKEN")]
    public async Task PartialEnvironmentCredentials_RefuseOtherProviders(string variable)
    {
        using var env = new EnvironmentVariableScope(BaseEnvironment((variable, "synthetic-partial-credential")));
        using var harness = new ResolverHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:partial"));

        harness.ImdsRequests.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
    }

    [SecurityTest]
    [Theory]
    [InlineData("AWS_WEB_IDENTITY_TOKEN_FILE")]
    [InlineData("AWS_ROLE_ARN")]
    public async Task UnsupportedWebIdentity_RefusesBeforeMetadataOrSecretRequests(string variable)
    {
        using var env = new EnvironmentVariableScope(BaseEnvironment((variable, "synthetic-web-identity-setting")));
        using var harness = new ResolverHarness();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:web-identity"));

        error.Message.Should().Contain("web-identity");
        harness.ImdsRequests.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
        harness.Logger.Messages.Should().NotContain(message => message.Contains("synthetic-web-identity", StringComparison.Ordinal));
    }

    [SecurityTest]
    [Fact]
    public async Task RejectedContainerUri_DoesNotFallBackOrLogItsPrivateQuery()
    {
        using var env = CredentialEnvironment(new Uri("https://untrusted.invalid/credentials?token=synthetic-private-token"), null, null);
        using var harness = new ResolverHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:bad-uri"));

        harness.ImdsRequests.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
        harness.Logger.Messages.Should().NotContain(message => message.Contains("synthetic-private-token", StringComparison.Ordinal));
    }

    [SecurityTest]
    [Fact]
    public async Task TokenFileWithoutContainerEndpoint_RefusesNodeFallback()
    {
        using var env = new EnvironmentVariableScope(BaseEnvironment(("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", "/synthetic-token-path")));
        using var harness = new ResolverHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:missing-endpoint"));

        harness.ImdsRequests.Should().BeEmpty();
        harness.SigningAccessKeys.Should().BeEmpty();
    }

    [SecurityTest]
    [Fact]
    public async Task ValidEnvironmentCredentials_RetainPrecedenceOverWorkloadConfiguration()
    {
        using var env = new EnvironmentVariableScope(BaseEnvironment(
            ("AWS_ACCESS_KEY_ID", "AKIA_EXPLICIT_ENVIRONMENT"), ("AWS_SECRET_ACCESS_KEY", "synthetic-secret"),
            ("AWS_WEB_IDENTITY_TOKEN_FILE", "/synthetic-token-path"), ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "/v2/credentials/task-guid")));
        using var harness = new ResolverHarness();

        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:explicit-env")).Should().Be("resolved");

        harness.SigningAccessKeys.Should().Equal("AKIA_EXPLICIT_ENVIRONMENT");
        harness.ImdsRequests.Should().BeEmpty();
        harness.EcsRelativeRequests.Should().BeEmpty();
    }

    [SecurityTest]
    [Fact]
    public async Task NoExplicitProvider_RetainsOrdinaryEc2RoleResolution()
    {
        using var env = new EnvironmentVariableScope(BaseEnvironment());
        using var harness = new ResolverHarness();

        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:ec2")).Should().Be("resolved");

        harness.SigningAccessKeys.Should().Equal(NodeRoleAccessKey);
        harness.ImdsRequests.Should().HaveCount(3);
    }

    [SecurityTest]
    [Fact]
    public async Task TokenRemovedDuringRefresh_RefusesInsteadOfChangingProvider()
    {
        using var agent = new FakeCredentialsAgent();
        var tokenFile = Path.Join(_tempDirectory, "rotating-token");
        await File.WriteAllTextAsync(tokenFile, "synthetic-initial-token");
        using var env = CredentialEnvironment(agent.Uri, null, tokenFile);
        using var harness = new ResolverHarness();
        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:initial")).Should().Be("resolved");
        File.Delete(tokenFile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:after-rotation"));

        harness.SigningAccessKeys.Should().Equal(PodIdentityAccessKey);
        harness.ImdsRequests.Should().BeEmpty();
    }

    [SecurityTest]
    [Fact]
    public async Task ProviderConfigurationRemovedDuringRefresh_RefusesNodeFallback()
    {
        using var agent = new FakeCredentialsAgent();
        using var env = CredentialEnvironment(agent.Uri, "synthetic-container-token", null);
        using var harness = new ResolverHarness();
        (await harness.Resolver.ResolveSecretAsync("aws:secretsmanager:before-removal")).Should().Be("resolved");
        Environment.SetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI", null);
        Environment.SetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveSecretAsync("aws:secretsmanager:after-removal"));

        harness.SigningAccessKeys.Should().Equal(PodIdentityAccessKey);
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
            ("AWS_ROLE_ARN", null),
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", null),
            ("AWS_REGION", "us-east-1"),
            ("AWS_DEFAULT_REGION", null),
            .. overrides,
        ];

    private static string CredentialsJson(string accessKey, DateTimeOffset? expiration = null)
        => "{\"AccessKeyId\":\"" + accessKey + "\",\"SecretAccessKey\":\"secret\",\"Token\":\"session\",\"Expiration\":\""
           // Inside the resolver's one-minute refresh window, so every resolution refetches.
           + (expiration ?? DateTimeOffset.UtcNow.AddSeconds(30)).ToString("o", CultureInfo.InvariantCulture) + "\"}";

    /// <summary>
    /// Wires the resolver: the Secrets Manager client is stubbed (and records which access key signed
    /// each call); the metadata client reaches the loopback fake agent over real HTTP and stubs the
    /// fixed link-local ECS and IMDS addresses so no test ever leaves the machine.
    /// </summary>
    private sealed class ResolverHarness : IDisposable
    {
        private readonly HttpClient _secretsClient;
        private readonly HttpClient _metadataClient;
        private readonly SocketsHttpHandler _loopbackHandler = new();

        public ResolverHarness()
        {
            _secretsClient = new HttpClient(new StubHandler(request =>
            {
                var authorization = request.Headers.GetValues("Authorization").Single();
                var credential = authorization.Split("Credential=", 2)[1];
                SigningAccessKeys.Enqueue(credential[..credential.IndexOf('/', StringComparison.Ordinal)]);
                return Json("{\"SecretString\":\"resolved\"}");
            }));
            _metadataClient = new HttpClient(new LinkLocalStubHandler(this, _loopbackHandler));
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
            _loopbackHandler.Dispose();
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Text(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body) };

        private sealed class LinkLocalStubHandler(ResolverHarness owner, HttpMessageHandler loopback) : DelegatingHandler(loopback)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.Host == "169.254.169.254")
                {
                    owner.ImdsRequests.Enqueue(uri);
                    if (request.Method == HttpMethod.Put)
                    {
                        return Task.FromResult(Text("imds-token"));
                    }

                    return Task.FromResult(uri.AbsolutePath.EndsWith("/security-credentials/", StringComparison.Ordinal)
                        ? Text("honua-eks-node")
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

        public string? ResponseStatus { get; init; }

        public string? ResponseBody { get; init; }

        public bool PauseResponses { get; init; }

        public TaskCompletionSource RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

            var authorization = received.ToString()
                .Split("\r\n")
                .Where(static line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                .Select(static line => line["Authorization:".Length..].Trim())
                .LastOrDefault();

            RequestReceived.TrySetResult();
            if (PauseResponses)
            {
                await Task.Delay(Timeout.Infinite, _stopping.Token).ConfigureAwait(false);
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

            status = ResponseStatus ?? status;
            body = ResponseBody ?? body;
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
                _originalValues.TryAdd(key, Environment.GetEnvironmentVariable(key));
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
