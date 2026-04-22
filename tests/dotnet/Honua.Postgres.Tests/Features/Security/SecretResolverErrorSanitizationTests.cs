// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Security;

[Collection("Security")]
public sealed class SecretResolverErrorSanitizationTests
{
    [SecurityTest]
    [Fact]
    public async Task AwsSecretsManagerResolver_Failure_DoesNotExposeResponseBody()
    {
        const string sensitiveBody = "{\"error\":\"password=leaked-value\"}";
        using var environmentScope = new EnvironmentVariableScope(
            ("AWS_ACCESS_KEY_ID", "AKIA_TEST_ACCESS_KEY"),
            ("AWS_SECRET_ACCESS_KEY", "test-secret-key"),
            ("AWS_REGION", "us-east-1"),
            ("AWS_DEFAULT_REGION", null),
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", null));

        var httpClientFactory = new StubHttpClientFactory(new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            ["AwsSecretsManager"] = CreateHttpClient(new DelegateHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(sensitiveBody, Encoding.UTF8, "application/json")
                })),
            ["AwsMetadata"] = CreateHttpClient(new DelegateHttpMessageHandler(_ =>
                throw new InvalidOperationException("Metadata endpoint should not be used when environment credentials are set.")))
        });

        using var resolver = new AwsSecretsManagerResolver(httpClientFactory, NullLogger<AwsSecretsManagerResolver>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveConnectionStringAsync("aws:secretsmanager:my-secret"));

        exception.Message.Should().Contain("status code 403");
        exception.Message.Should().NotContain(sensitiveBody);
        exception.Message.Should().NotContain("leaked-value");
    }

    [SecurityTest]
    [Fact]
    public async Task AwsSecretsManagerResolver_MalformedOptionEncoding_ThrowsArgumentExceptionWithoutNetworkCall()
    {
        var secretsClientCalled = false;
        using var environmentScope = new EnvironmentVariableScope(
            ("AWS_ACCESS_KEY_ID", "AKIA_TEST_ACCESS_KEY"),
            ("AWS_SECRET_ACCESS_KEY", "test-secret-key"),
            ("AWS_REGION", "us-east-1"),
            ("AWS_DEFAULT_REGION", null),
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", null));

        var httpClientFactory = new StubHttpClientFactory(new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            ["AwsSecretsManager"] = CreateHttpClient(new DelegateHttpMessageHandler(_ =>
            {
                secretsClientCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"SecretString\":\"unused\"}", Encoding.UTF8, "application/json")
                };
            })),
            ["AwsMetadata"] = CreateHttpClient(new DelegateHttpMessageHandler(_ =>
                throw new InvalidOperationException("Metadata endpoint should not be used when environment credentials are set.")))
        });

        using var resolver = new AwsSecretsManagerResolver(httpClientFactory, NullLogger<AwsSecretsManagerResolver>.Instance);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveConnectionStringAsync("aws:secretsmanager:my-secret?versionStage=%zz"));

        exception.Message.Should().Contain("malformed option encoding");
        secretsClientCalled.Should().BeFalse();
    }

    [SecurityTest]
    [Fact]
    public async Task AwsSecretsManagerResolver_CanResolveSecretAsync_MalformedOptionEncoding_ReturnsFalse()
    {
        using var environmentScope = new EnvironmentVariableScope(
            ("AWS_REGION", "us-east-1"),
            ("AWS_DEFAULT_REGION", null));

        var httpClientFactory = new StubHttpClientFactory(new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            ["AwsSecretsManager"] = CreateHttpClient(new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            ["AwsMetadata"] = CreateHttpClient(new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        });

        using var resolver = new AwsSecretsManagerResolver(httpClientFactory, NullLogger<AwsSecretsManagerResolver>.Instance);

        var canResolve = await resolver.CanResolveSecretAsync("aws:secretsmanager:my-secret?versionId=%zz");

        canResolve.Should().BeFalse();
    }

    [SecurityTest]
    [Fact]
    public async Task AwsSecretsManagerResolver_InvalidSecretBinaryBase64_ThrowsSanitizedInvalidOperation()
    {
        const string malformedBinaryPayload = "{\"SecretBinary\":\"%%%\"}";
        using var environmentScope = new EnvironmentVariableScope(
            ("AWS_ACCESS_KEY_ID", "AKIA_TEST_ACCESS_KEY"),
            ("AWS_SECRET_ACCESS_KEY", "test-secret-key"),
            ("AWS_REGION", "us-east-1"),
            ("AWS_DEFAULT_REGION", null),
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", null));

        var httpClientFactory = new StubHttpClientFactory(new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            ["AwsSecretsManager"] = CreateHttpClient(new DelegateHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(malformedBinaryPayload, Encoding.UTF8, "application/json")
                })),
            ["AwsMetadata"] = CreateHttpClient(new DelegateHttpMessageHandler(_ =>
                throw new InvalidOperationException("Metadata endpoint should not be used when environment credentials are set.")))
        });

        using var resolver = new AwsSecretsManagerResolver(httpClientFactory, NullLogger<AwsSecretsManagerResolver>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveConnectionStringAsync("aws:secretsmanager:my-secret"));

        exception.Message.Should().Contain("not valid base64");
        exception.Message.Should().NotContain(malformedBinaryPayload);
    }

    [SecurityTest]
    [Fact]
    public async Task AzureKeyVaultResolver_SecretRequestFailure_DoesNotExposeResponseBody()
    {
        const string sensitiveBody = "{\"error\":\"connectionString=leaked-value\"}";
        using var environmentScope = new EnvironmentVariableScope(
            ("AZURE_TENANT_ID", "test-tenant"),
            ("AZURE_CLIENT_ID", "test-client-id"),
            ("AZURE_CLIENT_SECRET", "test-client-secret"));

        var httpClientFactory = new StubHttpClientFactory(new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            ["AzureKeyVault"] = CreateHttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
                    };
                }

                if (request.RequestUri?.Host.Equals("myvault.vault.azure.net", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent(sensitiveBody, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            ["AzureManagedIdentityMetadata"] = CreateHttpClient(new DelegateHttpMessageHandler(_ =>
                throw new InvalidOperationException("Managed identity endpoint should not be used when client credentials are set.")))
        });

        using var resolver = new AzureKeyVaultResolver(httpClientFactory, NullLogger<AzureKeyVaultResolver>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveConnectionStringAsync("azure:keyvault:myvault:my-secret"));

        exception.Message.Should().Contain("status code 401");
        exception.Message.Should().NotContain(sensitiveBody);
        exception.Message.Should().NotContain("leaked-value");
    }

    [SecurityTest]
    [Fact]
    public async Task AzureKeyVaultResolver_TokenFailure_DoesNotExposeResponseBody()
    {
        const string sensitiveBody = "{\"error_description\":\"client_secret=leaked-value\"}";
        using var environmentScope = new EnvironmentVariableScope(
            ("AZURE_TENANT_ID", "test-tenant"),
            ("AZURE_CLIENT_ID", "test-client-id"),
            ("AZURE_CLIENT_SECRET", "test-client-secret"));

        var httpClientFactory = new StubHttpClientFactory(new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            ["AzureKeyVault"] = CreateHttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(sensitiveBody, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })),
            ["AzureManagedIdentityMetadata"] = CreateHttpClient(new DelegateHttpMessageHandler(_ =>
                throw new InvalidOperationException("Managed identity endpoint should not be used when client credentials are set.")))
        });

        using var resolver = new AzureKeyVaultResolver(httpClientFactory, NullLogger<AzureKeyVaultResolver>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveConnectionStringAsync("azure:keyvault:myvault:my-secret"));

        exception.Message.Should().Contain("status code 400");
        exception.Message.Should().NotContain(sensitiveBody);
        exception.Message.Should().NotContain("leaked-value");
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
        => new(handler, disposeHandler: true);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyDictionary<string, HttpClient> _clients;

        public StubHttpClientFactory(IReadOnlyDictionary<string, HttpClient> clients)
        {
            _clients = clients;
        }

        public HttpClient CreateClient(string name)
        {
            if (_clients.TryGetValue(name, out var client))
            {
                return client;
            }

            throw new InvalidOperationException($"No HttpClient configured for '{name}'.");
        }
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
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
