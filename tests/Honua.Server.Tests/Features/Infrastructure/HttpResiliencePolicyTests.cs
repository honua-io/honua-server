// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure;

/// <summary>
/// Integration tests for HTTP client resilience policies.
/// Validates retry behavior, circuit breaker operation, and metrics collection.
/// </summary>
[Collection("Database")]
public class HttpResiliencePolicyTests
{
    private readonly ITestOutputHelper _output;

    public HttpResiliencePolicyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task HttpPolicy_WithTransientFailures_RetriesCorrectly()
    {
        // Arrange
        var retryCount = 0;
        var expectedRetries = 3;
        var policy = HttpResiliencePolicies.CreateFreshHttpPolicy(new ResiliencePolicyOptions
        {
            MaxRetryAttempts = expectedRetries,
            BaseDelay = TimeSpan.FromMilliseconds(10)
        });

        var context = HttpResiliencePolicies.CreateHttpContext(
            onRetry: (result, delay, attempt) =>
            {
                retryCount++;
                _output.WriteLine($"Retry {attempt}, Delay: {delay.TotalMilliseconds}ms");
            });

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            policy.ExecuteAsync(async _ =>
            {
                await Task.Delay(1);
                throw new HttpRequestException("Simulated failure");
            }, context));

        // Verify retries occurred
        Assert.Equal(expectedRetries, retryCount);
    }

    [Fact]
    public async Task HttpPolicy_WithServerError_RetriesCorrectly()
    {
        // Arrange
        var retryCount = 0;
        var policy = HttpResiliencePolicies.CreateFreshHttpPolicy(new ResiliencePolicyOptions
        {
            MaxRetryAttempts = 2,
            BaseDelay = TimeSpan.FromMilliseconds(10)
        });

        var context = HttpResiliencePolicies.CreateHttpContext(
            onRetry: (result, delay, attempt) => retryCount++);

        // Act & Assert
        var result = await policy.ExecuteAsync(async _ =>
        {
            await Task.Delay(1);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }, context);

        // Verify retries occurred
        Assert.Equal(2, retryCount);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        result.Dispose();
    }

    [Fact]
    public async Task HttpPolicy_WithSuccessfulResponse_DoesNotRetry()
    {
        // Arrange
        var retryCount = 0;
        var policy = HttpResiliencePolicies.CreateFreshHttpPolicy();

        var context = HttpResiliencePolicies.CreateHttpContext(
            onRetry: (result, delay, attempt) => retryCount++);

        // Act
        var result = await policy.ExecuteAsync(async _ =>
        {
            await Task.Delay(1);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, context);

        // Assert
        Assert.Equal(0, retryCount);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        result.Dispose();
    }

    [Fact]
    public void IsTransientHttpFailure_WithTransientErrors_ReturnsTrue()
    {
        // Arrange & Act & Assert
        var httpException = new DelegateResult<HttpResponseMessage>(new HttpRequestException("Network error"));
        Assert.True(HttpResiliencePolicies.IsTransientHttpFailure(httpException));

        var timeoutException = new DelegateResult<HttpResponseMessage>(
            new TaskCanceledException("Timeout", new TimeoutException()));
        Assert.True(HttpResiliencePolicies.IsTransientHttpFailure(timeoutException));

        var serverError = new DelegateResult<HttpResponseMessage>(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.True(HttpResiliencePolicies.IsTransientHttpFailure(serverError));

        var serviceUnavailable = new DelegateResult<HttpResponseMessage>(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.True(HttpResiliencePolicies.IsTransientHttpFailure(serviceUnavailable));

        var tooManyRequests = new DelegateResult<HttpResponseMessage>(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        Assert.True(HttpResiliencePolicies.IsTransientHttpFailure(tooManyRequests));
    }

    [Fact]
    public void IsTransientHttpFailure_WithNonTransientErrors_ReturnsFalse()
    {
        // Arrange & Act & Assert
        var notFound = new DelegateResult<HttpResponseMessage>(
            new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.False(HttpResiliencePolicies.IsTransientHttpFailure(notFound));

        var badRequest = new DelegateResult<HttpResponseMessage>(
            new HttpResponseMessage(HttpStatusCode.BadRequest));
        Assert.False(HttpResiliencePolicies.IsTransientHttpFailure(badRequest));

        var unauthorized = new DelegateResult<HttpResponseMessage>(
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.False(HttpResiliencePolicies.IsTransientHttpFailure(unauthorized));

        var argumentException = new DelegateResult<HttpResponseMessage>(
            new ArgumentException("Invalid argument"));
        Assert.False(HttpResiliencePolicies.IsTransientHttpFailure(argumentException));
    }

    [Fact]
    public void HttpResiliencePolicies_DefaultConfigurations_AreValid()
    {
        // Arrange & Act
        var httpDefaults = HttpResiliencePolicies.HttpDefaults;
        var fastApiDefaults = HttpResiliencePolicies.FastApiDefaults;
        var slowServiceDefaults = HttpResiliencePolicies.SlowServiceDefaults;

        // Assert - Verify configurations make sense
        Assert.True(httpDefaults.MaxRetryAttempts >= 0);
        Assert.True(httpDefaults.BaseDelay.TotalMilliseconds > 0);
        Assert.True(httpDefaults.CircuitBreakerFailures > 0);
        Assert.True(httpDefaults.CircuitBreakDuration.TotalSeconds > 0);

        Assert.True(fastApiDefaults.MaxRetryAttempts <= httpDefaults.MaxRetryAttempts);
        Assert.True(fastApiDefaults.BaseDelay <= httpDefaults.BaseDelay);
        Assert.True(fastApiDefaults.CircuitBreakDuration <= httpDefaults.CircuitBreakDuration);

        Assert.True(slowServiceDefaults.MaxRetryAttempts >= httpDefaults.MaxRetryAttempts);
        Assert.True(slowServiceDefaults.BaseDelay >= httpDefaults.BaseDelay);
        Assert.True(slowServiceDefaults.CircuitBreakDuration >= httpDefaults.CircuitBreakDuration);
    }

    [Fact]
    public void HttpClientResilienceExtensions_ServiceRegistration_WorksCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());

        // Act
        services.AddResilientHttpClient<TestHttpClient>(
            "test-service",
            HttpResiliencePolicies.FastApiDefaults);

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient(typeof(TestHttpClient).FullName!);

        Assert.NotNull(httpClient);
        Assert.True(httpClient.Timeout > TimeSpan.Zero);
    }

    [Fact]
    public void HttpClientResilienceExtensions_NamedClientRegistration_WorksCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());

        // Act
        services.AddResilientHttpClient(
            "test-client",
            "test-service",
            HttpResiliencePolicies.HttpDefaults);

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient("test-client");

        Assert.NotNull(httpClient);
    }

    [Fact]
    public async Task CircuitBreaker_WithConsecutiveFailures_OpensCircuit()
    {
        // Arrange
        var failureCount = 0;
        var circuitOpened = false;
        var policy = HttpResiliencePolicies.CreateFreshHttpPolicy(new ResiliencePolicyOptions
        {
            MaxRetryAttempts = 0, // No retries to test circuit breaker directly
            CircuitBreakerFailures = 3,
            CircuitBreakDuration = TimeSpan.FromMilliseconds(100)
        });

        var context = HttpResiliencePolicies.CreateHttpContext(
            onCircuitBreaker: (result, state, duration) =>
            {
                if (state == CircuitState.Open)
                {
                    circuitOpened = true;
                    _output.WriteLine($"Circuit breaker opened for {duration.TotalMilliseconds}ms");
                }
            });

        // Act - Cause failures to open circuit breaker
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await policy.ExecuteAsync(async _ =>
                {
                    await Task.Delay(1);
                    failureCount++;
                    throw new HttpRequestException($"Failure {failureCount}");
                }, context);
            }
            catch (HttpRequestException)
            {
                // Expected failures
            }
            catch (CircuitBreakerOpenException)
            {
                // Circuit breaker opened - this is what we want to test
                _output.WriteLine($"Circuit breaker blocked request after {failureCount} failures");
                break;
            }
        }

        // Assert
        Assert.True(circuitOpened, "Circuit breaker should have opened");
        Assert.True(failureCount >= 3, $"Should have at least 3 failures, got {failureCount}");
    }

    private class TestHttpClient
    {
        public TestHttpClient(HttpClient httpClient)
        {
            HttpClient = httpClient;
        }

        public HttpClient HttpClient { get; }
    }
}