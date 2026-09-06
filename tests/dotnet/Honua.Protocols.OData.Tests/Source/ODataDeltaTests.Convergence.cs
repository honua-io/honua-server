// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed partial class ODataDeltaTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.ODataV4, "DeltaTracking")]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Delta_EqualTimestampsDeletesRecreateAndFilterTransitions_PageOneConverges()
    {
        await using var connection = new NpgsqlConnection(_fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        var schema = new NpgsqlCommandBuilder().QuoteIdentifier(_fixture.CurrentSchema!);
        await using (var seed = new NpgsqlCommand($$"""
            DELETE FROM {{schema}}.features WHERE layer_id = 0;
            INSERT INTO {{schema}}.features(objectid, layer_id, geometry, attributes, updated_at)
            VALUES
                (73001, 0, ST_SetSRID(ST_Point(1, 2), 4326), '{"name":"first"}', '2026-01-01'),
                (73002, 0, ST_SetSRID(ST_Point(2, 3), 4326), '{"name":"second"}', '2026-01-01'),
                (73003, 0, NULL, '{"name":"leaving"}', '2026-01-01'),
                (73004, 0, NULL, '{"name":"recreate"}', '2026-01-01'),
                (73005, 0, NULL, '{"name":"excluded"}', '2026-01-01'),
                (73006, 0, NULL, '{"name":"delete"}', '2026-01-01');
            """, connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        var state = new Dictionary<long, string>();
        async Task<string> FollowAsync(string link, bool baseline)
        {
            var pages = 0;
            while (true)
            {
                (++pages).Should().BeLessThan(20, "paging must terminate without unbounded duplicates");
                using var request = new HttpRequestMessage(HttpMethod.Get, Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.PathAndQuery : link);
                if (baseline) { request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes"); }
                using var response = await _fixture.Client.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var values = page.RootElement.GetProperty("value").EnumerateArray().ToArray();
                values.Length.Should().BeLessThanOrEqualTo(1);
                foreach (var value in values)
                {
                    var id = value.GetProperty("ObjectId").GetInt64();
                    if (value.TryGetProperty("@removed", out var removed))
                    {
                        removed.GetProperty("reason").GetString().Should().BeOneOf("deleted", "changed");
                        state.Remove(id);
                    }
                    else
                    {
                        state[id] = value.GetProperty("name").GetString()!;
                    }
                }

                if (page.RootElement.TryGetProperty("@odata.nextLink", out var next))
                {
                    link = next.GetString()!;
                    continue;
                }

                return page.RootElement.GetProperty("@odata.deltaLink").GetString()!;
            }
        }

        var delta = await FollowAsync("/odata/Features(0)?$filter=name%20ne%20'excluded'&$top=1", true);
        state.Should().BeEquivalentTo(new Dictionary<long, string>
        {
            [73001] = "first", [73002] = "second", [73003] = "leaving", [73004] = "recreate", [73006] = "delete"
        });

        // One frozen database timestamp, deliberately older than the baseline poll.
        // Change generations, not wall-clock precision, must drive delivery.
        await using (var mutate = new NpgsqlCommand($$"""
            UPDATE {{schema}}.features SET attributes = '{"name":"first-updated"}', updated_at = '2026-01-02' WHERE objectid = 73001;
            UPDATE {{schema}}.features SET attributes = '{"name":"second-updated"}', updated_at = '2026-01-02' WHERE objectid = 73002;
            UPDATE {{schema}}.features SET attributes = '{"name":"excluded"}', updated_at = '2026-01-02' WHERE objectid = 73003;
            DELETE FROM {{schema}}.features WHERE objectid IN (73004, 73006);
            INSERT INTO {{schema}}.features(objectid, layer_id, attributes, updated_at) VALUES (73004, 0, '{"name":"recreated"}', '2026-01-02');
            UPDATE {{schema}}.features SET attributes = '{"name":"entered"}', updated_at = '2026-01-02' WHERE objectid = 73005;
            """, connection))
        {
            await mutate.ExecuteNonQueryAsync();
        }

        var terminal = await FollowAsync(delta, false);
        var expected = new Dictionary<long, string>
        {
            [73001] = "first-updated", [73002] = "second-updated", [73004] = "recreated", [73005] = "entered"
        };
        state.Should().BeEquivalentTo(expected, "the independently specified mutation outcome must replace the baseline");
        _ = await FollowAsync(terminal, false);
        state.Should().BeEquivalentTo(expected, "terminal polling is idempotent");

        await _fixture.RestartHostAsync();
        _ = await FollowAsync(terminal, false);
        state.Should().BeEquivalentTo(expected, "the same durable terminal token survives a complete host and service-provider restart");
        await using (var afterRestart = new NpgsqlCommand($$"""
            UPDATE {{schema}}.features SET attributes = '{"name":"after-restart"}', updated_at = '2026-01-02'
            WHERE layer_id = 0 AND objectid = 73001;
            """, connection))
        {
            await afterRestart.ExecuteNonQueryAsync();
        }
        expected[73001] = "after-restart";
        var restartedTerminal = await FollowAsync(terminal, false);
        state.Should().BeEquivalentTo(expected, "a post-restart update at the same timestamp must converge without rebasing");
        _ = await FollowAsync(restartedTerminal, false);
        state.Should().BeEquivalentTo(expected);
    }
}
