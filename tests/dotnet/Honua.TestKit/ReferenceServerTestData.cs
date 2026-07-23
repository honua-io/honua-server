// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit;

internal static class ReferenceServerTestData
{
    internal const string TsunamiEvacuationZonesZipFileName = "Extreme_Tsunami_Evacuation_Zones.zip";

    // All Path.Combine calls below join a directory (AppContext.BaseDirectory or a
    // discovered repo root) with fixed literal segments, so none can drop an earlier
    // argument by unexpectedly resolving as rooted.
    internal static string ResolveTsunamiEvacuationZonesZipPath()
    {
        var outputDirectoryCandidate = Path.Join(
            AppContext.BaseDirectory,
            "TestData",
            TsunamiEvacuationZonesZipFileName);
        if (File.Exists(outputDirectoryCandidate))
        {
            return outputDirectoryCandidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory != null)
        {
            var repositoryCandidate = Path.Join(
                directory.FullName,
                "tests",
                "dotnet",
                "Honua.Server.Tests",
                "TestData",
                TsunamiEvacuationZonesZipFileName);

            if (File.Exists(repositoryCandidate))
            {
                return repositoryCandidate;
            }
        }

        throw new FileNotFoundException(
            $"Unable to locate '{TsunamiEvacuationZonesZipFileName}' for reference server fixture seeding.",
            TsunamiEvacuationZonesZipFileName);
    }
}
