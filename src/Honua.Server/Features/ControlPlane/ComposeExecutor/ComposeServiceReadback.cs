// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.ControlPlane;

internal sealed partial class ComposeProjectController
{
    private string[] ManagedServices() => [_options.ServiceName, _options.PostgresService, _options.RedisService];

    // Observation success describes collection, independently of the service's health. In
    // particular a stopped serving process must not prevent observation of its dependencies.
    private async Task<ComposeExecutorResponse> ObserveServicesAsync(
        ComposeExecutorRequest request, CancellationToken cancellationToken)
    {
        var ps = await ComposeAsync(CommandTimeout, ["ps", "--all", "--format", "json"], cancellationToken).ConfigureAwait(false);
        if (ps.ExitCode != 0)
            return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "Managed service observation is unavailable.");

        List<ComposePsRow> rows;
        try { rows = ParseComposePs(ps.Stdout).ToList(); }
        catch (JsonException)
        {
            return Refuse(request, ComposeExecutorCodes.ReadbackIncomplete, "Managed service observation is unavailable.");
        }

        var services = new List<ComposeServiceObservation>();
        string? imageId = null;
        string? runningImage = null;
        string? version = null;
        foreach (var service in ManagedServices().Distinct(StringComparer.Ordinal))
        {
            var matches = rows.Where(row => row.Service == service).ToArray();
            if (matches.Any(row => row.Project != _options.ProjectName))
                return Refuse(request, ComposeExecutorCodes.ScopeViolation, "A service row belongs to a different Compose project.");
            if (matches.Length == 0)
            {
                services.Add(new() { Service = service, Status = "Unhealthy", Description = "The managed service is absent." });
                continue;
            }

            // A single-project install has one container per configured service. Ambiguous
            // readback is never treated as healthy and cannot recommend a restart.
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Id))
            {
                services.Add(new() { Service = service, Status = "Unknown", Description = "The service container readback is incomplete." });
                continue;
            }

            var row = matches[0];
            var running = string.Equals(row.State, "running", StringComparison.OrdinalIgnoreCase);
            var start = await DockerAsync(TimeSpan.FromSeconds(5),
                ["inspect", "--format", "{{.State.StartedAt}}", row.Id], cancellationToken).ConfigureAwait(false);
            var incarnation = start.ExitCode == 0 && !string.IsNullOrWhiteSpace(start.Stdout)
                ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row.Id + ":" + start.Stdout.Trim()))) : null;
            bool ready = false;
            if (service == _options.ServiceName)
            {
                var image = await DockerAsync(CommandTimeout, ["inspect", "--format", "{{.Image}}", row.Id], cancellationToken).ConfigureAwait(false);
                imageId = image.ExitCode == 0 && ComposeExecutorRules.IsImmutableImage(image.Stdout.Trim()) ? image.Stdout.Trim() : null;
                if (running && imageId is not null)
                {
                    runningImage = imageId;
                    var digests = await DockerAsync(CommandTimeout, ["image", "inspect", "--format", "{{json .RepoDigests}}", imageId], cancellationToken).ConfigureAwait(false);
                    if (digests.ExitCode == 0)
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(digests.Stdout);
                            if (document.RootElement.ValueKind == JsonValueKind.Array)
                                runningImage = document.RootElement.EnumerateArray()
                                    .Where(item => item.ValueKind == JsonValueKind.String)
                                    .Select(item => item.GetString()).FirstOrDefault(ComposeExecutorRules.IsImmutableImage) ?? imageId;
                        }
                        catch (JsonException) { /* The immutable image id remains useful evidence. */ }
                    }
                    // A multi-architecture release digest differs from the container config id.
                    // Equality is established by Docker inspection, never by configuration alone.
                    var expected = await DockerAsync(CommandTimeout, ["image", "inspect", "--format", "{{.Id}}", _options.ReleaseImage], cancellationToken).ConfigureAwait(false);
                    if (expected.ExitCode == 0 && expected.Stdout.Trim() == imageId)
                        runningImage = _options.ReleaseImage;
                }
                if (running)
                {
                    ready = (await ProbeOkAsync(_options.HealthProbeUrl, false, cancellationToken).ConfigureAwait(false)).Ok;
                    version = await ReadServingVersionAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else if (running && service == _options.PostgresService)
            {
                // Query the managed database, rather than the manager's independent database.
                var result = await ComposeAsync(TimeSpan.FromSeconds(5),
                    ["exec", "-T", "--", service, "psql", "-U", _options.DatabaseUser, "-d", _options.DatabaseName,
                        "-tA", "-v", "ON_ERROR_STOP=1", "-c", "SELECT PostGIS_Version()"], cancellationToken).ConfigureAwait(false);
                ready = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout);
            }
            else if (running && service == _options.RedisService)
            {
                var ping = await ComposeAsync(TimeSpan.FromSeconds(5), ["exec", "-T", "--", service, "redis-cli", "ping"], cancellationToken).ConfigureAwait(false);
                ready = ping.ExitCode == 0 && ping.Stdout.Trim() == "PONG";
            }

            services.Add(new()
            {
                Service = service,
                Incarnation = incarnation,
                Status = ready ? "Healthy" : "Unhealthy",
                Description = !running ? "The managed service is stopped." : ready ? "The managed service is ready." : "The managed service is unready."
            });
        }

        return new ComposeExecutorResponse
        {
            RequestId = request.RequestId,
            Ok = true,
            ReadbackComplete = services.All(service => service.Status == "Healthy"),
            RunningImage = runningImage,
            ImageId = imageId,
            ServerVersion = version,
            Services = services,
            Message = "Managed service observation completed."
        };
    }

    private async Task<string?> ReadServingVersionAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri(_options.HealthProbeUrl), "/api/v1/admin/version");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(_options.VersionProbeApiKey))
            request.Headers.Add("X-API-Key", _options.VersionProbeApiKey);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("data", out var data)) root = data;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return root.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= 512 } version ? version : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
