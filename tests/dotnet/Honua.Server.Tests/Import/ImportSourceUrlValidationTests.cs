// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Server.Tests.Import;

public sealed class ImportSourceUrlValidationTests
{
    [Fact]
    public async Task ValidateAsync_WithPublicS3Url_ReturnsSuccess()
    {
        var result = await ImportSourceUrlValidation.ValidateAsync(
            "https://s3.amazonaws.com/sample-bucket/data.geojson",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_WithPublicAzureBlobUrl_ReturnsSuccess()
    {
        var result = await ImportSourceUrlValidation.ValidateAsync(
            "https://sample.blob.core.windows.net/container/data.geojson",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") }));

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_WithUnsupportedHost_ReturnsFailure()
    {
        var result = await ImportSourceUrlValidation.ValidateAsync(
            "https://example.com/data.geojson",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(ImportSourceUrlValidation.UnsupportedHostMessage);
    }

    [Fact]
    public async Task ValidateAsync_WithPrivateAddress_ReturnsFailure()
    {
        var result = await ImportSourceUrlValidation.ValidateAsync(
            "https://s3.amazonaws.com/sample-bucket/data.geojson",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.10") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(ImportSourceUrlValidation.DisallowedAddressMessage);
    }

    [Fact]
    public async Task ValidateAsync_WithEmbeddedCredentials_ReturnsFailure()
    {
        var result = await ImportSourceUrlValidation.ValidateAsync(
            "https://user:pass@s3.amazonaws.com/sample-bucket/data.geojson",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(ImportSourceUrlValidation.EmbeddedCredentialsMessage);
    }
}
