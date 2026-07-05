// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Compiles an in-memory assembly whose embedded <c>.sql</c> manifest resources are DbUp migration
/// scripts. This lets the ADR-0060 migration-gate lane (#2166, #2457) drive the REAL
/// <c>PostgresDatabaseMigrationRunner</c> — which reads scripts via
/// <c>WithScriptsEmbeddedInAssembly</c> — over a controlled, per-scenario expand/contract script set
/// without checking synthetic SQL into the shipped migrations directory.
/// </summary>
internal static class SyntheticMigrationsCompiler
{
    /// <summary>
    /// Emits an assembly embedding each supplied script as a <c>.sql</c> resource. Resource names are
    /// prefixed with the assembly name and preserve the supplied script names, so DbUp discovers and
    /// orders them exactly as it would shipped migrations (ascending by name).
    /// </summary>
    /// <param name="assemblyName">Unique assembly name (also the manifest-resource prefix).</param>
    /// <param name="scripts">Ordered <c>(scriptName, sql)</c> pairs; each name must end in <c>.sql</c>.</param>
    /// <returns>The loaded assembly, ready to hand to the migration runner.</returns>
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
