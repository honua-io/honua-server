// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using System.Reflection;
using System.Xml;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Tests for WFS 2.0 capability metadata validation.
/// </summary>
public class Wfs20ComplianceValidationTests
{
    [Fact]
    public void FilterCapabilities_ShouldAdvertiseOnlyRuntimeSupportedOptionalCapabilities()
    {
        var filterCapabilities = InvokeBuildFilterCapabilities();
        var constraintValues = filterCapabilities.Conformance.Constraints
            .ToDictionary(c => c.Name, c => c.DefaultValue);

        constraintValues["ImplementsQuery"].Should().Be("TRUE");
        constraintValues["ImplementsAdHocQuery"].Should().Be("TRUE");
        constraintValues["ImplementsResourceId"].Should().Be("TRUE");
        constraintValues["ImplementsStandardFilter"].Should().Be("TRUE");
        constraintValues["ImplementsSpatialFilter"].Should().Be("TRUE");
        constraintValues["ImplementsMinTemporalFilter"].Should().Be("TRUE");
        constraintValues["ImplementsTemporalFilter"].Should().Be("FALSE");
        constraintValues["ImplementsTemporalInstant"].Should().Be("TRUE");
        constraintValues["ImplementsTemporalPeriod"].Should().Be("TRUE");
        constraintValues["ImplementsFunctions"].Should().Be("FALSE");
        constraintValues["ImplementsCQL2Text"].Should().Be("FALSE");
        constraintValues["ImplementsCQL2JSON"].Should().Be("FALSE");
        constraintValues["ImplementsCQL2ArrayOperators"].Should().Be("FALSE");
        constraintValues["ImplementsCQL2Functions"].Should().Be("FALSE");
    }

    [Fact]
    public void WfsCapabilities_WithFilterCapabilities_ShouldValidateBasicXmlShape()
    {
        var capabilities = CreateSampleWfsCapabilities();
        var isValid = ValidateBasicXmlShape(capabilities, out var validationErrors);

        validationErrors.Should().BeEmpty();
        isValid.Should().BeTrue();
    }

    [Fact]
    public void TemporalCapabilities_ShouldAdvertiseMinimumTemporalOperators()
    {
        var filterCapabilities = InvokeBuildFilterCapabilities();

        filterCapabilities.TemporalCapabilities.Should().NotBeNull();
        filterCapabilities.TemporalCapabilities!.TemporalOperators!.Operators
            .Select(op => op.Name)
            .Should().BeEquivalentTo("After", "Before", "During");
    }

    [Fact]
    public void FunctionDefinitions_ShouldBeOmittedWhenFunctionsConformanceIsFalse()
    {
        var filterCapabilities = InvokeBuildFilterCapabilities();

        filterCapabilities.Functions.Should().BeNull();
        filterCapabilities.Conformance.Constraints
            .Single(c => c.Name == "ImplementsFunctions")
            .DefaultValue.Should().Be("FALSE");
    }

    private static FilterCapabilities InvokeBuildFilterCapabilities()
    {
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities", BindingFlags.NonPublic | BindingFlags.Static);
        var result = method!.Invoke(null, null);
        return (FilterCapabilities)result!;
    }

    private static WfsCapabilities CreateSampleWfsCapabilities()
    {
        return new WfsCapabilities
        {
            ServiceIdentification = new ServiceIdentification(),
            ServiceProvider = new ServiceProvider(),
            FilterCapabilities = InvokeBuildFilterCapabilities()
        };
    }

    private static bool ValidateBasicXmlShape(WfsCapabilities capabilities, out List<string> errors)
    {
        errors = new List<string>();

        try
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(WfsCapabilities));
            using var stringWriter = new StringWriter();
            using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { Indent = true });

            serializer.Serialize(xmlWriter, capabilities);
            var xml = stringWriter.ToString();

            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);

            if (xmlDoc.SelectSingleNode("//*[local-name()='Filter_Capabilities']") == null)
            {
                errors.Add("Missing Filter_Capabilities element");
            }

            if (xmlDoc.SelectSingleNode("//*[local-name()='Conformance']") == null)
            {
                errors.Add("Missing Conformance element");
            }

            return errors.Count == 0;
        }
        catch (Exception ex)
        {
            errors.Add($"XML serialization/validation error: {ex.Message}");
            return false;
        }
    }
}
