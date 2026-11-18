// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO;
using System.Text.Json.Nodes;
using Honua.Core.Features.Exports;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Core.Tests.Features.Publishing.ExportCollection;

public sealed class ExportCollectionSchemaTests
{
    private const string SchemaFailure = "schema";
    private static readonly string SchemaPath =
        RepositoryPaths.Resolve("docs", "gis", "schemas", "stac-honua.v1.0.0.schema.json");
    private static readonly string ExamplesDirectory =
        RepositoryPaths.Resolve("docs", "gis", "schemas", "export-collection.v1");
    private static readonly string InvalidDirectory = Path.Join(ExamplesDirectory, "invalid");
    private static readonly string PortolanSchemaPath = RepositoryPaths.Resolve(
        "tests", "dotnet", "Honua.Core.Tests", "Features", "Publishing", "ExportCollection", "portolan-v0.3.0.schema.json");
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
        var collection = Load(Path.Join(ExamplesDirectory, "maui-county-parcels.json"));

        collection.IsValid(Schema.Value, out IList<ValidationError> errors).Should().BeTrue(Describe(errors));
        ExportVersionRules.Violations(Read(collection)).Should().BeEmpty();
    }

    [UnitTheory]
    [MemberData(nameof(InvalidExamples))]
    public void InvalidExample_FailsTheCheckItIsNamedFor(string file, string expectedFailure)
    {
        var collection = Load(Path.Join(InvalidDirectory, file));
        var schemaValid = collection.IsValid(Schema.Value, out IList<ValidationError> errors);

        if (expectedFailure == SchemaFailure)
        {
            schemaValid.Should().BeFalse($"{file} must be rejected by the schema");
            return;
        }

        schemaValid.Should().BeTrue($"{file} must pass the schema so only rule {expectedFailure} rejects it: {Describe(errors)}");
        ExportVersionRules.Violations(Read(collection)).Should().Equal(expectedFailure);
    }

    [UnitTest]
    public void WrittenCollection_RoundTripsTheMauiExample()
    {
        var text = File.ReadAllText(Path.Join(ExamplesDirectory, "maui-county-parcels.json"));
        var example = JsonNode.Parse(text)!.AsObject();
        var export = ExportCollectionDocument.Read(example)!;

        var written = ExportCollectionDocument.Write(export, new ExportCollectionMetadata(
            example["description"]!.GetValue<string>(), example["license"]!.GetValue<string>()));

        JsonNode.DeepEquals(written, example).Should().BeTrue(written.ToJsonString());
        JObject.Parse(written.ToJsonString()).IsValid(Schema.Value, out IList<ValidationError> errors).Should().BeTrue(Describe(errors));
    }

    [UnitTest]
    public void WrittenCollection_LeavesNonPublicFieldsOut()
    {
        var export = ExportCollectionDocument.Read(JsonNode.Parse(
            File.ReadAllText(Path.Join(ExamplesDirectory, "maui-county-parcels.json")))!.AsObject())!;
        var withHidden = export with { Fields = [.. export.Fields, new ExportVersionField("landvalue", "integer", Public: false)] };

        var written = ExportCollectionDocument.Write(withHidden, ExportCollectionMetadata.None);

        written.ToJsonString().Should().NotContain("landvalue");
        written["license"]!.GetValue<string>().Should().Be("other");
    }

    [UnitTheory]
    [InlineData("CC0-1.0", "CC0-1.0")]
    [InlineData("proprietary", "other")]
    [InlineData("CC BY 4.0", "other")]
    [InlineData(null, "other")]
    public void WrittenCollection_LicenseIsAnSpdxIdentifierOrOther(string? datasetLicense, string expected)
    {
        var written = ExportCollectionDocument.Write(Example(), new ExportCollectionMetadata(null, datasetLicense));

        written["license"]!.GetValue<string>().Should().Be(expected);
    }

    // The server adds the README.md and AGENTS.md links and the providers (PairedPublicationDescriptorFactory.Collection);
    // it does not write a host contact yet, so the example supplies one. With them the document is Portolan-valid.
    [UnitTest]
    public void MauiCountyParcelsExample_PassesThePinnedPortolanProfileOnceCompanionsAreAdded()
    {
        var portolan = JSchema.Parse(File.ReadAllText(PortolanSchemaPath));
        var collection = Load(Path.Join(ExamplesDirectory, "maui-county-parcels.json"));
        collection.IsValid(portolan).Should().BeFalse("the companions are not written yet");

        ((JArray)collection["links"]!).Add(JObject.Parse("""{"rel":"agents","href":"AGENTS.md","type":"text/markdown"}"""));
        ((JArray)collection["links"]!).Add(JObject.Parse("""{"rel":"describedby","href":"README.md","type":"text/markdown"}"""));
        collection["providers"] = JArray.Parse(
            """[{"name":"County of Maui","roles":["producer"]},{"name":"Honua","roles":["host"],"url":"https://example.org"}]""");

        collection.IsValid(portolan, out IList<ValidationError> errors).Should().BeTrue(Describe(errors));
        portolan.Id!.GetLeftPart(UriPartial.Path).Should().Be(ExportCollectionDocument.PortolanSchemaUri);
    }

    [UnitTest]
    public void Read_ThirdPartyCollectionWithoutHonuaFacts_IsNotAnExportVersion()
    {
        var collection = JsonNode.Parse(File.ReadAllText(Path.Join(ExamplesDirectory, "maui-county-parcels.json")))!.AsObject();
        foreach (var property in collection.Select(pair => pair.Key).Where(key => key.StartsWith("honua:", StringComparison.Ordinal)).ToList())
        {
            collection.Remove(property);
        }

        ExportCollectionDocument.Read(collection).Should().BeNull();
    }

    [UnitTest]
    public void InvalidExamplesDirectory_ListsEveryExampleWithItsExpectedFailure()
    {
        var files = Directory.GetFiles(InvalidDirectory, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name != "expected.json");

        files.Should().BeEquivalentTo(ExpectedFailures().Keys);
        ExpectedFailures().Values.Should().OnlyContain(failure =>
            failure == SchemaFailure || ExportVersionRules.All.Contains(failure));
    }

    [UnitTest]
    public void SchemaDescription_NamesEveryRuleTheValidatorEnforces()
    {
        var description = JObject.Parse(File.ReadAllText(SchemaPath)).Value<string>("description")!;

        ExportVersionRules.All.Should().OnlyContain(rule => description.Contains($") {rule}:", StringComparison.Ordinal));
    }

    [UnitTest]
    public void SchemaId_IsTheExtensionUriTheWriterDeclares()
    {
        JObject.Parse(File.ReadAllText(SchemaPath)).Value<string>("$id").Should().Be(ExportCollectionDocument.HonuaSchemaUri);
    }

    private static Dictionary<string, string> ExpectedFailures() =>
        JObject.Parse(File.ReadAllText(Path.Join(InvalidDirectory, "expected.json")))
            .Properties()
            .ToDictionary(property => property.Name, property => property.Value.Value<string>()!, StringComparer.Ordinal);

    private static ExportVersion Example()
        => ExportCollectionDocument.Read(JsonNode.Parse(File.ReadAllText(Path.Join(ExamplesDirectory, "maui-county-parcels.json")))!.AsObject())!;

    private static JObject Load(string path) => JObject.Parse(File.ReadAllText(path));

    private static ExportVersion Read(JObject collection)
        => ExportCollectionDocument.Read(JsonNode.Parse(collection.ToString())!.AsObject())
           ?? throw new InvalidOperationException("The example is not a readable Export version.");

    private static string Describe(IEnumerable<ValidationError> errors) =>
        string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}"));
}
