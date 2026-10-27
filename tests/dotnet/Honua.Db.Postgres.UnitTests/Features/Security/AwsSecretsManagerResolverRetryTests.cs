// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Security;

/// <summary>
/// AWS Secrets Manager failures name the error type and retry only throttling,
/// request limits, internal service errors, and HTTP 5xx (#6646).
/// </summary>
[Collection("Security")]
[Trait("Tier", "Fast")]
public sealed class AwsSecretsManagerResolverRetryTests
{
    private const string SecretReference = "aws:secretsmanager:demo/key-ring";
    private const string ResolvedSecret = "resolved-key-ring-material";
    private const string LeakedBodyValue = "super-secret-value-must-not-leak";

    [SecurityTest]
    [Fact]
    public async Task ResolveSecretAsync_ThrottlingThenSuccess_RetriesOnceAndReturnsSecret()
    {
        var calls = 0;
        var delays = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(request =>
        {
            calls++;
            return calls == 1
                ? Error(HttpStatusCode.BadRequest, "ThrottlingException", "Rate exceeded")
                : Success(ResolvedSecret);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        var value = await resolver.ResolveSecretAsync(SecretReference);

        value.Should().Be(ResolvedSecret);
        calls.Should().Be(2);
        delays.Should().Be(1);
    }

    [SecurityTest]
    [Fact]
    public async Task ResolveSecretAsync_PrefixedThrottlingType_Retries()
    {
        var calls = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return calls == 1
                ? Error(HttpStatusCode.BadRequest, "com.amazonaws.secretsmanager#ThrottlingException", LeakedBodyValue)
                : Success(ResolvedSecret);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) => Task.CompletedTask);

        var value = await resolver.ResolveSecretAsync(SecretReference);

        value.Should().Be(ResolvedSecret);
        calls.Should().Be(2);
    }

    [SecurityTest]
    [Fact]
    public async Task ResolveSecretAsync_RequestLimitExceededThenSuccess_Retries()
    {
        var calls = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return calls == 1
                ? Error(HttpStatusCode.BadRequest, "RequestLimitExceeded", "slow down")
                : Success(ResolvedSecret);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) => Task.CompletedTask);

        var value = await resolver.ResolveSecretAsync(SecretReference);

        value.Should().Be(ResolvedSecret);
        calls.Should().Be(2);
    }

    [SecurityTest]
    [Fact]
    public async Task ResolveSecretAsync_InternalServiceErrorThenSuccess_Retries()
    {
        var calls = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return calls == 1
                ? Error((HttpStatusCode)500, "InternalServiceError", "transient")
                : Success(ResolvedSecret);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) => Task.CompletedTask);

        var value = await resolver.ResolveSecretAsync(SecretReference);

        value.Should().Be(ResolvedSecret);
        calls.Should().Be(2);
    }

    [SecurityTest]
    [UnitTheory]
    [InlineData(503, "null")]
    [InlineData(503, "[]")]
    [InlineData(502, "\"ThrottlingException\"")]
    public async Task ResolveSecretAsync_NonObjectServerErrorBody_RetriesWithoutReadingAType(int status, string body)
    {
        var calls = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                }
                : Success(ResolvedSecret);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) => Task.CompletedTask);

        var value = await resolver.ResolveSecretAsync(SecretReference);

        value.Should().Be(ResolvedSecret);
        calls.Should().Be(2);
    }

    [SecurityTest]
    [UnitTheory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task ResolveSecretAsync_NonObjectClientErrorBody_FailsOnceWithoutTheBody(string body)
    {
        var calls = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveSecretAsync(SecretReference));

        calls.Should().Be(1);
        exception.Message.Should().Be("AWS Secrets Manager request failed with status code 400.");
        exception.Message.Should().NotContain(body);
    }

    [SecurityTest]
    [Fact]
    public async Task ResolveSecretAsync_BareServerErrorThenSuccess_RetriesWithoutType()
    {
        var calls = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Success(ResolvedSecret);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) => Task.CompletedTask);

        var value = await resolver.ResolveSecretAsync(SecretReference);

        value.Should().Be(ResolvedSecret);
        calls.Should().Be(2);
    }

    [SecurityTest]
    [Theory]
    [InlineData("ResourceNotFoundException")]
    [InlineData("AccessDeniedException")]
    [InlineData("DecryptionFailure")]
    [InlineData("InvalidSignatureException")]
    public async Task ResolveSecretAsync_NonRetryableError_FailsOnceAndNamesTheType(string errorType)
    {
        var calls = 0;
        var delays = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return Error(HttpStatusCode.BadRequest, errorType, LeakedBodyValue);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveSecretAsync(SecretReference));

        calls.Should().Be(1);
        delays.Should().Be(0);
        exception.Message.Should().Be($"AWS Secrets Manager request failed with status code 400 ({errorType}).");
        exception.Message.Should().NotContain(LeakedBodyValue);
        exception.Message.Should().NotContain(ResolvedSecret);
    }

    [SecurityTest]
    [Fact]
    public async Task ResolveSecretAsync_ForbiddenWithoutType_FailsOnceWithoutBody()
    {
        const string body = "{\"error\":\"password=leaked-value\"}";
        var calls = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveSecretAsync(SecretReference));

        calls.Should().Be(1);
        exception.Message.Should().Be("AWS Secrets Manager request failed with status code 403.");
        exception.Message.Should().NotContain("leaked-value");
        exception.Message.Should().NotContain(body);
    }

    [SecurityTest]
    [Fact]
    public async Task ResolveSecretAsync_ThrottlingExhausted_NamesTheTypeAfterThreeAttempts()
    {
        var calls = 0;
        var delays = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return Error(HttpStatusCode.BadRequest, "ThrottlingException", LeakedBodyValue);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveSecretAsync(SecretReference));

        calls.Should().Be(3);
        delays.Should().Be(2);
        exception.Message.Should().Be("AWS Secrets Manager request failed with status code 400 (ThrottlingException).");
        exception.Message.Should().NotContain(LeakedBodyValue);
    }

    [SecurityTest]
    [Fact]
    public async Task ResolveSecretAsync_Success_DoesNotDelay()
    {
        var calls = 0;
        var delays = 0;
        using var environment = CreateEnvironment();
        using var secrets = CreateClient(_ =>
        {
            calls++;
            return Success(ResolvedSecret);
        });
        using var metadata = CreateClient(_ => throw new InvalidOperationException("metadata must not be called"));
        using var resolver = CreateResolver(secrets, metadata, (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        var value = await resolver.ResolveSecretAsync(SecretReference);

        value.Should().Be(ResolvedSecret);
        calls.Should().Be(1);
        delays.Should().Be(0);
    }

    private static AwsSecretsManagerResolver CreateResolver(
        HttpClient secrets,
        HttpClient metadata,
        Func<int, CancellationToken, Task> retryDelay)
        => new(secrets, metadata, NullLogger<AwsSecretsManagerResolver>.Instance, retryDelay);

    private static EnvironmentVariableScope CreateEnvironment()
        => new(
            ("AWS_ACCESS_KEY_ID", "AKIA_TEST_ACCESS_KEY"),
            ("AWS_SECRET_ACCESS_KEY", "test-secret-key"),
            ("AWS_SESSION_TOKEN", null),
            ("AWS_REGION", "us-east-1"),
            ("AWS_DEFAULT_REGION", null),
            ("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", null),
            ("AWS_CONTAINER_CREDENTIALS_FULL_URI", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN", null),
            ("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", null),
            ("AWS_WEB_IDENTITY_TOKEN_FILE", null),
            ("AWS_ROLE_ARN", null));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new DelegateHttpMessageHandler(handler), disposeHandler: true);

    private static HttpResponseMessage Success(string secret)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"SecretString\":\"{secret}\"}}", Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Error(HttpStatusCode status, string errorType, string message)
        => new(status)
        {
            Content = new StringContent(
                $"{{\"__type\":\"{errorType}\",\"message\":\"{message}\"}}",
                Encoding.UTF8,
                "application/json")
        };

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
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
