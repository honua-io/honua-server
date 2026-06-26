// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Geoprocessing.Cli;

/// <summary>
/// Injects the one-line DI registration and the catalog <c>ProcessDefinition</c> for a
/// scaffolded process into the existing source files (GP Devkit P4, issue #2125). Keeping
/// these as anchored, idempotent text transforms — rather than editing by hand — is what
/// makes <c>honua gp new</c> a single command that yields a REGISTERED, runnable process.
/// All methods are pure string transforms so they are unit-testable offline.
/// </summary>
public static class GpScaffoldInjector
{
    /// <summary>
    /// Inserts a registration call into the auto-registration method body, immediately after
    /// the last existing call of the same shape (<c>Register&lt;...&gt;(services);</c> for the
    /// managed extension, <c>RegisterGdalExecutor&lt;...&gt;(services);</c> for the GDAL one).
    /// The insert is idempotent: if the exact call is already present the source is returned
    /// unchanged.
    /// </summary>
    /// <param name="source">The full text of the registration extension file.</param>
    /// <param name="registrationCall">
    /// The call to insert, e.g. <c>Register&lt;MyJobExecutor&gt;(services);</c>.
    /// </param>
    /// <param name="result">On success, the transformed source.</param>
    /// <param name="error">On failure, the reason the anchor could not be found.</param>
    /// <returns><see langword="true"/> when the call was inserted (or already present).</returns>
    public static bool TryInsertRegistration(
        string source,
        string registrationCall,
        out string result,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationCall);

        if (source.Contains(registrationCall, StringComparison.Ordinal))
        {
            result = source;
            error = string.Empty;
            return true;
        }

        // Anchor on the keyword the existing calls share. Both helpers emit the call as a
        // single statement line; we splice the new line in after the last matching one,
        // copying its indentation.
        var anchorKeyword = registrationCall.StartsWith("RegisterGdalExecutor", StringComparison.Ordinal)
            ? "RegisterGdalExecutor<"
            : "Register<";

        var lines = source.Split('\n');
        var lastIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(anchorKeyword, StringComparison.Ordinal)
                && lines[i].Contains("(services);", StringComparison.Ordinal))
            {
                lastIndex = i;
            }
        }

        if (lastIndex < 0)
        {
            result = source;
            error =
                $"could not find an existing '{anchorKeyword}...(services);' registration line to anchor to.";
            return false;
        }

        var indent = GetLeadingWhitespace(lines[lastIndex]);
        var inserted = new List<string>(lines);
        inserted.Insert(lastIndex + 1, indent + registrationCall);

        result = string.Join('\n', inserted);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Inserts a <c>ProcessDefinition</c> block into the catalog's <c>BuildDefinitions()</c>
    /// collection expression, immediately before the <c>];</c> that closes it. Idempotent on
    /// the process id: if a definition with that id is already present the source is returned
    /// unchanged.
    /// </summary>
    /// <param name="source">The full text of <c>BuiltInProcessCatalog.cs</c>.</param>
    /// <param name="processId">The new process id.</param>
    /// <param name="kind">Managed vs native, which sets the runtime profile + title hint.</param>
    /// <param name="result">On success, the transformed source.</param>
    /// <param name="error">On failure, the reason the anchor could not be found.</param>
    /// <returns><see langword="true"/> when the definition was inserted (or already present).</returns>
    public static bool TryInsertCatalogEntry(
        string source,
        string processId,
        GpProcessKind kind,
        out string result,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);

        if (source.Contains($"ProcessId = \"{processId}\"", StringComparison.Ordinal))
        {
            result = source;
            error = string.Empty;
            return true;
        }

        // Anchor on the FIRST "\n    ];" (4-space indent) that follows the BuildDefinitions()
        // signature — that line closes the collection expression the definitions live in.
        // Using LastIndexOf would wrongly match the shared ProcessParameterSpec[] arrays
        // declared after it, so we scope the search to start at BuildDefinitions().
        var buildStart = source.IndexOf("BuildDefinitions() =>", StringComparison.Ordinal);
        if (buildStart < 0)
        {
            result = source;
            error = "could not find the BuildDefinitions() method body in the catalog source.";
            return false;
        }

        const string closeMarker = "\n    ];";
        var closeIndex = source.IndexOf(closeMarker, buildStart, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            result = source;
            error = "could not find the closing '];' of BuildDefinitions() to anchor the catalog entry to.";
            return false;
        }

        var entry = RenderCatalogEntry(processId, kind);
        result = source.Insert(closeIndex, "\n" + entry);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Renders the <c>ProcessDefinition</c> block for the scaffolded process: a single
    /// optional <c>value</c> text input matching the scaffold executor's body, the right
    /// category/runtime-profile for the kind, and a TODO pointer.
    /// </summary>
    internal static string RenderCatalogEntry(string processId, GpProcessKind kind)
    {
        var stem = GpScaffolder.ToTypeStem(processId);
        var title = StemToTitle(stem);
        var category = processId.Contains('.', StringComparison.Ordinal)
            ? processId[..processId.IndexOf('.', StringComparison.Ordinal)]
            : processId;

        var sb = new StringBuilder();
        sb.Append("        // Scaffolded by `honua gp new` (GP Devkit P4). TODO: refine the title,\n");
        sb.Append("        // description, parameters, and output artifact kinds for the real process.\n");
        sb.Append("        new ProcessDefinition\n");
        sb.Append("        {\n");
        sb.Append($"            ProcessId = \"{processId}\",\n");
        sb.Append($"            Title = \"{title}\",\n");
        sb.Append($"            Description = \"Scaffolded {processId} process. Replace this description and the parameter schema with the real operation.\",\n");
        sb.Append($"            Category = \"{category}\",\n");
        sb.Append("            Parameters =\n");
        sb.Append("            [\n");
        sb.Append("                Param(\"value\", \"Value\", \"Scaffold echo input. Replace with the real typed inputs.\", ProcessParameterValueType.Text),\n");
        sb.Append("            ],\n");
        sb.Append("            OutputArtifactKinds = [ArtifactKind.FeatureLayer]");
        if (kind == GpProcessKind.Gdal)
        {
            sb.Append(",\n            RuntimeProfile = RuntimeProfiles.Native\n");
        }
        else
        {
            sb.Append('\n');
        }

        sb.Append("        },\n");
        return sb.ToString();
    }

    private static string StemToTitle(string stem)
    {
        // Insert spaces before interior capitals: "AnalyticsSpatialJoin" -> "Analytics Spatial Join".
        var sb = new StringBuilder(stem.Length + 4);
        for (var i = 0; i < stem.Length; i++)
        {
            if (i > 0 && char.IsUpper(stem[i]))
            {
                sb.Append(' ');
            }

            sb.Append(stem[i]);
        }

        return sb.ToString();
    }

    private static string GetLeadingWhitespace(string line)
    {
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return line[..count];
    }
}
