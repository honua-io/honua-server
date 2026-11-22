// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

public sealed class ImageServerCatalogLikeTests
{
    private static readonly string WildcardHeavyPrefix =
        "Name LIKE '" + string.Concat(Enumerable.Repeat("%a", SqlLikePattern.MaxWildcards / 2)) + "%";

    [UnitTest]
    public void Apply_WildcardHeavyLikeOnLongValue_EvaluatesWithoutFailing()
    {
        var items = new[] { Item(1, new string('a', 10_000)) };
        var evaluator = new ImageServerCatalogFilterEvaluator();

        evaluator.Apply(items, WildcardHeavyPrefix + "b'").Should().BeEmpty();
        evaluator.Apply(items, WildcardHeavyPrefix + "a'").Should().ContainSingle();
    }

    [UnitTest]
    public void Apply_LikePatternOverWildcardLimit_ThrowsFilterException()
    {
        var pattern = string.Concat(Enumerable.Repeat("%a", SqlLikePattern.MaxWildcards + 1));

        var act = () => new ImageServerCatalogFilterEvaluator().Apply([Item(1, "a")], $"Name LIKE '{pattern}'").ToList();

        act.Should().Throw<ImageServerCatalogFilterException>();
    }

    [UnitTest]
    public void Apply_LikePatternOverLengthLimit_ThrowsFilterException()
    {
        var pattern = new string('a', SqlLikePattern.MaxPatternLength + 1);

        var act = () => new ImageServerCatalogFilterEvaluator().Apply([Item(1, "a")], $"Name LIKE '{pattern}'").ToList();

        act.Should().Throw<ImageServerCatalogFilterException>();
    }

    [UnitTest]
    public void Apply_Like_MatchesCaseInsensitivelyWithBothWildcards()
    {
        var items = new[] { Item(1, "Scene_2026_North"), Item(2, "scene-2025-south"), Item(3, "Other") };

        var matched = new ImageServerCatalogFilterEvaluator().Apply(items, "Name LIKE 'SCENE_202_%'").ToList();

        matched.Select(static item => item.ObjectId).Should().Equal(1, 2);
    }

    [UnitTest]
    public void Apply_NotLike_ExcludesMatchingItems()
    {
        var items = new[] { Item(1, "Scene_North"), Item(2, "Other") };

        var matched = new ImageServerCatalogFilterEvaluator().Apply(items, "Name NOT LIKE 'scene%'").ToList();

        matched.Select(static item => item.ObjectId).Should().Equal(2);
    }

    private static ImageServerCatalogItem Item(long objectId, string name)
        => new() { ObjectId = objectId, Name = name, PixelType = "8BUI" };
}
