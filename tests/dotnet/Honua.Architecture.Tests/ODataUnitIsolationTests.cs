// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Xml.Linq;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

public sealed class ODataUnitIsolationTests
{
    private static readonly string[] MovedClasses =
    [
        "ODataCloudNativeRegressionTests", "ODataCrsUtilitiesTests", "ODataFeatureProviderResolverTests",
        "ODataGeometryConverterTests", "ODataQueryParameterAdapterOrderByTests", "ODataSearchServiceSqlTests",
        "ODataApplyPipelineTests", "ODataComputeServiceTests", "ODataCrudServiceStorageLayerTests",
        "ODataEtagSerializationTests", "ODataPageSizeClampTests", "ODataSkipTokenServiceTests", "ODataSqlFragmentMergeHelperTests"
    ];

    [UnitTest]
    public void UnitProject_TransitiveReferencesExcludeHostFixturesAndProviders()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var project = Path.Combine(root, "tests", "dotnet", "Honua.Protocols.OData.UnitTests", "Honua.Protocols.OData.UnitTests.csproj");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Honua.Core.Abstractions", "Honua.Core", "Honua.Geometry", "Honua.ServiceDefaults",
            "Honua.Hosting", "Honua.Protocols.OData", "Honua.TestKit.Abstractions", "Honua.Protocols.OData.UnitTests"
        };
        var pending = new Stack<string>(); pending.Push(project);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            var name = Path.GetFileNameWithoutExtension(current);
            Assert.Contains(name, allowed);
            foreach (var reference in XDocument.Load(current).Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar);
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, include)));
            }
        }
        Assert.Contains(seen, path => Path.GetFileNameWithoutExtension(path) == "Honua.Protocols.OData");
        Assert.Contains(seen, path => Path.GetFileNameWithoutExtension(path) == "Honua.TestKit.Abstractions");
    }

    [UnitTest]
    public void MovedClasses_AreDiscoveredExactlyOnceInUnitAssembly()
    {
        var assemblies = ArchitectureTestHelpers.IntegrationTestAssemblies();
        Assert.Contains(assemblies, assembly => assembly.GetName().Name == "Honua.Protocols.OData.UnitTests");
        foreach (var name in MovedClasses)
        {
            var type = Assert.Single(assemblies.SelectMany(ArchitectureTestHelpers.GetTypesSafely), type => type.Name == name);
            Assert.Equal("Honua.Protocols.OData.UnitTests", type.Assembly.GetName().Name);
            var methods = type.GetMethods().Where(method => method.GetCustomAttributes<FactAttribute>().Any()).ToArray();
            Assert.NotEmpty(methods);
            Assert.All(methods, method => Assert.StartsWith("Honua.Protocols.OData.UnitTests:", TierTraitScanner.MethodKey(type, method), StringComparison.Ordinal));
        }
        var odataAssemblies = assemblies.Where(assembly => assembly.GetName().Name is "Honua.Protocols.OData.Tests" or "Honua.Protocols.OData.UnitTests");
        var discoveredTypes = odataAssemblies.SelectMany(ArchitectureTestHelpers.GetTypesSafely)
            .Where(type => type.GetMethods().Any(method => method.GetCustomAttributes<FactAttribute>().Any()));
        Assert.All(discoveredTypes.GroupBy(type => type.FullName), group => Assert.Single(group));
        var integration = Assert.Single(assemblies, assembly => assembly.GetName().Name == "Honua.Protocols.OData.Tests");
        Assert.NotNull(integration.GetType("Honua.Server.Tests.Features.Protocols.OData.ODataDeltaTests"));
    }

    [UnitTest]
    public void LightweightTraits_ResolveDiscovererWithoutFullTestKit()
    {
        Assert.Equal("Honua.TestKit.Abstractions", typeof(UnitTestAttribute).Assembly.GetName().Name);
        Assert.Same(typeof(UnitTestAttribute).Assembly, typeof(UnitTestDiscoverer).Assembly);
        var traits = new UnitTestDiscoverer().GetTraits(null!).ToArray();
        Assert.Contains(new KeyValuePair<string, string>("Tier", "Fast"), traits);
        Assert.Contains(new KeyValuePair<string, string>("Category", "Unit"), traits);
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "tests", "dotnet", "Honua.TestKit.Abstractions", "Honua.TestKit.Abstractions.csproj"));
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.All(project.Descendants("PackageReference"), reference => Assert.StartsWith("xunit", reference.Attribute("Include")!.Value, StringComparison.Ordinal));
    }
}
