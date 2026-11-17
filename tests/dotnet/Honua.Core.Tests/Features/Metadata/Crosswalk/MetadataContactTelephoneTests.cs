// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Crosswalk;

[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataContactTelephoneTests
{
    private const string Telephone = "+1 555 0100";
    private static readonly XNamespace Gmd = "http://www.isotc211.org/2005/gmd";
    private static readonly XNamespace Cit = "http://standards.iso.org/iso/19115/-3/cit/2.0";

    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+1 555\n0100")]
    [InlineData("+1 555 0100 0100 0100 0100 0100 0100 0100 0100 0100 0100 0100 0100")]
    public void GraphValidation_UnusableTelephone_IsRejectedOnContactPointAndContacts(string telephone)
    {
        TelephoneErrors(telephone).Should().Contain(error => error.Contains("metadata.contactPoint.telephone"))
            .And.Contain(error => error.Contains("metadata.contacts[].telephone"));
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void GraphValidation_PublicTelephone_IsAccepted()
    {
        TelephoneErrors(Telephone).Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Writers_ContactTelephone_IsEmittedAsTheVoiceNumber()
    {
        var record = CrosswalkFixtures.FullRecord() with
        {
            Contacts =
            [
                new MetadataV2ResponsibleParty { Name = "Ana Ruiz", Email = "gis@example.org", Telephone = Telephone }
            ],
        };

        Iso19139Writer.Write(record).Descendants(Gmd + "voice").Select(element => element.Value)
            .Should().Contain(Telephone);
        Iso19115Part3Writer.Write(record).Descendants(Cit + "number").Select(element => element.Value)
            .Should().Contain(Telephone);
        DcatApWriter.Write(record, "https://maps.example.org/items/trails")["dcat:contactPoint"]![0]!
            ["vcard:hasTelephone"]!["@id"]!.GetValue<string>().Should().Be("tel:+15550100");
    }

    private static string[] TelephoneErrors(string telephone)
    {
        var graph = CrosswalkFixtures.Graph(resource: CrosswalkFixtures.FullDataset with
        {
            Contacts = [new MetadataV2ResponsibleParty { Name = "Operations", Telephone = telephone }],
        });
        graph = graph with
        {
            Metadata = graph.Metadata with
            {
                ContactPoint = new MetadataV2ContactPoint { Name = "Operations", Telephone = telephone }
            }
        };

        return MetadataV2GraphValidator.Validate(graph).Errors
            .Where(error => error.Contains("telephone", StringComparison.Ordinal))
            .ToArray();
    }
}
