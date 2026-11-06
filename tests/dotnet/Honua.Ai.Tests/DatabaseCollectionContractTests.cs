// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Ai.Tests;

public sealed class DatabaseCollectionContractTests
{
    [UnitTest]
    public void DatabaseCollection_OwnsOnePostgresFixtureAndDoesNotRunBesideOtherCollections()
    {
        var definition = typeof(DatabaseCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        var declaredName = typeof(DatabaseCollection)
            .GetCustomAttributesData()
            .Single(attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute))
            .ConstructorArguments
            .Single()
            .Value;

        definition.Should().NotBeNull();
        declaredName.Should().Be("Database");
        definition!.DisableParallelization.Should().BeTrue();
        typeof(DatabaseCollection).GetInterfaces().Should().Contain(typeof(ICollectionFixture<PostgresFixture>));
    }
}
