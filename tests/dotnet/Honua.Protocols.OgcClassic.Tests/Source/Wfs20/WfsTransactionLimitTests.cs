// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsTransactionLimitTests : IAsyncLifetime
{
    private const int MaxFeaturesPerEdit = 2;
    private const int MaxEditsPerTransaction = 3;
    private const string TypeName = "pub-layer-0";
    private const string InsertShape =
        "<honua:shape><gml:Point srsName=\"urn:ogc:def:crs:EPSG::4326\"><gml:pos>37.1 -122.4</gml:pos></gml:Point></honua:shape>";
    private const string TransactionLimitMessage = "Transaction exceeds the maximum of 3 edits.";

    private static readonly XNamespace Ows = "http://www.opengis.net/ows/1.1";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community)
        .OverrideConfiguration(new Dictionary<string, string?>
        {
            ["Limits:Edits:MaxFeaturesPerEdit"] = MaxFeaturesPerEdit.ToString(CultureInfo.InvariantCulture),
            ["Limits:Edits:MaxEditsPerTransaction"] = MaxEditsPerTransaction.ToString(CultureInfo.InvariantCulture)
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_MatchedFeaturesWithinActionLimitButOverTransactionLimit_NamesTransactionLimit()
    {
        var targets = await InsertTargetsAsync(MaxFeaturesPerEdit);

        var actions = Insert(MaxFeaturesPerEdit) + Delete(targets);

        await AssertRefusedAsync(actions, "Transaction", TransactionLimitMessage);
    }

    [IntegrationTheory]
    [InlineData(new[] { 1, 1, 1, 1 })]
    [InlineData(new[] { 1, 1, 2 })]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_InsertsOverTransactionLimit_ReportOneMessageAndLocator(int[] featuresPerInsert)
    {
        var actions = string.Concat(featuresPerInsert.Select(Insert));

        await AssertRefusedAsync(actions, "Transaction", TransactionLimitMessage);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_InsertOverFeaturesPerEditLimit_IsRefused()
    {
        await AssertRefusedAsync(Insert(MaxFeaturesPerEdit + 1), "Insert", "Insert action exceeds the maximum of 2 features.");
    }

    [IntegrationTheory]
    [InlineData("Update")]
    [InlineData("Delete")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_FilterMatchingOverFeaturesPerEditLimit_IsRefused(string action)
    {
        var targets = await InsertTargetsAsync(MaxFeaturesPerEdit + 1);

        var actions = action == "Update" ? Update(targets) : Delete(targets);

        await AssertRefusedAsync(actions, "Filter", $"{action} action exceeds the maximum of 2 matched features.");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_ExactlyAtTransactionLimit_IsCommitted()
    {
        var targets = await InsertTargetsAsync(1);
        var featuresBefore = int.Parse(await CountFeaturesAsync(), CultureInfo.InvariantCulture);

        using var content = new StringContent(
            TransactionDocument(Insert(MaxFeaturesPerEdit) + Delete(targets)), Encoding.UTF8, "application/xml");
        using var response = await _fixture.Client.PostAsync("/wfs", content);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var featuresAfter = int.Parse(await CountFeaturesAsync(), CultureInfo.InvariantCulture);
        featuresAfter.Should().Be(featuresBefore + MaxFeaturesPerEdit - 1);
    }

    private async Task<long[]> InsertTargetsAsync(int count)
    {
        var targets = new long[count];
        for (var index = 0; index < count; index++)
        {
            targets[index] = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, $"limit-target-{index}");
        }

        return targets;
    }

    private async Task AssertRefusedAsync(string actions, string locator, string message)
    {
        var featuresBefore = await CountFeaturesAsync();

        using var content = new StringContent(TransactionDocument(actions), Encoding.UTF8, "application/xml");
        using var response = await _fixture.Client.PostAsync("/wfs", content);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        var exception = XDocument.Parse(body).Descendants(Ows + "Exception").Single();
        exception.Attribute("exceptionCode")!.Value.Should().Be("InvalidParameterValue");
        exception.Attribute("locator")!.Value.Should().Be(locator);
        exception.Element(Ows + "ExceptionText")!.Value.Should().Contain(message);
        (await CountFeaturesAsync()).Should().Be(featuresBefore, "a refused transaction must persist nothing");
    }

    private async Task<string> CountFeaturesAsync()
    {
        using var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={TypeName}&RESULTTYPE=hits");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return XDocument.Parse(body).Root!.Attribute("numberMatched")!.Value;
    }

    private static string Insert(int featureCount) =>
        "<wfs:Insert>"
        + string.Concat(Enumerable.Range(0, featureCount)
            .Select(index => $"<honua:{TypeName}><honua:name>limit-insert-{index}</honua:name>{InsertShape}</honua:{TypeName}>"))
        + "</wfs:Insert>";

    private static string Update(IEnumerable<long> targets) =>
        $"<wfs:Update typeName=\"{TypeName}\"><wfs:Property><wfs:ValueReference>name</wfs:ValueReference>"
        + $"<wfs:Value>limit-updated</wfs:Value></wfs:Property>{ResourceFilter(targets)}</wfs:Update>";

    private static string Delete(IEnumerable<long> targets) =>
        $"<wfs:Delete typeName=\"{TypeName}\">{ResourceFilter(targets)}</wfs:Delete>";

    private static string ResourceFilter(IEnumerable<long> targets) =>
        "<fes:Filter>"
        + string.Concat(targets.Select(id => $"<fes:ResourceId rid=\"{TypeName}.{id.ToString(CultureInfo.InvariantCulture)}\" />"))
        + "</fes:Filter>";

    private static string TransactionDocument(string actions) =>
        "<wfs:Transaction service=\"WFS\" version=\"2.0.0\" "
        + "xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" xmlns:fes=\"http://www.opengis.net/fes/2.0\" "
        + "xmlns:gml=\"http://www.opengis.net/gml/3.2\" xmlns:honua=\"http://honua.io/wfs\">"
        + actions
        + "</wfs:Transaction>";
}
