// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// The embedded release/component-versions.json declaration and the startup check that refuses a
/// declaration differing from the contract versions the server serves (honua-server#5378).
/// </summary>
[Trait("Component", "Admin")]
public sealed class ComponentContractVersionsTests
{
    private const string CurrentDeclaration = """
        {
          "format": "honua.component-versions/v1",
          "component": "honua-server",
          "contractVersions": {
            "admin": "v1",
            "metadata": "metadata.honua.io/v2alpha1",
            "grpc": "v1",
            "geoservices": "2.0.0",
            "ogc": "1.0.0",
            "stac": "1.0.0"
          },
          "schemaVersions": {}
        }
        """;

    [UnitTest]
    public void EmbeddedDeclaration_DeclaresTheSixServedContracts()
    {
        var declared = ComponentVersionsDeclaration.Embedded.ContractVersions;

        declared.Keys.Should().BeEquivalentTo(new[] { "admin", "metadata", "geoservices", "ogc", "stac", "grpc" });
        declared.Should().Equal(ServedContractVersions.Current);
        ComponentVersionsDeclaration.Embedded.SchemaVersions.Should().NotContainKey("database");
    }

    [UnitTest]
    public void StartupCheck_AcceptsTheEmbeddedDeclaration()
    {
        var act = () => ContractVersionsStartupCheck.Validate(
            ComponentVersionsDeclaration.Embedded.ContractVersions,
            ServedContractVersions.Current);

        act.Should().NotThrow();
    }

    [UnitTest]
    public void ServedContractVersions_ReadTheConstantsEachSurfaceOwns()
    {
        var served = ServedContractVersions.Current;

        served["admin"].Should().Be("v1");
        served["metadata"].Should().Be(Honua.Core.Features.Metadata.Domain.V2.MetadataV2Constants.ApiVersion);
        served["geoservices"].Should().Be(Honua.Protocols.GeoServices.GeoServicesContract.Version);
        served["ogc"].Should().Be(Honua.Protocols.Ogc.Common.OgcContract.Version);
        served["stac"].Should().Be(Honua.Protocols.Stac.Models.StacContract.Version);
        served["grpc"].Should().Be("v1");
    }

    [UnitTest]
    public void StartupCheck_RefusesAStaleDeclaredVersion()
    {
        var stale = ComponentVersionsDeclaration.Parse(
            CurrentDeclaration.Replace("\"geoservices\": \"2.0.0\"", "\"geoservices\": \"0.9.0\"", StringComparison.Ordinal));

        var act = () => ContractVersionsStartupCheck.Validate(stale.ContractVersions, ServedContractVersions.Current);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'geoservices' is declared as '0.9.0' but served as '2.0.0'*");
    }

    [UnitTest]
    public void StartupCheck_RefusesADeclarationMissingAServedContract()
    {
        var missing = ComponentVersionsDeclaration.Parse(
            CurrentDeclaration.Replace("\"grpc\": \"v1\",", string.Empty, StringComparison.Ordinal));

        var act = () => ContractVersionsStartupCheck.Validate(missing.ContractVersions, ServedContractVersions.Current);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'grpc' is served as 'v1' but is not declared*");
    }

    [UnitTest]
    public void StartupCheck_RefusesADeclaredContractTheServerDoesNotServe()
    {
        var extra = ComponentVersionsDeclaration.Parse(
            CurrentDeclaration.Replace("\"stac\": \"1.0.0\"", "\"stac\": \"1.0.0\", \"odata\": \"4.01\"", StringComparison.Ordinal));

        var act = () => ContractVersionsStartupCheck.Validate(extra.ContractVersions, ServedContractVersions.Current);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'odata' is declared as '4.01' but this server does not serve it*");
    }

    [UnitTest]
    public void Parse_ReadsContractAndSchemaVersions()
    {
        var declaration = ComponentVersionsDeclaration.Parse(
            CurrentDeclaration.Replace("\"schemaVersions\": {}", "\"schemaVersions\": {\"diagnostic-bundle\": \"1.0\"}", StringComparison.Ordinal));

        declaration.ContractVersions.Should().HaveCount(6).And.ContainKey("grpc").WhoseValue.Should().Be("v1");
        declaration.SchemaVersions.Should().Equal(new Dictionary<string, string> { ["diagnostic-bundle"] = "1.0" });
    }

    [UnitTest]
    public void Parse_RefusesTheShapesTheReleaseResolverRefuses()
    {
        string[] invalid =
        [
            CurrentDeclaration.Replace("honua.component-versions/v1", "honua.component-versions/v2", StringComparison.Ordinal),
            CurrentDeclaration.Replace("\"honua-server\"", "\"honua-console\"", StringComparison.Ordinal),
            CurrentDeclaration.Replace("\"schemaVersions\": {}", "\"schemaVersions\": {\"database\": \"109\"}", StringComparison.Ordinal),
            CurrentDeclaration.Replace("\"schemaVersions\": {}", "\"schemaVersions\": {}, \"notes\": \"x\"", StringComparison.Ordinal),
            CurrentDeclaration.Replace(",\n  \"schemaVersions\": {}", string.Empty, StringComparison.Ordinal),
            CurrentDeclaration.Replace("\"ogc\": \"1.0.0\"", "\"ogc\": 1", StringComparison.Ordinal),
            CurrentDeclaration.Replace("\"ogc\": \"1.0.0\"", "\"ogc\": \" 1.0.0\"", StringComparison.Ordinal),
            CurrentDeclaration.Replace("\"ogc\": \"1.0.0\"", "\"ogc\": \"1.0.0\", \"ogc\": \"2.0.0\"", StringComparison.Ordinal),
            CurrentDeclaration.Replace("\"component\": \"honua-server\",", "\"component\": \"honua-server\", \"component\": \"honua-server\",", StringComparison.Ordinal),
        ];

        foreach (var json in invalid)
        {
            json.Should().NotBe(CurrentDeclaration);
            var act = () => ComponentVersionsDeclaration.Parse(json);
            act.Should().Throw<InvalidOperationException>(json)
                .WithMessage("release/component-versions.json is invalid: *");
        }
    }

    [UnitTest]
    public void GrpcPackageMajor_RefusesServicesFromMoreThanOnePackage()
    {
        var act = () => ServedContractVersions.GrpcPackageMajor(
            [Geospatial.V1.FeatureService.Descriptor, Grpc.Health.V1.Health.Descriptor]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*span 2 proto packages*");
    }
}
