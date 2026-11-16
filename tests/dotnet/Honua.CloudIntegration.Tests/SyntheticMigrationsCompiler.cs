// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Honua.CloudIntegration.Tests;

internal static class SyntheticMigrationsCompiler
{
    public static Assembly Compile(string assemblyName, params (string ScriptName, string Sql)[] scripts)
    {
        // A trivial marker type keeps the emitted PE well-formed; only the embedded resources matter.
        var syntaxTree = CSharpSyntaxTree.ParseText(
            "namespace Honua.SyntheticMigrations { internal static class Marker { } }");

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var resources = scripts
            .Select(script =>
            {
                var bytes = Encoding.UTF8.GetBytes(script.Sql);
                return new ResourceDescription(
                    $"{assemblyName}.{script.ScriptName}",
                    () => new MemoryStream(bytes),
                    isPublic: true);
            })
            .ToArray();

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream, manifestResources: resources);
        if (!result.Success)
        {
            var errors = string.Join(
                "; ",
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
            throw new InvalidOperationException($"Failed to compile the synthetic migrations assembly: {errors}");
        }

        return Assembly.Load(peStream.ToArray());
    }
}
