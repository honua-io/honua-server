// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Seed;

[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.GetServiceInfo)]
public sealed class AdminSampleSeedFixtureTests
{
    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public void AdminSampleSeed_DeclaresPreviewableFeatureServerFixture()
    {
        var seed = File.ReadAllText(ResolveRepoFile("tests", "seed", "admin-sample-feature-server.yaml"));

        seed.Should().Contain("/rest/services/admin_sample/FeatureServer?f=json");
        seed.Should().Contain("/rest/services/admin_sample/FeatureServer/3000/query?f=geojson&where=1%3D1");
        seed.Should().Contain("/rest/services/admin_sample/FeatureServer/3001/query?f=geojson&where=1%3D1");
        seed.Should().Contain("/rest/services/admin_sample/FeatureServer/3002/query?f=geojson&where=1%3D1");
        seed.Should().Contain("'admin_sample'");
        seed.Should().Contain("3000");
        seed.Should().Contain("3001");
        seed.Should().Contain("3002");
        seed.Should().Contain("ST_MakeEnvelope(-158.05, 21.25, -157.65, 21.45, 4326)");
        seed.Should().Contain("Oahu Operations Sites");
        seed.Should().Contain("Oahu Response Routes");
        seed.Should().Contain("Oahu Service Areas");
        seed.Should().Contain("Honolulu Operations Center");
        seed.Should().Contain("Pearl City Sensor Gateway");
        seed.Should().Contain("Town to Airport Response Route");
        seed.Should().Contain("Urban Core Service Area");
        seed.Should().Contain("storage_srid");
        seed.Should().Contain("3857");
        Regex.Count(seed, "ST_SetSRID\\(ST_MakePoint").Should().BeGreaterThanOrEqualTo(4);
        Regex.Count(seed, "ST_Transform\\(").Should().BeGreaterThanOrEqualTo(3);
        Regex.Count(seed, "ST_GeomFromText\\('POLYGON").Should().BeGreaterThanOrEqualTo(2);
    }

    private static string ResolveRepoFile(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test should run under the repository output tree");
        return Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray());
    }
}
