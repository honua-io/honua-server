// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Forms;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Forms;

// Exact values reach the feature writer as the text its exact-value check parses.
public sealed class FormSubmissionValueConversionTests
{
    [UnitTheory]
    [InlineData(MetadataV2FieldType.TimestampOffset, "\"2026-11-21T08:30:00.000-10:00\"", "2026-11-21T08:30:00.000-10:00")]
    [InlineData(MetadataV2FieldType.Decimal, "\"12.50\"", "12.50")]
    [InlineData(MetadataV2FieldType.Decimal, "12.50", "12.50")]
    [InlineData(MetadataV2FieldType.Decimal, "1.5e3", "1500")]
    public void ConvertJsonValue_ExactField_PassesUnquotedText(MetadataV2FieldType fieldType, string json, string expected)
    {
        FormSubmissionService.ConvertJsonValue(JsonElement.Parse(json), fieldType).Should().Be(expected);
    }
}
