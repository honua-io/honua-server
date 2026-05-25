// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services;
using Honua.Core.Features.GeoETL.Services.Connectors;
using Honua.Core.Features.GeoETL.Services.Transforms;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the GeoETL baseline domain components: the pipeline validator,
/// the reproject transform, and the attribute-filter transform.
/// </summary>
public sealed class PipelineComponentTests
{
    private static PipelineComponentFactory BuildFactory() => new(
        [new GeoJsonSourceConnector()],
        [
            new ReprojectTransform(),
            new AttributeFilterTransform(),
            new AttributeRenameTransform(),
            new AttributeCastTransform(),
            new ComputedFieldTransform()
        ],
        [new NullPreviewSinkConnector()]);

    private static PipelineDefinition ValidPipeline() => new()
    {
        Id = "pl-1",
        Name = "test",
        Stages =
        [
            new PipelineStage
            {
                Kind = PipelineStageKind.Source,
                Connector = new ConnectorConfig { Type = GeoJsonSourceConnector.ConnectorType }
            },
            new PipelineStage
            {
                Kind = PipelineStageKind.Transform,
                Transform = new TransformConfig { Type = ReprojectTransform.TransformType }
            },
            new PipelineStage
            {
                Kind = PipelineStageKind.Sink,
                Connector = new ConnectorConfig { Type = NullPreviewSinkConnector.ConnectorType }
            }
        ]
    };

    [UnitTest]
    public void Validator_AcceptsWellFormedPipeline()
    {
        var validator = new PipelineValidator(BuildFactory());

        var result = validator.Validate(ValidPipeline());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [UnitTest]
    public void Validator_RejectsUnknownConnector()
    {
        var validator = new PipelineValidator(BuildFactory());
        var pipeline = ValidPipeline() with
        {
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig { Type = "does-not-exist" }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Sink,
                    Connector = new ConnectorConfig { Type = NullPreviewSinkConnector.ConnectorType }
                }
            ]
        };

        var result = validator.Validate(pipeline);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "UNKNOWN_SOURCE");
    }

    [UnitTest]
    public void Validator_RejectsMissingSinkStage()
    {
        var validator = new PipelineValidator(BuildFactory());
        var pipeline = ValidPipeline() with
        {
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig { Type = GeoJsonSourceConnector.ConnectorType }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Transform,
                    Transform = new TransformConfig { Type = ReprojectTransform.TransformType }
                }
            ]
        };

        var result = validator.Validate(pipeline);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "MISSING_SINK");
    }

    [UnitTest]
    public async Task Reproject_Wgs84ToWebMercator_ConvertsToMeters()
    {
        var transform = new ReprojectTransform();
        var config = new TransformConfig
        {
            Type = ReprojectTransform.TransformType,
            Options = new Dictionary<string, string> { ["fromSrid"] = "4326", ["toSrid"] = "3857" }
        };
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var source = Single(new Feature(factory.CreatePoint(new Coordinate(13.405, 52.52)), new AttributesTable()));

        var results = new List<IFeature>();
        await foreach (var feature in transform.TransformAsync(config, source))
        {
            results.Add(feature);
        }

        var point = (Point)results.Single().Geometry!;
        point.SRID.Should().Be(3857);
        point.X.Should().BeApproximately(1_492_273, 2000);
        point.Y.Should().BeApproximately(6_894_582, 5000);
    }

    [UnitTest]
    public async Task Reproject_RejectsUnsupportedDatumShift()
    {
        var transform = new ReprojectTransform();
        var config = new TransformConfig
        {
            Type = ReprojectTransform.TransformType,
            Options = new Dictionary<string, string> { ["fromSrid"] = "4326", ["toSrid"] = "27700" }
        };
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var source = Single(new Feature(factory.CreatePoint(new Coordinate(0, 0)), new AttributesTable()));

        var act = async () =>
        {
            await foreach (var _ in transform.TransformAsync(config, source))
            {
            }
        };

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [UnitTest]
    public async Task AttributeFilter_KeepsOnlyMatchingFeatures()
    {
        var transform = new AttributeFilterTransform();
        var config = new TransformConfig
        {
            Type = AttributeFilterTransform.TransformType,
            Options = new Dictionary<string, string> { ["field"] = "score", ["op"] = "gte", ["value"] = "50" }
        };
        var factory = new GeometryFactory(new PrecisionModel(), 4326);

        var low = new Feature(factory.CreatePoint(new Coordinate(0, 0)),
            new AttributesTable { { "score", 10L } });
        var high = new Feature(factory.CreatePoint(new Coordinate(1, 1)),
            new AttributesTable { { "score", 90L } });

        var results = new List<IFeature>();
        await foreach (var feature in transform.TransformAsync(config, Many(low, high)))
        {
            results.Add(feature);
        }

        results.Should().HaveCount(1);
        results.Single().Attributes!.GetOptionalValue("score").Should().Be(90L);
    }

    [UnitTest]
    public void Validator_FailsFast_WhenTransformRequiresFieldSourceDoesNotDeclare()
    {
        var validator = new PipelineValidator(BuildFactory());
        var pipeline = ValidPipeline() with
        {
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig
                    {
                        Type = GeoJsonSourceConnector.ConnectorType,
                        Options = new Dictionary<string, string> { ["declaredFields"] = "name,score" }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Transform,
                    Transform = new TransformConfig
                    {
                        Type = AttributeRenameTransform.TransformType,
                        Options = new Dictionary<string, string> { ["from"] = "missing", ["to"] = "renamed" }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Sink,
                    Connector = new ConnectorConfig { Type = NullPreviewSinkConnector.ConnectorType }
                }
            ]
        };

        var result = validator.Validate(pipeline);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "SCHEMA_FIELD_UNAVAILABLE");
    }

    [UnitTest]
    public void Validator_PassesSchemaChain_WhenEarlierStageProducesRequiredField()
    {
        var validator = new PipelineValidator(BuildFactory());
        var pipeline = ValidPipeline() with
        {
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig
                    {
                        Type = GeoJsonSourceConnector.ConnectorType,
                        Options = new Dictionary<string, string> { ["declaredFields"] = "first,last" }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Transform,
                    Transform = new TransformConfig
                    {
                        Type = ComputedFieldTransform.TransformType,
                        Options = new Dictionary<string, string>
                        {
                            ["target"] = "full", ["op"] = "concat", ["fields"] = "first,last"
                        }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Transform,
                    Transform = new TransformConfig
                    {
                        Type = AttributeFilterTransform.TransformType,
                        Options = new Dictionary<string, string> { ["field"] = "full", ["op"] = "exists" }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Sink,
                    Connector = new ConnectorConfig { Type = NullPreviewSinkConnector.ConnectorType }
                }
            ]
        };

        var result = validator.Validate(pipeline);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [UnitTest]
    public void Validator_SkipsSchemaChain_WhenSourceDeclaresNoFields()
    {
        // Permissive passthrough: a source with a dynamic schema (no declaredFields) must
        // not be blocked by stage-chain field reachability.
        var validator = new PipelineValidator(BuildFactory());
        var pipeline = ValidPipeline() with
        {
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig { Type = GeoJsonSourceConnector.ConnectorType }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Transform,
                    Transform = new TransformConfig
                    {
                        Type = AttributeRenameTransform.TransformType,
                        Options = new Dictionary<string, string> { ["from"] = "whatever", ["to"] = "x" }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Sink,
                    Connector = new ConnectorConfig { Type = NullPreviewSinkConnector.ConnectorType }
                }
            ]
        };

        var result = validator.Validate(pipeline);

        result.IsValid.Should().BeTrue();
    }

    private static async IAsyncEnumerable<IFeature> Single(IFeature feature)
    {
        yield return feature;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<IFeature> Many(params IFeature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }

        await Task.CompletedTask;
    }
}
