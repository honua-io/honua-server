// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Geoprocessing.Tests;

public sealed partial class ServerJobArtifactImporterTests
{
    [UnitTheory]
    [InlineData("pg_catalog")]
    [InlineData("pg_temp_1")]
    [InlineData("information_schema")]
    [InlineData("honua")]
    [InlineData("private_metadata")]
    [InlineData("unconfigured_destination")]
    public async Task PreviewAsync_ForbiddenSchema_RefusesBeforeReadingArtifact(string schema)
    {
        var scenario = await StageScenarioAsync();
        var context = WithSchemaConfiguration(scenario.Context);
        var command = scenario.Command with { Schema = schema };

        var act = () => ServerJobArtifactImporter.PreviewAsync(command, context, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage(ImportTargetSchemaPolicy.NotOperationalSchemaMessage);
        await context.Jobs.DidNotReceive().GetJobAsync(command.Reference.JobId,
            Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        scenario.Recording.Request.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("public")]
    [InlineData("tenant_data")]
    [InlineData("project_data")]
    public async Task PreviewAsync_ConfiguredOperationalSchema_PreviewsWithoutImport(string schema)
    {
        var scenario = await StageScenarioAsync();
        var context = WithSchemaConfiguration(scenario.Context);

        var preview = await ServerJobArtifactImporter.PreviewAsync(
            scenario.Command with { Schema = schema }, context, CancellationToken.None);

        preview.FeatureCount.Should().Be(1);
        preview.ContentSha256.Should().HaveLength(64);
        scenario.Recording.Request.Should().BeNull();
    }

    private static ServerJobArtifactImportContext WithSchemaConfiguration(ServerJobArtifactImportContext context)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ImportTargetSchemaPolicy.MetadataSchemaConfigurationKey] = "private_metadata",
            [ImportTargetSchemaPolicy.DefaultOperationalSchemaConfigurationKey] = "tenant_data",
            [$"{ImportTargetSchemaPolicy.OperationalSchemasConfigurationKey}:0"] = "project_data",
            // Listing a protected schema as operational must never grant access to it.
            [$"{ImportTargetSchemaPolicy.OperationalSchemasConfigurationKey}:1"] = "private_metadata",
            [$"{ImportTargetSchemaPolicy.OperationalSchemasConfigurationKey}:2"] = "pg_catalog",
        }).Build();
        return context with
        {
            Services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider(),
        };
    }
}
