// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataV2ContactPhoneTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void DeserializeContactPoint_Phone_BindsTypedPropertyNotExtension()
    {
        var contact = JsonSerializer.Deserialize(
            "{\"name\":\"Operations\",\"phone\":\"+1 555 0100\"}",
            MetadataV2JsonContext.Default.MetadataV2ContactPoint)!;

        contact.Phone.Should().Be("+1 555 0100");
        contact.AdditionalProperties.Should().BeNullOrEmpty();
    }

    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData("+1 555 0100")]
    [InlineData("(555) 123-4567")]
    [InlineData("+44 20 7946 0958 ext. 12")]
    [InlineData("555.123.4567 x9")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateContactPhone_DialableNumber_Accepted(string? phone)
    {
        var errors = new List<string>();

        MetadataV2GraphValidator.ValidateContactPhone(errors, "metadata.contactPoint.phone", phone);

        errors.Should().BeEmpty();
    }

    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData("call the front desk")]
    [InlineData("555-0100<script>")]
    [InlineData("+")]
    [InlineData("555\u00010100")]
    [InlineData("555 0100\n")]
    public void ValidateContactPhone_NotANumber_Rejected(string phone)
    {
        var errors = new List<string>();

        MetadataV2GraphValidator.ValidateContactPhone(errors, "metadata.contactPoint.phone", phone);

        errors.Should().ContainSingle(error => error.StartsWith("metadata.contactPoint.phone must be a telephone number", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ValidateContactPhone_LongerThanLimit_Rejected()
    {
        var errors = new List<string>();

        MetadataV2GraphValidator.ValidateContactPhone(errors, "metadata.contacts[].phone", new string('5', MetadataV2ContactPoint.MaxPhoneLength + 1));

        errors.Should().ContainSingle(error => error.Contains($"at most {MetadataV2ContactPoint.MaxPhoneLength} characters", StringComparison.Ordinal));
    }
}
