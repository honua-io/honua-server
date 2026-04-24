// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.NlQuery;
using Honua.Core.Features.NlQuery.Abstractions;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.NlQuery;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.NlQuery;

[Protocol(TestProtocols.TestQuality)]
public sealed class NlQueryPlanProviderTests
{
    private readonly LayerDefinition _testLayer;

    public NlQueryPlanProviderTests()
    {
        _testLayer = new LayerDefinition(
            Id: 1,
            Name: "test_parks",
            Description: "Parks layer",
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 100),
                new FieldDefinition("population", FieldType.Integer),
                new FieldDefinition("shape", FieldType.Geometry)
            ]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_ValidResponse_ReturnsSuccessWithFilterPlan()
    {
        var responseJson = """
        {
          "id": "chatcmpl-test-123",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": "{\"combinator\":\"and\",\"clauses\":[{\"type\":\"comparison\",\"comparison\":{\"property\":\"name\",\"operator\":\"eq\",\"value\":\"Portland\"}}]}" },
            "finish_reason": "stop"
          }],
          "usage": { "prompt_tokens": 100, "completion_tokens": 50, "total_tokens": 150 }
        }
        """;

        var provider = CreateProvider(responseJson, HttpStatusCode.OK);
        var request = new NlQueryPlanRequest("Show me Portland", _testLayer, "test-collection");

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.Clauses.Should().HaveCount(1);
        result.Plan.Clauses[0].Type.Should().Be("comparison");
        result.Plan.Clauses[0].Comparison!.Property.Should().Be("name");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_SpatialResponse_ReturnsFilterPlanWithSpatialClause()
    {
        var responseJson = """
        {
          "id": "chatcmpl-test-456",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": "{\"combinator\":\"and\",\"clauses\":[{\"type\":\"spatial\",\"spatial\":{\"operator\":\"dwithin\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[-122.6765,45.5231]},\"distance\":5,\"distanceUnit\":\"kilometers\"}}]}" },
            "finish_reason": "stop"
          }]
        }
        """;

        var provider = CreateProvider(responseJson, HttpStatusCode.OK);
        var request = new NlQueryPlanRequest("Parks within 5 km of downtown Portland", _testLayer, "test-collection");

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Plan!.Clauses[0].Type.Should().Be("spatial");
        result.Plan.Clauses[0].Spatial!.Operator.Should().Be("dwithin");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_MalformedContent_ReturnsFailure()
    {
        var responseJson = """
        {
          "id": "chatcmpl-test-789",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": "This is not valid JSON at all" },
            "finish_reason": "stop"
          }]
        }
        """;

        var provider = CreateProvider(responseJson, HttpStatusCode.OK);
        var request = new NlQueryPlanRequest("test query", _testLayer);

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("parse");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_HttpError_ReturnsFailure()
    {
        var provider = CreateProvider("Internal Server Error", HttpStatusCode.InternalServerError);
        var request = new NlQueryPlanRequest("test query", _testLayer);

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("500");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_EmptyChoices_ReturnsFailure()
    {
        var responseJson = """
        {
          "id": "chatcmpl-test-empty",
          "choices": []
        }
        """;

        var provider = CreateProvider(responseJson, HttpStatusCode.OK);
        var request = new NlQueryPlanRequest("test query", _testLayer);

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureDisabled_ProviderNotRegistered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NlQuery:Enabled"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNlQuery(configuration);

        var sp = services.BuildServiceProvider();
        var provider = sp.GetService<INlQueryPlanProvider>();

        provider.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureEnabled_ProviderIsRegistered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NlQuery:Enabled"] = "true",
                ["NlQuery:Provider"] = "openai",
                ["NlQuery:Endpoint"] = "https://api.openai.com/v1",
                ["NlQuery:Model"] = "gpt-4o",
                ["NlQuery:ApiKey"] = "test-key"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNlQuery(configuration);

        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetService<INlQueryPlanProvider>();

        provider.Should().NotBeNull();
        provider.Should().BeOfType<OpenAiNlQueryPlanProvider>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureEnabled_WithUnsupportedProvider_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NlQuery:Enabled"] = "true",
                ["NlQuery:Provider"] = "anthropic",
                ["NlQuery:Endpoint"] = "https://example.com/v1",
                ["NlQuery:Model"] = "test-model",
                ["NlQuery:ApiKey"] = "test-key"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddNlQuery(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Unsupported NlQuery provider 'anthropic'. Only 'openai' is supported.");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ConfigurationValidator_WithHttpEndpoint_FailsValidation()
    {
        var validator = new NlQueryConfigurationValidator();
        var options = new NlQueryConfiguration
        {
            Enabled = true,
            Provider = "openai",
            Endpoint = "http://example.com/v1",
            Model = "gpt-4o",
            ApiKey = "test-key",
            TimeoutSeconds = 30,
            MaxTokens = 1024
        };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("must use HTTPS", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureEnabled_WithEnvironmentApiKey_BindsOptionsWithoutMutatingConfiguration()
    {
        const string envVariableName = "HONUA_NLQUERY_API_KEY";
        var previousValue = Environment.GetEnvironmentVariable(envVariableName);

        try
        {
            Environment.SetEnvironmentVariable(envVariableName, "env-key");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NlQuery:Enabled"] = "true",
                    ["NlQuery:Provider"] = "openai",
                    ["NlQuery:Endpoint"] = "https://api.openai.com/v1",
                    ["NlQuery:Model"] = "gpt-4o"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNlQuery(configuration);

            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<NlQueryConfiguration>>().Value;

            options.ApiKey.Should().Be("env-key");
            configuration["NlQuery:ApiKey"].Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVariableName, previousValue);
        }
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void FeatureNotConfigured_ProviderNotRegistered()
    {
        // No NlQuery section at all
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNlQuery(configuration);

        var sp = services.BuildServiceProvider();
        var provider = sp.GetService<INlQueryPlanProvider>();

        provider.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_RequestSchema_EmitsValidJsonSchemaForValue()
    {
        // Regression: the anyOf for comparison.value must contain schema objects, not raw type strings.
        var successResponse = """
        {
          "id": "chatcmpl-schema-test",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": "{\"combinator\":\"and\",\"clauses\":[]}" },
            "finish_reason": "stop"
          }]
        }
        """;

        var handler = new MockHttpMessageHandler(successResponse, HttpStatusCode.OK);
        var provider = CreateProvider(handler);
        var request = new NlQueryPlanRequest("test", _testLayer, "schema-test");

        await provider.GeneratePlanAsync(request);

        handler.CapturedRequestBody.Should().NotBeNull();
        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var schema = doc.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema");
        var valueSchema = schema
            .GetProperty("properties")
            .GetProperty("clauses")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("comparison")
            .GetProperty("properties")
            .GetProperty("value");

        // Must use anyOf with schema objects, not raw type strings
        var anyOf = valueSchema.GetProperty("anyOf");
        anyOf.GetArrayLength().Should().BeGreaterThanOrEqualTo(3);
        foreach (var item in anyOf.EnumerateArray())
        {
            item.ValueKind.Should().Be(JsonValueKind.Object, "anyOf items must be schema objects");
            item.TryGetProperty("type", out _).Should().BeTrue("each anyOf item must have a 'type' property");
        }
    }

    private OpenAiNlQueryPlanProvider CreateProvider(string responseBody, HttpStatusCode statusCode)
    {
        var handler = new MockHttpMessageHandler(responseBody, statusCode);
        return CreateProvider(handler);
    }

    private OpenAiNlQueryPlanProvider CreateProvider(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var factory = new MockHttpClientFactory(httpClient);

        var config = new NlQueryConfiguration
        {
            Enabled = true,
            Provider = "openai",
            Endpoint = "https://api.openai.com/v1",
            Model = "gpt-4o",
            ApiKey = "test-api-key",
            TimeoutSeconds = 30,
            MaxTokens = 1024
        };

        return new OpenAiNlQueryPlanProvider(
            factory,
            Options.Create(config),
            NullLogger<OpenAiNlQueryPlanProvider>.Instance);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public MockHttpMessageHandler(string responseBody, HttpStatusCode statusCode)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        public string? CapturedRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
            };
            return response;
        }
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public MockHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }
}
