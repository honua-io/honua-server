// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing.Inference;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed class ImageryDisclosureTests
{
    [UnitTest]
    public async Task InferAsync_UnapprovedOrOverriddenModel_SendsNoPayloadAndResolvesNoSecret()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        var secrets = Substitute.For<ISecretProvider>();
        var client = new HttpImageryInferenceClient(factory, NullLogger<HttpImageryInferenceClient>.Instance, secrets);
        var options = new ImageryInferenceOptions { Provider = "http", Endpoint = "https://inference.example/v1", ApiKey = "secret:imagery" };
        var request = new ImageryInferenceRequest { Model = "actual-model", Task = "classification", ImageBytes = [1, 2, 3], MaxArtifactBytes = 1024 };
        await FluentActions.Awaiting(() => client.InferAsync(options, request, default)).Should().ThrowAsync<ImageryInferenceException>().WithMessage("*disclosure*");
        options.Disclosure.Approvals.Add(new AiDisclosureApproval
        {
            Provider = "http",
            Kind = "http",
            Destination = options.Endpoint,
            Model = "other-model",
            UseCase = "imagery-inference",
            DataClassification = "unclassified"
        });
        await FluentActions.Awaiting(() => client.InferAsync(options, request, default)).Should().ThrowAsync<ImageryInferenceException>().WithMessage("*disclosure*");
        handler.Calls.Should().Be(0);
        factory.DidNotReceive().CreateClient(Arg.Any<string>());
        secrets.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task InferAsync_ExplicitApproval_SubmitsPayload()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        var client = new HttpImageryInferenceClient(factory, NullLogger<HttpImageryInferenceClient>.Instance);
        var options = new ImageryInferenceOptions { Provider = "http", Endpoint = "https://inference.example/v1" };
        options.Disclosure.Approvals.Add(new AiDisclosureApproval
        {
            Provider = "http",
            Kind = "http",
            Destination = options.Endpoint,
            Model = "approved-model",
            UseCase = "imagery-inference",
            DataClassification = "unclassified"
        });
        await client.InferAsync(options, new ImageryInferenceRequest { Model = "approved-model", Task = "classification", ImageBytes = [1, 2, 3], MaxArtifactBytes = 1024 }, default);
        handler.Calls.Should().Be(1);
        handler.Payload.Should().Contain("approved-model").And.Contain("AQID");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"outputType\":\"raster\",\"raster\":\"AQID\"}") };
        }
    }
}
