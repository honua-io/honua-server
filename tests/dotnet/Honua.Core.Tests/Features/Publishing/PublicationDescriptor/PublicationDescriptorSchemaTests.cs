// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Core.Tests.Features.Publishing.PublicationDescriptor;

public sealed class PublicationDescriptorSchemaTests
{
    private const string SchemaFailure = "schema";
    private static readonly string SchemaPath =
        RepositoryPaths.Resolve("docs", "gis", "schemas", "publication-descriptor.v1.schema.json");
    private static readonly string ExamplesDirectory =
        RepositoryPaths.Resolve("docs", "gis", "schemas", "publication-descriptor.v1");
    private static readonly string InvalidDirectory = Path.Join(ExamplesDirectory, "invalid");
    private static readonly Lazy<JSchema> Schema = new(() => JSchema.Parse(File.ReadAllText(SchemaPath)));

    public static TheoryData<string, string> InvalidExamples()
    {
        var data = new TheoryData<string, string>();
        foreach (var (file, expected) in ExpectedFailures())
        {
            data.Add(file, expected);
        }

        return data;
    }

    [UnitTest]
    public void MauiCountyParcelsExample_PassesSchemaAndRules()
    {
        var descriptor = Load(Path.Join(ExamplesDirectory, "maui-county-parcels.json"));

        descriptor.IsValid(Schema.Value, out IList<ValidationError> errors).Should().BeTrue(Describe(errors));
        PublicationDescriptorRules.Violations(descriptor).Should().BeEmpty();
    }

    [UnitTheory]
    [MemberData(nameof(InvalidExamples))]
    public void InvalidExample_FailsTheCheckItIsNamedFor(string file, string expectedFailure)
    {
        var descriptor = Load(Path.Join(InvalidDirectory, file));
        var schemaValid = descriptor.IsValid(Schema.Value, out IList<ValidationError> errors);

        if (expectedFailure == SchemaFailure)
        {
            schemaValid.Should().BeFalse($"{file} must be rejected by the schema");
            return;
        }

        schemaValid.Should().BeTrue($"{file} must pass the schema so only rule {expectedFailure} rejects it: {Describe(errors)}");
        PublicationDescriptorRules.Violations(descriptor).Should().Equal(expectedFailure);
    }

    [UnitTest]
    public void InvalidExamplesDirectory_ListsEveryExampleWithItsExpectedFailure()
    {
        var files = Directory.GetFiles(InvalidDirectory, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name != "expected.json");

        files.Should().BeEquivalentTo(ExpectedFailures().Keys);
        ExpectedFailures().Values.Should().OnlyContain(failure =>
            failure == SchemaFailure || PublicationDescriptorRules.All.Contains(failure));
    }

    [UnitTest]
    public void SchemaDescription_NamesEveryRuleTheValidatorEnforces()
    {
        var description = JObject.Parse(File.ReadAllText(SchemaPath)).Value<string>("description")!;

        PublicationDescriptorRules.All.Should().OnlyContain(rule => description.Contains($") {rule}:", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> ExpectedFailures() =>
        JObject.Parse(File.ReadAllText(Path.Join(InvalidDirectory, "expected.json")))
            .Properties()
            .ToDictionary(property => property.Name, property => property.Value.Value<string>()!, StringComparer.Ordinal);

    private static JObject Load(string path) => JObject.Parse(File.ReadAllText(path));

    private static string Describe(IEnumerable<ValidationError> errors) =>
        string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}"));
}
