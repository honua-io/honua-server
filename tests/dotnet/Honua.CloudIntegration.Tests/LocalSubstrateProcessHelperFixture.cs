// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Compiles a tiny, dependency-free managed child binary the GP process-pool lane launches through
/// <see cref="Honua.ControlPlane.LocalProcessPoolBatchComputeBackend"/>. The binary self-selects its
/// behaviour from arguments (<c>--sleep N</c>, <c>--exit CODE</c>, <c>--env-out PATH</c>) so a single
/// portable executable covers the run-to-completion, non-zero-exit, cancellation, pool-saturation, and
/// <c>HONUA_*</c> environment-contract scenarios — with no shell and no OS-specific coreutil.
/// </summary>
/// <remarks>
/// The binary is invoked as <c>dotnet &lt;helper.dll&gt; …</c>. The <c>dotnet</c> muxer is resolved by
/// ABSOLUTE path (from <c>DOTNET_ROOT</c> or the running shared-framework directory) so the child spawn
/// bypasses any PATH shim and works identically in CI and locally. When the muxer cannot be resolved the
/// fixture reports <see cref="Available"/> = false and the dependent tests skip.
/// </remarks>
public sealed class LocalSubstrateProcessHelperFixture : IAsyncLifetime
{
    private string? _workDir;

    /// <summary>True when the helper compiled and a runnable <c>dotnet</c> muxer was resolved.</summary>
    public bool Available { get; private set; }

    /// <summary>Absolute path to the <c>dotnet</c> muxer used to launch the helper.</summary>
    public string DotnetMuxerPath { get; private set; } = string.Empty;

    /// <summary>Absolute path to the compiled helper assembly.</summary>
    public string HelperDllPath { get; private set; } = string.Empty;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        try
        {
            var muxer = ResolveDotnetMuxer();
            if (muxer is null)
            {
                Available = false;
                return Task.CompletedTask;
            }

            _workDir = Directory.CreateTempSubdirectory("honua-ls-gp").FullName;
            // "honua-envprobe.dll" is a fixed relative literal, so Path.Combine cannot drop _workDir.
            HelperDllPath = Path.Join(_workDir, "honua-envprobe.dll");
            EmitHelperAssembly(HelperDllPath);
            WriteRuntimeConfig(Path.ChangeExtension(HelperDllPath, ".runtimeconfig.json"));

            DotnetMuxerPath = muxer;
            Available = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Available = false;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        if (_workDir is not null && Directory.Exists(_workDir))
        {
            try
            {
                Directory.Delete(_workDir, recursive: true);
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Best-effort temp cleanup.
            }
        }

        return Task.CompletedTask;
    }

    private static void EmitHelperAssembly(string outputPath)
    {
        const string source =
            """
            using System;
            using System.Collections;
            using System.IO;
            using System.Threading;

            internal static class HonuaEnvProbe
            {
                private static int Main(string[] args)
                {
                    var sleepMs = 0;
                    var exitCode = 0;
                    string? envOut = null;

                    for (var i = 0; i < args.Length; i++)
                    {
                        switch (args[i])
                        {
                            case "--sleep":
                                sleepMs = int.Parse(args[++i]) * 1000;
                                break;
                            case "--exit":
                                exitCode = int.Parse(args[++i]);
                                break;
                            case "--env-out":
                                envOut = args[++i];
                                break;
                        }
                    }

                    if (envOut is not null)
                    {
                        using var writer = new StreamWriter(envOut, append: false);
                        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                        {
                            var key = (string)entry.Key;
                            if (key.StartsWith("HONUA_", StringComparison.Ordinal)
                                || key.StartsWith("CUSTOM_", StringComparison.Ordinal))
                            {
                                writer.WriteLine(key + "=" + entry.Value);
                            }
                        }
                    }

                    if (sleepMs > 0)
                    {
                        Thread.Sleep(sleepMs);
                    }

                    return exitCode;
                }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest));

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "honua-envprobe",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release));

        using var stream = File.Create(outputPath);
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            var errors = string.Join(
                "; ",
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
            throw new InvalidOperationException($"Failed to compile the GP process helper: {errors}");
        }
    }

    private static void WriteRuntimeConfig(string path)
    {
        var version = Environment.Version; // e.g. 10.0.0
        var runtimeConfig =
            $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{version.Major}}.{{version.Minor}}",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{version.Major}}.{{version.Minor}}.0"
                },
                "rollForward": "LatestMajor"
              }
            }
            """;
        File.WriteAllText(path, runtimeConfig);
    }

    private static string? ResolveDotnetMuxer()
    {
        var muxerName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            // muxerName is always the literal "dotnet"/"dotnet.exe", never rooted, so it cannot
            // cause Path.Combine to drop dotnetRoot.
            var fromRoot = Path.Join(dotnetRoot, muxerName);
            if (File.Exists(fromRoot))
            {
                return fromRoot;
            }
        }

        // GetRuntimeDirectory() -> <root>/shared/Microsoft.NETCore.App/<version>/ ; the muxer sits at <root>.
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var root = new DirectoryInfo(runtimeDir).Parent?.Parent?.Parent;
        if (root is not null)
        {
            // Same literal muxerName as above; Path.Combine cannot drop root.FullName here either.
            var fromRuntime = Path.Join(root.FullName, muxerName);
            if (File.Exists(fromRuntime))
            {
                return fromRuntime;
            }
        }

        return null;
    }
}
