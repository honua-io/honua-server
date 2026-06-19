// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Tiles;

public class TileMatrixSetOptionsValidatorTests
{
    private static readonly TileMatrixSetOptionsValidator Validator = new();

    private static CustomTileMatrixSet Valid(string id = "Custom1")
        => new()
        {
            Id = id,
            Crs = "http://www.opengis.net/def/crs/EPSG/0/3857",
            Srid = 3857,
            TopLeftCorner = [-20037508.34, 20037508.34],
            TileWidth = 256,
            TileHeight = 256,
            Levels =
            [
                new TileMatrixLevel { Id = 0, ScaleDenominator = 500_000_000, CellSize = 140_000, MatrixWidth = 1, MatrixHeight = 1 },
                new TileMatrixLevel { Id = 1, ScaleDenominator = 250_000_000, CellSize = 70_000, MatrixWidth = 2, MatrixHeight = 2 }
            ]
        };

    private static ValidateOptionsResult Validate(params CustomTileMatrixSet[] custom)
    {
        var options = new TileMatrixSetDefinitionOptions();
        foreach (var entry in custom)
        {
            options.Custom.Add(entry);
        }

        return Validator.Validate(Options.DefaultName, options);
    }

    [Fact]
    public void Validate_NoCustom_Succeeds()
    {
        Validate().Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidCustom_Succeeds()
    {
        Validate(Valid()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("WebMercatorQuad")]
    [InlineData("worldcrs84quad")]
    public void Validate_ReservedIdCollision_Fails(string reservedId)
    {
        var result = Validate(Valid(reservedId));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("reserved built-in"));
    }

    [Fact]
    public void Validate_DuplicateId_Fails()
    {
        var result = Validate(Valid("Dup"), Valid("Dup"));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("duplicate"));
    }

    [Fact]
    public void Validate_NonMonotonicScaleDenominators_Fails()
    {
        var custom = Valid();
        // Level 1 scale denominator must be strictly smaller than level 0; make it larger.
        custom.Levels[1].ScaleDenominator = 600_000_000;

        var result = Validate(custom);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("ScaleDenominator must be strictly smaller"));
    }

    [Fact]
    public void Validate_NonPositiveMatrixDimensions_Fails()
    {
        var custom = Valid();
        custom.Levels[0].MatrixWidth = 0;

        var result = Validate(custom);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("MatrixWidth and MatrixHeight"));
    }

    [Fact]
    public void Validate_InvalidSrid_Fails()
    {
        var custom = Valid();
        custom.Srid = 0;

        var result = Validate(custom);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("positive Srid"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Validate_TopLeftCornerWrongArity_Fails(int arity)
    {
        var custom = Valid();
        custom.TopLeftCorner = new double[arity];

        var result = Validate(custom);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("TopLeftCorner"));
    }

    [Fact]
    public void Validate_NoLevels_Fails()
    {
        var custom = Valid();
        custom.Levels.Clear();

        var result = Validate(custom);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("at least one level"));
    }

    [Fact]
    public void Validate_EmptyId_Fails()
    {
        var custom = Valid();
        custom.Id = "  ";

        var result = Validate(custom);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("non-empty Id"));
    }
}
