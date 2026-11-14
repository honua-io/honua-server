// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed partial class WfsFallbackGeometryTransactionTests
{
    private readonly IFeatureChangeEventPublisher _publisher = Substitute.For<IFeatureChangeEventPublisher>();

    [IntegrationTest]
    [Operation(Operations.Create, Operations.Update, Operations.Delete)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_CreateUpdateDelete_PublishesCommittedChanges()
    {
        var inserted = await SendAsync(LegacyTransaction("""
            <Insert><honua:native_rich_edits_wfs>
              <honua:name>Field observation</honua:name><honua:rank>1</honua:rank>
              <honua:score>1.5</honua:score><honua:active>false</honua:active>
              <honua:cert_owner>field-client-events</honua:cert_owner>
            </honua:native_rich_edits_wfs></Insert>
            """), HttpStatusCode.OK);
        XNamespace ogc = "http://www.opengis.net/ogc";
        var fid = XDocument.Parse(inserted).Descendants(ogc + "FeatureId").Single().Attribute("fid")!.Value;
        var id = long.Parse(fid[(fid.LastIndexOf('.') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        await SendAsync(LegacyTransaction(Update(fid, Property("name", "Updated observation"))), HttpStatusCode.OK);
        using var current = await ReadStoredAsync(id);
        current.RootElement.GetProperty("attributes").GetProperty("name").GetString().Should().Be("Updated observation");
        var deleted = await SendAsync(LegacyTransaction($"""
            <Delete typeName="honua:{TypeName}"><Filter xmlns="http://www.opengis.net/ogc">
              <FeatureId fid="{fid}" /></Filter></Delete>
            """), HttpStatusCode.OK);
        XDocument.Parse(deleted).Descendants().Single(element => element.Name.LocalName == "totalDeleted")
            .Value.Should().Be("1");

        var changes = _publisher.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IFeatureChangeEventPublisher.PublishAsync))
            .Select(call => (FeatureChangeEventRequest)call.GetArguments()[0]!).ToArray();
        changes.Select(change => change.Operation).Should().Equal("create", "update", "delete");
        changes.Should().OnlyContain(change => change.ObjectId == id && change.LayerId == 0);
        changes.Should().OnlyContain(change => !string.IsNullOrWhiteSpace(change.RequestId));
    }
}
