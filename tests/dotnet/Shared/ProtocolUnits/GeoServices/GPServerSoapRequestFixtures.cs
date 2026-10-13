// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>Nonsecret controls captured from installed ArcPy 3.7.1 SubmitJob.</summary>
internal static class GPServerSoapRequestFixtures
{
    internal const string ArcPyDefaultControls = """
            <Options><DensifyFeatures>false</DensifyFeatures><TransportType>esriGDSTransportTypeUrl</TransportType>
            <ReturnData>false</ReturnData><UpdateValues>true</UpdateValues></Options>
            <EnvironmentValues><PropertyArray>
            <PropertySetProperty><Key>outputZFlag</Key><Value xsi:type="tns:GPString"><Value>Same As Input</Value></Value></PropertySetProperty>
            <PropertySetProperty><Key>outputMFlag</Key><Value xsi:type="tns:GPString"><Value>Same As Input</Value></Value></PropertySetProperty>
            <PropertySetProperty><Key>randomGenerator</Key><Value xsi:type="tns:GPRandomNumberGenerator"><Value>0</Value><GPRandomNumberGenerator>ACM599</GPRandomNumberGenerator></Value></PropertySetProperty>
            <PropertySetProperty><Key>autoCommit</Key><Value xsi:type="tns:GPLong"><Value>1000</Value></Value></PropertySetProperty>
            <PropertySetProperty><Key>cellSizeProjectionMethod</Key><Value xsi:type="tns:GPString"><Value>CONVERT_UNITS</Value></Value></PropertySetProperty>
            <PropertySetProperty><Key>nodata</Key><Value xsi:type="tns:GPString"><Value>NONE</Value></Value></PropertySetProperty>
            </PropertyArray></EnvironmentValues>
            """;
}
