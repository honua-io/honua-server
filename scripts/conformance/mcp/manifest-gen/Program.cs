// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// geospatial-mcp reference conformance-manifest generator.
//
// Runs CapabilityManifestEmitter (Honua.Ai) to project the live /mcp operator
// surface into the geospatial-mcp honua.manifest.json shape and writes it to a
// known path. The geospatial-mcp repo vendors the output as
// conformance/manifests/honua.manifest.json and scores it with
// conformance/check_manifest.py --strict.
//
// Usage: dotnet run --project scripts/conformance/mcp/manifest-gen -- <output-file>
// Default output: ./honua.manifest.json

using Honua.Ai.Protocols.Mcp.Discovery;

var outPath = args.Length > 0 ? args[0] : "honua.manifest.json";
var fullPath = Path.GetFullPath(outPath);

var directory = Path.GetDirectoryName(fullPath);
if (!string.IsNullOrEmpty(directory))
{
    Directory.CreateDirectory(directory);
}

var json = CapabilityManifestEmitter.EmitJson();
File.WriteAllText(fullPath, json);

Console.WriteLine($"geospatial-mcp manifest written to {fullPath}");
Console.WriteLine(
    $"  {CapabilityManifestEmitter.Tools.Count} tools, "
    + $"{CapabilityManifestEmitter.ResourcesByLiveFamily.Count} resource families "
    + $"(specDate {CapabilityManifestEmitter.SpecDate}).");
return 0;
