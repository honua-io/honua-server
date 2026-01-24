// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Tests for ImportRequest validation and cloud storage detection.
/// </summary>
public sealed class ImportRequestTests
{
    [Fact]
    public void Validate_WhenCloudFileIdIsWhitespaceAndNoFileStream_Throws()
    {
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            CloudFileId = "   "
        };

        Action act = () => request.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Either FileStream or CloudFileId must be provided*");
    }

    [Fact]
    public void Validate_WhenFileStreamProvidedAndCloudFileIdWhitespace_DoesNotThrow()
    {
        using var stream = new MemoryStream();
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            FileStream = stream,
            CloudFileId = "   "
        };

        Action act = () => request.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WhenFileStreamAndCloudFileIdProvided_Throws()
    {
        using var stream = new MemoryStream();
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            FileStream = stream,
            CloudFileId = "cloud-id"
        };

        Action act = () => request.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Only one of FileStream or CloudFileId should be provided*");
    }

    [Fact]
    public void UsesCloudStorage_WhenCloudFileIdWhitespace_ReturnsFalse()
    {
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            CloudFileId = "   "
        };

        request.UsesCloudStorage.Should().BeFalse();
    }
}
