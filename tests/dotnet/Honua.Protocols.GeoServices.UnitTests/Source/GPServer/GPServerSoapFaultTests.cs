// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class GPServerSoapFaultTests
{
    [UnitTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void ProcessingReference_UsesCanonicalFormatter_AndPreservesSoapFaultExplanation(
        bool useContext, bool soap12)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/services/geoprocessing/GPServer";
        var parameters = useContext
            ? new Dictionary<string, string> { ["context"] = "{\"processSR\":3857}" }
            : new Dictionary<string, string> { ["env:processSR"] = "3857" };
        var refusal = GPServerEndpoints.TryParseEnvControls(context, NullLogger.Instance, parameters, out _);
        refusal.Should().NotBeNull();

        XNamespace soap = soap12
            ? "http://www.w3.org/2003/05/soap-envelope"
            : "http://schemas.xmlsoap.org/soap/envelope/";
        var fault = GPServerEndpoints.ExecutionFault(refusal!, soap);
        ((IStatusCodeHttpResult)fault).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var document = XDocument.Parse(((ContentHttpResult)fault).ResponseContent!);
        document.Descendants(soap + "Fault").Should().ContainSingle();
        var explanation = soap12
            ? document.Descendants(soap + "Text").Single().Value
            : document.Descendants("faultstring").Single().Value;
        explanation.Should().Contain("processing CRS");
        explanation.Should().Contain("not supported");
        document.Descendants("Result").Should().BeEmpty();
    }
}
