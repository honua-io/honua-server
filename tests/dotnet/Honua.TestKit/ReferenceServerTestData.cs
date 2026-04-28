// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit;

internal static class ReferenceServerTestData
{
    internal const string TsunamiEvacuationZonesZipFileName = "Extreme_Tsunami_Evacuation_Zones.zip";

    internal static string ResolveTsunamiEvacuationZonesZipPath()
    {
        var outputDirectoryCandidate = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            TsunamiEvacuationZonesZipFileName);
        if (File.Exists(outputDirectoryCandidate))
        {
            return outputDirectoryCandidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory != null)
        {
            var repositoryCandidate = Path.Combine(
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
