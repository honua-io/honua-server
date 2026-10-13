// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Grounding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp.Grounding;

/// <summary>
/// Unit tests for <see cref="GroundingToolMapper"/>. Pin the wire↔domain
/// translations for both grounding-tool arguments and the grounding output.
/// The MCP error mapper depends on <see cref="GeoprocessingValidationException"/>
/// being thrown for invalid shapes, so each failure mode is asserted here.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class GroundingToolMapperTests
{
    // -----------------------------------------------------------------------
    // McpGroundCandidatesArgument → GroundingRequest
    // -----------------------------------------------------------------------

    [UnitTest]
    public void GroundCandidatesToDomain_NullArgument_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain((McpGroundCandidatesArgument?)null);

        act.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    public void GroundCandidatesToDomain_MissingGoal_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument { Goal = " " });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*non-empty*");
    }

    [UnitTest]
    public void GroundCandidatesToDomain_UnknownWorkflowFamilyHint_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "buffer",
            WorkflowFamilyHint = "NotAFamily"
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*workflowFamilyHint*");
    }

    [UnitTest]
    public void GroundCandidatesToDomain_UnknownAssumptionPolicy_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "buffer",
            AssumptionPolicy = "Mystery"
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*assumptionPolicy*");
    }

    [UnitTest]
    public void GroundCandidatesToDomain_NumericWorkflowFamilyHint_ThrowsValidation()
    {
        // Enum.TryParse accepts underlying numeric values — the mapper
        // must reject them so undefined enum strings never reach
        // workflowFamily.value on the wire.
        var act = () => GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "buffer",
            WorkflowFamilyHint = "999"
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*workflowFamilyHint*");
    }

    [UnitTest]
    public void GroundCandidatesToDomain_NumericAssumptionPolicy_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "buffer",
            AssumptionPolicy = "999"
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*assumptionPolicy*");
    }

    [UnitTest]
    public void GroundCandidatesToDomain_WhitespaceIntentId_NormalizesToNullSoServiceAllocatesFreshId()
    {
        // A whitespace intentId must not leak into draftIntent.intentId /
        // clarification.intentId — the clarify tool rejects blank intentIds,
        // which would strand the caller. Treat it as omitted.
        var request = GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "buffer",
            IntentId = "   "
        });

        request.IntentId.Should().BeNull();
    }

    [UnitTest]
    public void GroundCandidatesToDomain_BlankExplicitInputs_AreFilteredOut()
    {
        // Blank entries must not become sourceId candidates for the drafted
        // publish intent — PublishIntent.CreateDraft preserves whatever it is
        // given, so filtering must happen at the MCP boundary.
        var request = GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "publish parcels",
            ExplicitInputs = ["   ", "parcels", ""]
        });

        request.ExplicitInputs.Should().ContainSingle().Which.Should().Be("parcels");
    }

    [UnitTest]
    public void GroundCandidatesToDomain_AllBlankExplicitInputs_CollapseToEmptyList()
    {
        var request = GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "publish parcels",
            ExplicitInputs = ["   ", ""]
        });

        request.ExplicitInputs.Should().BeEmpty();
    }

    [UnitTest]
    public void GroundCandidatesToDomain_ExplicitInputs_AreTrimmed()
    {
        // Padded values like "  parcels-layer  " must be trimmed here — IntentDrafter
        // uses ExplicitInputs[0] as PublishIntent.SourceId, and downstream source
        // lookups match on the exact id, so retained padding would silently break
        // resolution while also suppressing the publish.source clarification.
        var request = GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "publish parcels",
            ExplicitInputs = ["  parcels-layer  ", "\tstreets\n"]
        });

        request.ExplicitInputs.Should().BeEquivalentTo(["parcels-layer", "streets"]);
    }

    [UnitTest]
    public void GroundCandidatesArgumentSchema_RequiresMinLengthOnIdentifierFields()
    {
        // Schema-driven clients rely on the advertised contract to prevent
        // whitespace-only identifier payloads from reaching the server.
        var schema = GroundingToolSchemas.GroundCandidatesArgumentSchema;
        var properties = schema.GetProperty("properties");

        properties.GetProperty("intentId").GetProperty("minLength").GetInt32().Should().Be(1);
        properties.GetProperty("explicitInputs").GetProperty("items")
            .GetProperty("minLength").GetInt32().Should().Be(1);
    }

    [UnitTest]
    public void GroundCandidatesToDomain_DefaultsAssumptionPolicyToAskWhenMaterial()
    {
        var request = GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
        {
            Goal = "buffer"
        });

        request.AssumptionPolicy.Should().Be(AssumptionPolicy.AskWhenMaterial);
    }

    [UnitTest]
    public void GroundCandidatesToDomain_FullPayload_RoundTripsAllFields()
    {
        var argument = new McpGroundCandidatesArgument
        {
            Goal = "Buffer the parcels",
            WorkflowFamilyHint = "Analyze",
            AssumptionPolicy = "UseDefaults",
            ExplicitInputs = ["layer-1"],
            IntentId = "intent-x",
            Constraints = new McpIntentConstraintsInput
            {
                AreaOfInterest = "POLYGON EMPTY",
                SpatialReferenceId = 3857,
                Units = "feet",
                TimeWindowStart = DateTimeOffset.Parse("2025-01-01Z", CultureInfo.InvariantCulture),
                TimeWindowEnd = DateTimeOffset.Parse("2025-02-01Z", CultureInfo.InvariantCulture)
            },
            Context = new McpCallerContextInput
            {
                WorkspaceId = "ws-1",
                PriorIntentId = "intent-prev",
                PromotionScope = "shared"
            }
        };

        var request = GroundingToolMapper.ToDomain(argument);

        request.Goal.Should().Be("Buffer the parcels");
        request.WorkflowFamilyHint.Should().Be(WorkflowFamily.Analyze);
        request.AssumptionPolicy.Should().Be(AssumptionPolicy.UseDefaults);
        request.ExplicitInputs.Should().BeEquivalentTo("layer-1");
        request.IntentId.Should().Be("intent-x");
        request.Constraints!.AreaOfInterest.Should().Be("POLYGON EMPTY");
        request.Constraints.SpatialReferenceId.Should().Be(3857);
        request.Constraints.Units.Should().Be("feet");
        request.Context.WorkspaceId.Should().Be("ws-1");
        request.Context.PriorIntentId.Should().Be("intent-prev");
        request.Context.PromotionScope.Should().Be("shared");
    }

    // -----------------------------------------------------------------------
    // McpClarifyIntentArgument → GroundingRequest
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ClarifyIntentToDomain_MissingIntentId_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpClarifyIntentArgument
        {
            Goal = "buffer",
            Response = new McpClarificationResponseInput
            {
                Answers = new Dictionary<string, IReadOnlyList<string>> { ["q1"] = ["answer"] }
            }
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*intentId*");
    }

    [UnitTest]
    public void ClarifyIntentToDomain_MissingResponse_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpClarifyIntentArgument
        {
            IntentId = "intent-1",
            Goal = "buffer",
            Response = null
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*response.answers*");
    }

    [UnitTest]
    public void ClarifyIntentToDomain_EmptyAnswers_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpClarifyIntentArgument
        {
            IntentId = "intent-1",
            Goal = "buffer",
            Response = new McpClarificationResponseInput
            {
                Answers = new Dictionary<string, IReadOnlyList<string>>()
            }
        });

        act.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    public void ClarifyIntentToDomain_MissingGoal_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpClarifyIntentArgument
        {
            IntentId = "intent-1",
            Goal = null,
            Response = new McpClarificationResponseInput
            {
                Answers = new Dictionary<string, IReadOnlyList<string>> { ["q1"] = ["answer"] }
            }
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*goal*");
    }

    [UnitTest]
    public void ClarifyIntentToDomain_ValidPayload_CarriesClarificationResponse()
    {
        var argument = new McpClarifyIntentArgument
        {
            IntentId = "intent-1",
            Goal = "Buffer the parcels",
            Response = new McpClarificationResponseInput
            {
                Answers = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["param.distance"] = ["50"]
                }
            }
        };

        var request = GroundingToolMapper.ToDomain(argument);

        request.IntentId.Should().Be("intent-1");
        request.ClarificationResponse.Should().NotBeNull();
        request.ClarificationResponse!.IntentId.Should().Be("intent-1");
        request.ClarificationResponse.Answers.Should().ContainKey("param.distance");
    }

    [UnitTest]
    public void ClarifyIntentToDomain_WhitespaceOnlyAnswerValue_ThrowsValidation()
    {
        var act = () => GroundingToolMapper.ToDomain(new McpClarifyIntentArgument
        {
            IntentId = "intent-1",
            Goal = "Buffer the parcels",
            Response = new McpClarificationResponseInput
            {
                Answers = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["publish.source"] = ["   "]
                }
            }
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*publish.source*non-blank*");
    }

    [UnitTest]
    public void ClarifyIntentToDomain_BlankAnswerQuestionId_ThrowsValidation()
    {
        // ClarificationAnswerResolver drops blank/whitespace question ids, so
        // the MCP boundary must reject them up front instead of accepting a
        // silently-ignored clarification turn.
        var act = () => GroundingToolMapper.ToDomain(new McpClarifyIntentArgument
        {
            IntentId = "intent-1",
            Goal = "Buffer the parcels",
            Response = new McpClarificationResponseInput
            {
                Answers = new Dictionary<string, IReadOnlyList<string>>
                {
                    [""] = ["x"]
                }
            }
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*questionId*non-blank*");
    }

    // -----------------------------------------------------------------------
    // GroundingResult → McpGroundingOutput
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToWire_SerializesEnumsAsStringsForJsonFriendlyOutput()
    {
        var result = new GroundingResult
        {
            WorkflowFamily = new WorkflowFamilyClassification
            {
                Value = WorkflowFamily.PublishData,
                Confidence = 0.9,
                Evidence = ["verb:publish"]
            },
            DraftIntent = new DraftIntent
            {
                IntentId = "intent-1",
                Goal = "Publish parcels",
                WorkflowFamily = WorkflowFamily.PublishData,
                RequestedOutputs = [ArtifactKind.FeatureLayer],
                AssumptionPolicy = AssumptionPolicy.AskWhenMaterial,
                Publishing = PublishIntent.CreateDraft(
                    "intent-1",
                    PublishSourceKind.FeatureLayer,
                    "parcels",
                    PublishTargetKind.FeatureService),
                Provenance = new ProvenanceRecord
                {
                    Sources = [new ProvenanceSource { SourceId = "parcels", Description = "Parcels" }],
                    ProcessDefinitions = []
                }
            },
            Candidates = new CandidateRanking
            {
                Datasets =
                [
                    new GroundingCandidate
                    {
                        Id = "parcels",
                        Kind = CandidateKind.Dataset,
                        DisplayName = "Parcels",
                        Score = 0.9,
                        ConfidenceBand = ConfidenceBand.High,
                        Evidence = ["name:2"]
                    }
                ]
            },
            Engine = "deterministic"
        };

        var wire = GroundingToolMapper.ToWire(result);

        wire.Engine.Should().Be("deterministic");
        wire.WorkflowFamily.Value.Should().Be(nameof(WorkflowFamily.PublishData));
        wire.DraftIntent.WorkflowFamily.Should().Be(nameof(WorkflowFamily.PublishData));
        wire.DraftIntent.AssumptionPolicy.Should().Be(nameof(AssumptionPolicy.AskWhenMaterial));
        wire.DraftIntent.Publishing.Should().NotBeNull();
        wire.DraftIntent.Publishing!.SourceKind.Should().Be(nameof(PublishSourceKind.FeatureLayer));
        wire.DraftIntent.Publishing.TargetKind.Should().Be(nameof(PublishTargetKind.FeatureService));
        wire.DraftIntent.Publishing.Status.Should().Be(nameof(PublishIntentStatus.Draft));
        wire.Candidates.Datasets.Should().ContainSingle()
            .Which.ConfidenceBand.Should().Be(nameof(ConfidenceBand.High));
    }

    // -----------------------------------------------------------------------
    // Published clarify-intent JSON schema — the server mapper rejects an
    // empty answers object, so the advertised contract must not claim an
    // empty object is acceptable (schema-driven clients would otherwise hit
    // a server-side invalid_argument the published contract should have
    // prevented).
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ClarifyIntentArgumentSchema_RequiresAtLeastOneAnswer()
    {
        var schema = GroundingToolSchemas.ClarifyIntentArgumentSchema;
        var answers = schema
            .GetProperty("properties")
            .GetProperty("response")
            .GetProperty("properties")
            .GetProperty("answers");

        answers.TryGetProperty("minProperties", out var minProperties).Should().BeTrue(
            "schema-driven clients rely on the published schema to prevent empty answers payloads.");
        minProperties.ValueKind.Should().Be(JsonValueKind.Number);
        minProperties.GetInt32().Should().Be(1);
    }

    [UnitTest]
    public void ClarifyIntentArgumentSchema_RequiresNonEmptyAnswerStrings()
    {
        var schema = GroundingToolSchemas.ClarifyIntentArgumentSchema;
        var answerItems = schema
            .GetProperty("properties")
            .GetProperty("response")
            .GetProperty("properties")
            .GetProperty("answers")
            .GetProperty("additionalProperties")
            .GetProperty("items");

        answerItems.GetProperty("minLength").GetInt32().Should().Be(1);
    }

    [UnitTest]
    public void ClarifyIntentArgumentSchema_RequiresAtLeastOneValuePerAnswer()
    {
        // The server mapper rejects `{ "answers": { "q1": [] } }` with
        // invalid_argument; pin `minItems: 1` on the published schema so
        // schema-driven clients cannot generate a payload that fails at
        // runtime.
        var schema = GroundingToolSchemas.ClarifyIntentArgumentSchema;
        var answerArray = schema
            .GetProperty("properties")
            .GetProperty("response")
            .GetProperty("properties")
            .GetProperty("answers")
            .GetProperty("additionalProperties");

        answerArray.TryGetProperty("minItems", out var minItems).Should().BeTrue(
            "schema-driven clients rely on the published schema to prevent empty answer arrays.");
        minItems.ValueKind.Should().Be(JsonValueKind.Number);
        minItems.GetInt32().Should().Be(1);
    }

    [UnitTest]
    public void ToWire_WithClarificationEnvelope_CopiesQuestionsAndOptions()
    {
        var result = new GroundingResult
        {
            WorkflowFamily = new WorkflowFamilyClassification
            {
                Value = WorkflowFamily.Analyze,
                Confidence = 0.9
            },
            DraftIntent = new DraftIntent
            {
                IntentId = "i",
                Goal = "g",
                WorkflowFamily = WorkflowFamily.Analyze,
                Provenance = new ProvenanceRecord { Sources = [], ProcessDefinitions = [] }
            },
            Candidates = new CandidateRanking(),
            Clarification = new ClarificationRequest
            {
                IntentId = "i",
                ReasonCodes = [ClarificationReasonCode.AmbiguousDataset],
                Questions =
                [
                    new ClarificationQuestion
                    {
                        QuestionId = "dataset.selection",
                        Kind = ClarificationQuestionKind.SingleSelect,
                        Prompt = "Which dataset?",
                        Options =
                        [
                            new ClarificationOption { Id = "a", Label = "A" },
                            new ClarificationOption { Id = "b", Label = "B" }
                        ]
                    }
                ]
            },
            Engine = "deterministic"
        };

        var wire = GroundingToolMapper.ToWire(result);

        wire.Clarification.Should().NotBeNull();
        wire.Clarification!.ReasonCodes.Should().ContainSingle()
            .Which.Should().Be(nameof(ClarificationReasonCode.AmbiguousDataset));
        wire.Clarification.Questions.Should().ContainSingle()
            .Which.Options.Should().HaveCount(2);
    }
}
