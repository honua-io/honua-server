// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;

namespace Honua.CloudIntegration.Tests;

public sealed partial class CandidateTelemetryGateCertificationTests
{
    private sealed partial class CandidateGateEnvironment
    {
        private const string PreviousFeatureMarker = "candidate-gate-previous-feature";
        private const string FunctionalQueryPath = "/rest/services/candidate_gate/FeatureServer/7650/query?where=1%3D1&outFields=*&f=json";

        public async Task AssertPreviousFeatureQueryAsync()
        {
            using var client = new HttpClient { BaseAddress = new Uri(ProxyBaseUrl), Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.GetAsync(FunctionalQueryPath);
            var body = await response.Content.ReadAsStringAsync();
            ((int)response.StatusCode).Should().Be(200, body);
            using var json = JsonDocument.Parse(body);
            json.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
            var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
            features.Should().ContainSingle();
            features[0].GetProperty("attributes").GetProperty("name").GetString().Should().Be(PreviousFeatureMarker);
        }

        private async Task WriteReplicaDiagnosticsAsync()
        {
            foreach (var port in new[] { _activePort, _standbyPort })
            {
                var name = $"{_containerPrefix}-{port.ToString(CultureInfo.InvariantCulture)}";
                var (_, state, stateError) = await Docker.RunAsync(["inspect", "--format", "{{json .State}}", name]);
                var (_, logs, logError) = await Docker.RunAsync(["logs", "--tail", "80", name]);
                _output.WriteLine($"Replica {name} state: {state} {stateError}\n{logs}\n{logError}");
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    using var response = await client.GetAsync(ReplicaUrl(port, "/healthz/ready"));
                    _output.WriteLine($"Replica {name} readiness: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    _output.WriteLine($"Replica {name} readiness unavailable: {exception.GetType().Name}");
                }
            }
        }

        private static Task SeedPreviousFeatureAsync(string postgres)
            => Docker.RunCheckedAsync([
                "exec", postgres, "psql", "-U", "honua", "-d", "honua", "-v", "ON_ERROR_STOP=1", "-q", "-c",
                $$"""
                INSERT INTO honua.services (service_name, description, srid, supported_formats, capabilities, metadata)
                VALUES ('candidate_gate', 'Rollback feature proof', 4326, ARRAY['JSON'], ARRAY['Query'],
                        '{"accessPolicy":{"allowAnonymous":true}}');
                INSERT INTO honua.layers (layer_id, layer_name, table_schema, table_name, geometry_type, srid, metadata)
                VALUES (7650, 'Rollback feature', 'public', 'features', 'Point', 4326,
                        '{"accessPolicy":{"allowAnonymous":true}}');
                INSERT INTO honua.layer_fields (layer_id, field_name, field_type, field_order, nullable)
                VALUES (7650, 'objectid', 'Integer', 0, false), (7650, 'name', 'String', 1, true),
                       (7650, 'shape', 'Geometry', 2, true);
                INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
                VALUES ('candidate_gate', 7650, 0);
                INSERT INTO public.features (objectid, layer_id, geometry, attributes)
                VALUES (7650, 7650, ST_SetSRID(ST_Point(1, 2), 4326),
                        jsonb_build_object('objectid', 7650, 'name', '{{PreviousFeatureMarker}}'));
                """
            ]);
    }
}
