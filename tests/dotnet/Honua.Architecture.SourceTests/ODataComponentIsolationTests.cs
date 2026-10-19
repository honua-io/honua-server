// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

public sealed class ODataComponentIsolationTests
{
    private static readonly string[] RuntimeProjects =
    [
        "Honua.Core", "Honua.Core.Abstractions", "Honua.Postgres", "Honua.Postgres.Shared",
        "Honua.Geometry", "Honua.Hosting", "Honua.Protocols.OData", "Honua.ServiceDefaults", "Honua.OData.TestHost"
    ];

    [UnitTest]
    public void MinimalHost_ReferencesOnlyODataRuntimeProjectsAndNoTestPackages()
    {
        var projects = ProjectClosure("Honua.OData.TestHost");
        Assert.All(projects, project => Assert.Contains(Path.GetFileNameWithoutExtension(project), RuntimeProjects));
        Assert.Contains(projects, project => Path.GetFileNameWithoutExtension(project) == "Honua.Postgres");
        Assert.Contains(projects, project => Path.GetFileNameWithoutExtension(project) == "Honua.Protocols.OData");
        foreach (var project in projects)
        {
            foreach (var package in XDocument.Load(project).Descendants("PackageReference"))
            {
                var name = package.Attribute("Include")!.Value;
                Assert.False(name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Testcontainers", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Microsoft.AspNetCore.TestHost", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Microsoft.AspNetCore.Mvc.Testing", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("FluentAssertions", StringComparison.OrdinalIgnoreCase),
                    $"Runtime project {project} must not reference test package {name}.");
            }
        }
    }

    [UnitTest]
    public void ComponentTests_ExcludeFullServerOtherProtocolsAndFullTestKit()
    {
        var allowed = RuntimeProjects.Concat(["Honua.Protocols.OData.ComponentTests", "Honua.TestKit.Postgres"]).ToArray();
        var projects = ProjectClosure("Honua.Protocols.OData.ComponentTests");
        Assert.All(projects, project => Assert.Contains(Path.GetFileNameWithoutExtension(project), allowed));
        Assert.Contains(projects, project => Path.GetFileNameWithoutExtension(project) == "Honua.OData.TestHost");
        Assert.Contains(projects, project => Path.GetFileNameWithoutExtension(project) == "Honua.TestKit.Postgres");
    }

    [UnitTest]
    public void Provisioning_LinksCanonicalManifestAndEveryCanonicalSqlResource()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var host = ProjectPath(root, "Honua.OData.TestHost");
        var manifest = Path.Combine(root, "src", "Honua.Server", "Startup", "ServerCoreSchemaMigrations.cs");
        var project = XDocument.Load(host);
        Assert.Single(project.Descendants("Compile"), item =>
            ResolveInclude(host, item.Attribute("Include")!.Value) == manifest && item.Attribute("Link") is not null);
        var migrations = Path.Combine(root, "src", "Honua.Server", "Migrations");
        var canonical = Directory.GetFiles(migrations, "*.sql").Select(Path.GetFileName).Order().ToArray();
        Assert.NotEmpty(canonical);
        foreach (var name in new[] { "Honua.OData.TestHost", "Honua.TestKit.Postgres" })
        {
            var path = ProjectPath(root, name);
            var resources = XDocument.Load(path).Descendants("EmbeddedResource").ToArray();
            var sql = resources.Where(item => item.Attribute("Include")!.Value.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).ToArray();
            var linked = Assert.Single(sql);
            Assert.Equal(Path.Combine(migrations, "*.sql"), ResolveInclude(path, linked.Attribute("Include")!.Value));
            Assert.Equal("Honua.Server.Migrations.%(Filename)%(Extension)", linked.Attribute("LogicalName")?.Value);
            Assert.Equal(canonical, Directory.GetFiles(Path.GetDirectoryName(ResolveInclude(path, linked.Attribute("Include")!.Value))!, "*.sql")
                .Select(Path.GetFileName).Order().ToArray());
            var directory = Path.GetDirectoryName(path)!;
            Assert.DoesNotContain(Directory.GetFiles(directory, "*.sql", SearchOption.AllDirectories), file =>
                !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
        }
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(host)!, "ServerCoreSchemaMigrations.cs", SearchOption.AllDirectories),
            file => !Path.GetRelativePath(Path.GetDirectoryName(host)!, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
    }

    private static HashSet<string> ProjectClosure(string name)
    {
        var pending = new Stack<string>();
        pending.Push(ProjectPath(ArchitectureTestHelpers.ResolveRepositoryRoot(), name));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            foreach (var reference in XDocument.Load(current).Descendants("ProjectReference"))
                pending.Push(ResolveInclude(current, reference.Attribute("Include")!.Value));
        }
        return seen;
    }

    private static string ProjectPath(string root, string name) => Path.Combine(root, "tests", "dotnet", name, name + ".csproj");

    private static string ResolveInclude(string project, string include) => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(project)!, include.Replace('\\', Path.DirectorySeparatorChar)));
}
