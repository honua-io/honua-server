// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Prompts;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Pins the curated MCP prompt catalog (#1953): the advertised <c>prompts/list</c>
/// roster, the descriptor shape, and the <c>prompts/get</c> rendering/argument
/// validation contract.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpPromptCatalogTests
{
    private static readonly string[] CuratedPromptNames =
    {
        "site_selection_analysis",
        "hazard_assessment",
        "permit_review",
        "dashboard_scaffolding",
    };

    [UnitTest]
    public void List_AdvertisesTheCuratedRoster()
    {
        var result = McpPromptCatalog.List();

        result.Prompts.Select(p => p.Name)
            .Should().BeEquivalentTo(CuratedPromptNames);
    }

    [UnitTest]
    public void List_IsOrderedByNameForADeterministicSurface()
    {
        var names = McpPromptCatalog.List().Prompts.Select(p => p.Name).ToArray();

        names.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [UnitTest]
    public void List_EveryPrompt_HasTitleDescriptionAndArguments()
    {
        var prompts = McpPromptCatalog.List().Prompts;

        foreach (var prompt in prompts)
        {
            prompt.Title.Should().NotBeNullOrWhiteSpace($"'{prompt.Name}' must carry a display title");
            prompt.Description.Should().NotBeNullOrWhiteSpace($"'{prompt.Name}' must carry a description");
            prompt.Arguments.Should().NotBeEmpty($"'{prompt.Name}' must declare at least one argument");
            prompt.Arguments.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Name));
            prompt.Arguments.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Description));
        }
    }

    [UnitTest]
    public void Get_RendersAUserMessageWithSubstitutedArguments()
    {
        var result = McpPromptCatalog.Get("site_selection_analysis", new Dictionary<string, string>
        {
            ["objective"] = "a new fire station",
            ["studyArea"] = "Travis County",
            ["criteria"] = "near arterial roads",
        });

        result.Messages.Should().ContainSingle();
        var message = result.Messages[0];
        message.Role.Should().Be("user");
        message.Content.Type.Should().Be("text");
        message.Content.Text.Should().Contain("a new fire station");
        message.Content.Text.Should().Contain("Travis County");
        message.Content.Text.Should().Contain("near arterial roads");
        // The template must not leak unresolved placeholders.
        message.Content.Text.Should().NotContain("{objective}");
        message.Content.Text.Should().NotContain("{studyArea}");
        message.Content.Text.Should().NotContain("{criteria}");
    }

    [UnitTest]
    public void Get_OmittedOptionalArgument_RendersANeutralPlaceholderNotTheRawToken()
    {
        // criteria is optional on site_selection_analysis.
        var result = McpPromptCatalog.Get("site_selection_analysis", new Dictionary<string, string>
        {
            ["objective"] = "an EV charging hub",
            ["studyArea"] = "downtown",
        });

        var text = result.Messages[0].Content.Text;
        text.Should().NotContain("{criteria}");
        text.Should().Contain("(not specified)");
    }

    [UnitTest]
    public void Get_MissingRequiredArgument_ThrowsValidation()
    {
        var act = () => McpPromptCatalog.Get("hazard_assessment", new Dictionary<string, string>
        {
            // exposureLayer is required and omitted.
            ["hazard"] = "100-year flood",
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*exposureLayer*");
    }

    [UnitTest]
    public void Get_BlankRequiredArgument_ThrowsValidation()
    {
        var act = () => McpPromptCatalog.Get("permit_review", new Dictionary<string, string>
        {
            ["parcel"] = "   ",
            ["permitType"] = "ADU",
        });

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*parcel*");
    }

    [UnitTest]
    public void Get_UnknownPromptName_ThrowsValidation()
    {
        var act = () => McpPromptCatalog.Get("does_not_exist", null);

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*does_not_exist*");
    }

    [UnitTest]
    public void Get_DescriptorArguments_MatchTheRequiredArgumentsEnforcedAtRender()
    {
        // Every argument flagged required in prompts/list must actually be
        // enforced by prompts/get, so a schema-driven client that supplies all
        // required arguments never hits a validation error.
        foreach (var descriptor in McpPromptCatalog.List().Prompts)
        {
            var allRequired = descriptor.Arguments
                .Where(a => a.Required)
                .ToDictionary(a => a.Name, _ => "value", StringComparer.Ordinal);

            var get = () => McpPromptCatalog.Get(descriptor.Name, allRequired);
            get.Should().NotThrow($"'{descriptor.Name}' renders once every required argument is supplied");

            foreach (var required in descriptor.Arguments.Where(a => a.Required))
            {
                var missing = allRequired
                    .Where(kvp => !string.Equals(kvp.Key, required.Name, StringComparison.Ordinal))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

                var act = () => McpPromptCatalog.Get(descriptor.Name, missing);
                act.Should().Throw<GeoprocessingValidationException>(
                    $"'{descriptor.Name}' must require '{required.Name}'");
            }
        }
    }
}
