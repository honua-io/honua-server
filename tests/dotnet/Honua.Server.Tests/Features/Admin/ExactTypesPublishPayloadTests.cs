// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ExactTypesPublishPayloadTests
{
    [UnitTest]
    public void DurablePublishPayload_ExactTypes_RoundTripsThroughDomainAndHttp()
    {
        var payload = CreatePayload(new PublishLayerRequest
        {
            Schema = "public",
            Table = "ledger",
            LayerName = "Ledger",
            ExactTypes = true
        });

        var parameters = new Dictionary<string, string> { [LayerPublishJobPayload.ParameterName] = payload.ToJson() };
        LayerPublishJobPayload.TryParse(parameters, out var restored, out _).Should().BeTrue();
        restored!.Request.ExactTypes.Should().BeTrue();

        var domain = LayerPublishRequestMapper.CreateDomain(restored.Request, payload.ConnectionId, null);
        domain.ExactTypes.Should().BeTrue();
        LayerPublishRequestMapper.ToHttp(domain).ExactTypes.Should().BeTrue();
    }

    [UnitTest]
    public void DurablePublishPayload_OmittedExactTypes_StaysOff()
    {
        var payload = CreatePayload(new PublishLayerRequest { Schema = "public", Table = "ledger", LayerName = "Ledger" });

        var parameters = new Dictionary<string, string> { [LayerPublishJobPayload.ParameterName] = payload.ToJson() };
        LayerPublishJobPayload.TryParse(parameters, out var restored, out _).Should().BeTrue();

        var domain = LayerPublishRequestMapper.CreateDomain(restored!.Request, payload.ConnectionId, null);
        domain.ExactTypes.Should().BeFalse();
        LayerPublishRequestMapper.ToHttp(domain).ExactTypes.Should().BeFalse();
    }

    [UnitTest]
    public void ServicePublishDescriptor_DeclaresOptionalExactTypesFlag()
    {
        var parameter = ServicePublishOperation.BuildDescriptor().InputSchema
            .Single(candidate => candidate.Name == "exactTypes");

        parameter.Required.Should().BeFalse();
        parameter.Schema.Type.Should().Be(WorkflowSchemaValueType.Flag);
    }

    private static LayerPublishJobPayload CreatePayload(PublishLayerRequest request)
        => new()
        {
            ConnectionId = Guid.NewGuid(),
            ConnectionKey = "test",
            RowCount = 1,
            Request = request
        };
}
