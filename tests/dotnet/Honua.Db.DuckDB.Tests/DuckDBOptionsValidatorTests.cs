// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.DuckDB.Features.Infrastructure;

namespace Honua.DuckDB.Tests;

public sealed class DuckDBOptionsValidatorTests
{
    [Fact]
    public void Validate_WithValidExternalGeoParquetSource_ReturnsNoErrors()
    {
        var options = new DuckDBOptions
        {
            HttpFs = new DuckDBHttpFsOptions
            {
                S3 = new DuckDBS3Options
                {
                    Region = "us-east-1",
                    UrlStyle = "path"
                }
            },
            Layers =
            [
                new DuckDBLayerOptions
                {
                    Id = 7,
                    Table = "parcels",
                    GeometryColumn = "geom",
                    ObjectIdColumn = "id",
                    ExternalSource = new DuckDBExternalSourceOptions
                    {
                        Format = "GeoParquet",
                        Path = "s3://county/parcels/*.parquet"
                    }
                }
            ]
        };

        var errors = DuckDBOptionsValidator.Validate(options);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WithMissingExternalSourcePath_ReturnsError()
    {
        var options = BuildSingleLayerOptions(source: new DuckDBExternalSourceOptions());

        var errors = DuckDBOptionsValidator.Validate(options);

        Assert.Contains(errors, error => error.Contains("must configure Path or Paths", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithUnsupportedExternalSourceScheme_ReturnsError()
    {
        var options = BuildSingleLayerOptions(
            source: new DuckDBExternalSourceOptions { Path = "ftp://host/parcels.parquet" });

        var errors = DuckDBOptionsValidator.Validate(options);

        Assert.Contains(errors, error => error.Contains("scheme 'ftp' is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithPathAndPaths_ReturnsError()
    {
        var options = BuildSingleLayerOptions(
            source: new DuckDBExternalSourceOptions
            {
                Path = "s3://bucket/one.parquet",
                Paths = ["s3://bucket/two.parquet"]
            });

        var errors = DuckDBOptionsValidator.Validate(options);

        Assert.Contains(errors, error => error.Contains("either Path or Paths", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithCredentialQueryParameters_ReturnsError()
    {
        var options = BuildSingleLayerOptions(
            source: new DuckDBExternalSourceOptions
            {
                Path = "s3://bucket/parcels.parquet?s3_secret_access_key=secret"
            });

        var errors = DuckDBOptionsValidator.Validate(options);

        Assert.Contains(errors, error => error.Contains("must not include credential query parameters", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithInvalidS3UrlStyle_ReturnsError()
    {
        var options = BuildSingleLayerOptions(
            source: new DuckDBExternalSourceOptions { Path = "s3://bucket/parcels.parquet" });
        options.HttpFs.S3.UrlStyle = "invalid";

        var errors = DuckDBOptionsValidator.Validate(options);

        Assert.Contains(errors, error => error.Contains("UrlStyle", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithBothAzureConnectionStringAndCredentialChain_ReturnsError()
    {
        var options = BuildSingleLayerOptions(
            source: new DuckDBExternalSourceOptions { Path = "az://container/parcels.parquet" });
        options.Azure.ConnectionString = "UseDevelopmentStorage=true";
        options.Azure.CredentialChain = "cli;env";

        var errors = DuckDBOptionsValidator.Validate(options);

        Assert.Contains(errors, error => error.Contains("cannot both be configured", StringComparison.Ordinal));
    }

    private static DuckDBOptions BuildSingleLayerOptions(DuckDBExternalSourceOptions source)
        => new()
        {
            Layers =
            [
                new DuckDBLayerOptions
                {
                    Id = 1,
                    Table = "parcels",
                    GeometryColumn = "geom",
                    ObjectIdColumn = "id",
                    ExternalSource = source
                }
            ]
        };
}
