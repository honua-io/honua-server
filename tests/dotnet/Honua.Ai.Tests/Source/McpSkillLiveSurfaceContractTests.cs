// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpTaxonomyAlignmentTests
{
    private static string SkillContractRoot => Path.Join(SchemaRoot, "skills");

    [UnitTest]
    public void SkillLiveSurfaceContract_MatchesProductionToolDescriptors()
    {
        using var catalogDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Join(SkillContractRoot, "catalog.json")));
        using var contractDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Join(SkillContractRoot, "contracts", "live-surface.json")));

        var declaredToolsBySkill = catalogDocument.RootElement
            .GetProperty("skills")
            .EnumerateArray()
            .ToDictionary(
                skill => skill.GetProperty("name").GetString()!,
                skill => skill.GetProperty("standardTools").EnumerateArray()
                    .Select(tool => tool.GetString()!)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var assertions = contractDocument.RootElement.GetProperty("assertions").EnumerateArray().ToArray();

        foreach (var (skill, declaredTools) in declaredToolsBySkill)
        {
            assertions
                .Where(assertion => assertion.GetProperty("skill").GetString() == skill)
                .Select(assertion => assertion.GetProperty("standardTool").GetString())
                .Should().BeEquivalentTo(declaredTools,
                    $"every tool declared by skill '{skill}' must have a live-surface assertion");
        }

        var liveTools = BuildTools().ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var liveNamesByStandardName = ImplementedToolStandardNames
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Key).ToArray(),
                StringComparer.Ordinal);

        foreach (var assertion in assertions)
        {
            var skill = assertion.GetProperty("skill").GetString()!;
            var standardName = assertion.GetProperty("standardTool").GetString()!;
            declaredToolsBySkill.Should().ContainKey(skill);
            declaredToolsBySkill[skill].Should().Contain(standardName);

            if (!liveNamesByStandardName.TryGetValue(standardName, out var liveNames))
            {
                KnownGapStandardTools.Should().Contain(standardName,
                    $"skill tool '{standardName}' must be live or an explicit standard gap");
                if (assertion.TryGetProperty("fallbackTool", out var fallbackTool))
                {
                    var fallbackName = fallbackTool.GetString()!;
                    liveNamesByStandardName.Should().ContainKey(fallbackName,
                        $"fallback '{fallbackName}' for '{standardName}' must resolve to a live production tool");
                }

                continue;
            }

            liveNames.Should().ContainSingle(
                $"skill tool '{standardName}' must resolve unambiguously to one production descriptor");
            liveTools.Should().ContainKey(liveNames[0]);
            var descriptor = liveTools[liveNames[0]].Describe();
            using var liveSchemaDocument = JsonDocument.Parse(JsonSerializer.Serialize(descriptor.InputSchema));
            var liveSchema = liveSchemaDocument.RootElement;
            var liveProperties = liveSchema.GetProperty("properties");
            var liveRequired = liveSchema.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(field => field.GetString()!).ToArray()
                : [];
            var expectedRequired = assertion.GetProperty("requiredFields")
                .EnumerateArray().Select(field => field.GetString()!).ToArray();

            liveRequired.Should().BeEquivalentTo(expectedRequired,
                $"live required fields for '{liveNames[0]}' must match skill '{skill}'");

            var assertedProperties = expectedRequired
                .Concat(assertion.GetProperty("requiredProperties")
                    .EnumerateArray().Select(property => property.GetString()!))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            using var standardSchemaDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Join(SchemaRoot, "tools", standardName + ".schema.json")));
            var standardProperties = standardSchemaDocument.RootElement.GetProperty("properties");

            foreach (var propertyName in assertedProperties)
            {
                liveProperties.TryGetProperty(propertyName, out var liveProperty).Should().BeTrue(
                    $"skill '{skill}' references '{standardName}.{propertyName}', which '{liveNames[0]}' must advertise");

                var standardProperty = standardProperties.GetProperty(propertyName);
                if (standardProperty.TryGetProperty("enum", out var standardEnum))
                {
                    liveProperty.TryGetProperty("enum", out var liveEnum).Should().BeTrue(
                        $"live enum path '{liveNames[0]}.{propertyName}' must remain advertised");
                    liveEnum.EnumerateArray().Select(value => value.GetRawText()).Should().BeEquivalentTo(
                        standardEnum.EnumerateArray().Select(value => value.GetRawText()),
                        $"live enum values for '{liveNames[0]}.{propertyName}' must match the canonical schema");
                }
            }

            if (assertion.TryGetProperty("forbiddenProperties", out var forbiddenProperties))
            {
                foreach (var forbiddenProperty in forbiddenProperties.EnumerateArray())
                {
                    liveProperties.TryGetProperty(forbiddenProperty.GetString()!, out _).Should().BeFalse(
                        $"skill '{skill}' forbids non-canonical argument '{standardName}.{forbiddenProperty.GetString()}'");
                }
            }
        }
    }
}
