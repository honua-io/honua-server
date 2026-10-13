// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Tests for ImportRequest validation and cloud storage detection.
/// </summary>
public sealed class ImportRequestTests
{
    [Trait("Tier", "Fast")]
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
            .WithMessage("*Either FileStream, CloudFileId, or LocalFilePath must be provided*");
    }

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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
            .WithMessage("*Only one of FileStream, CloudFileId, or LocalFilePath should be provided*");
    }

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public void Validate_WhenLocalFilePathProvided_DoesNotThrow()
    {
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            LocalFilePath = "/tmp/honua-import-test.geojson"
        };

        Action act = () => request.Validate();

        act.Should().NotThrow();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void UsesLocalFile_WhenLocalFilePathWhitespace_ReturnsFalse()
    {
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            LocalFilePath = "   "
        };

        request.UsesLocalFile.Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void LoadMode_DefaultsToReplace_ButLegacyFalseFlagResolvesToAppend()
    {
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table"
        };

        request.LoadMode.Should().Be(ImportLoadMode.Replace);
        request.EffectiveLoadMode.Should().Be(ImportLoadMode.Append);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Upsert)]
    public void EffectiveLoadMode_WhenLoadModeSet_HonorsExplicitMode(ImportLoadMode mode)
    {
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            LoadMode = mode,
            KeyColumns = mode == ImportLoadMode.Upsert ? ["id"] : []
        };

        request.EffectiveLoadMode.Should().Be(mode);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void EffectiveLoadMode_WhenOverwriteIsFalse_AppendsInsteadOfReplacing()
    {
        var request = new ImportRequest
        {
            FileName = "features.geojson",
            TableName = "features",
            OverwriteExisting = false
        };

        request.EffectiveLoadMode.Should().Be(ImportLoadMode.Append);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Validate_WhenUpsertWithoutKeyColumns_Throws()
    {
        using var stream = new MemoryStream();
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            FileStream = stream,
            LoadMode = ImportLoadMode.Upsert
        };

        Action act = () => request.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Upsert load mode requires at least one key column*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Validate_WhenUpsertWithKeyColumns_DoesNotThrow()
    {
        using var stream = new MemoryStream();
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            FileStream = stream,
            LoadMode = ImportLoadMode.Upsert,
            KeyColumns = ["id"]
        };

        Action act = () => request.Validate();

        act.Should().NotThrow();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Validate_WhenAppendWithoutKeyColumns_DoesNotThrow()
    {
        using var stream = new MemoryStream();
        var request = new ImportRequest
        {
            FileName = "data.geojson",
            TableName = "table",
            FileStream = stream,
            LoadMode = ImportLoadMode.Append
        };

        Action act = () => request.Validate();

        act.Should().NotThrow();
    }
}
