// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class AdminOperationRequestContractTests
{
    [Theory]
    [InlineData("connectionId", ".")]
    [InlineData("connectionId", "..")]
    [InlineData("serviceName", ".")]
    [InlineData("serviceName", "..")]
    [InlineData("storageLayerId", ".")]
    [InlineData("storageLayerId", "..")]
    public async Task Validate_DotRouteValue_RejectsRequest(string parameter, string value)
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var request = RouteValueRequest(parameter, value);
        var result = await CreateApi(request.OperationId, client).ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Messages.Should().Contain(message => message.Contains(parameter, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("connectionId", ".")]
    [InlineData("connectionId", "..")]
    [InlineData("serviceName", ".")]
    [InlineData("serviceName", "..")]
    [InlineData("storageLayerId", ".")]
    [InlineData("storageLayerId", "..")]
    public async Task Submit_DotRouteValue_RejectsBeforeIssuingCredential(string parameter, string value)
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminApiOperationExecutor.HttpClientName).Returns(client);
        var store = Substitute.For<IAdminApiKeyStore>();
        var request = RouteValueRequest(parameter, value);
        var executor = new AdminApiOperationExecutor(
            AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == request.OperationId),
            factory, Context(), store, TimeProvider.System, new OperationLineageAttestationStore(TimeProvider.System));

        var submit = () => executor.SubmitAsync(request, new OperationPolicyContext { ApprovedProposalId = "approved" });

        await submit.Should().ThrowAsync<ArgumentException>();
        store.ReceivedCalls().Should().BeEmpty();
        handler.Uri.Should().BeNull();
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Submit_DryRunDotRouteValue_RejectsBeforeTransport(string value)
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var executor = CreateApi("admin.layer.publish", client);
        var submit = () => executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            ConnectionId = value,
            DryRun = true
        }, new OperationPolicyContext());

        await submit.Should().ThrowAsync<ArgumentException>();
        handler.Uri.Should().BeNull();
    }

    [Theory]
    [InlineData("roads.v2", "roads.v2")]
    [InlineData("...", "...")]
    [InlineData("%2e%2e", "%252e%252e")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("roads west", "roads%20west")]
    public async Task BindPath_OrdinaryRouteValue_PreservesEscaping(string value, string escaped)
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var request = RouteValueRequest("serviceName", value);
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == request.OperationId);

        (await CreateApi(request.OperationId, client).ValidateAsync(request)).IsValid.Should().BeTrue();
        AdminApiOperationExecutor.BindPath(definition, request).Should().Be($"/services/{escaped}/settings");
    }

    private static OperationRequest RouteValueRequest(string parameter, string value) => new()
    {
        OperationId = parameter switch
        {
            "connectionId" => "admin.layer.set-enabled",
            "serviceName" => "admin.services.settings.get",
            _ => "admin.layer.fields.get"
        },
        ConnectionId = parameter == "connectionId" ? value : null,
        ServiceName = parameter == "serviceName" ? value : null,
        Parameters = new Dictionary<string, string?>
        {
            ["storageLayerId"] = parameter == "storageLayerId" ? value : "741",
            ["enabled"] = "true"
        }
    };
}
