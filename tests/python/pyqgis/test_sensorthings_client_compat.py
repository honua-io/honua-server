# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""OGC SensorThings 1.1 compatibility exercised through the real QGIS provider.

QGIS 3.44.14 ships a ``sensorthings`` data provider whose URI is
``url='<service root>' entity='<EntityType>'``, optionally with
``expandTo='<ChildEntity>'``. That is the whole client surface, so these cases
drive it directly rather than asserting over HTTP.

The service root is ``/sta/v1.1``. That is worth stating plainly, because it is
not guessable and has already cost twice: honua-server#4202 was filed as "service
root returns 404" against ``/sensorthings``, and the same wrong guess was made
again during the certification audit. The spec leaves the root prefix to the
deployment, and the common implementations serve a bare ``/v1.1``. The server now
also answers ``/sensorthings/v1.1`` with a 308 onto the canonical root, so either
spelling reaches it; these cases use the canonical one.

The surface is gated: ``serve.sensorthings`` is an experimental capability, and
the client-compat fixture enables it explicitly. With it off every path 404s, and
a lane run would report this protocol as unreachable rather than failing - which
is why the entity-set case asserts the root document first.
"""

from __future__ import annotations

import json
import urllib.parse
import urllib.request

import pytest

from .conftest import CertificationEvidenceCollector

SERVICE_ROOT_PATH = "/sta/v1.1"

# Seeded by the fixture: one station, one thermometer, one datastream and the
# observation series across it.
EXPECTED_COUNTS = {
    "Thing": 1,
    "Sensor": 1,
    "ObservedProperty": 1,
    "Datastream": 1,
    "Observation": 48,
}

# Advertised entity sets, keyed by the singular entity name the provider takes.
ENTITY_SET_NAMES = {
    "Thing": "Things",
    "Sensor": "Sensors",
    "ObservedProperty": "ObservedProperties",
    "Datastream": "Datastreams",
    "Observation": "Observations",
}

# The server's default page is smaller than the observation series, so iterating
# the whole layer is what proves the provider follows @iot.nextLink.
OBSERVATION_COUNT = EXPECTED_COUNTS["Observation"]


def _service_root(base_url: str) -> str:
    return f"{base_url.rstrip('/')}{SERVICE_ROOT_PATH}"


def _get_json(url: str) -> dict:
    # Stdlib only, and the query is quoted so a filter containing spaces cannot
    # produce an invalid URL rather than a server answer.
    with urllib.request.urlopen(url, timeout=30) as response:
        return json.load(response)


def _make_layer(base_url: str, entity: str, *, expand_to: str | None = None):
    from qgis.core import QgsVectorLayer

    uri = f"url='{_service_root(base_url)}' entity='{entity}'"
    if expand_to:
        uri = f"{uri} expandTo='{expand_to}'"
    return QgsVectorLayer(uri, f"sta_{entity}", "sensorthings")


@pytest.mark.integration
@pytest.mark.pyqgis
class TestSensorThingsClientCompat:
    """SensorThings 1.1 via the QGIS sensorthings provider."""

    # CERT-DISC-01 / entity sets.
    def test_advertised_entity_sets_each_resolve(
        self, qgis_app, base_url: str,
        sensorthings_evidence: CertificationEvidenceCollector,
    ) -> None:
        root = _get_json(_service_root(base_url))
        advertised = {entry["name"] for entry in root.get("value", [])}
        assert advertised, (
            f"the service root advertised no entity sets: {root!r}. If every path "
            "404s, serve.sensorthings is switched off in this fixture."
        )

        resolved = {}
        for entity, collection in ENTITY_SET_NAMES.items():
            assert collection in advertised, (
                f"{collection} is not advertised by the service root; it lists "
                f"{sorted(advertised)}"
            )
            layer = _make_layer(base_url, entity)
            assert layer.isValid(), (
                f"entity {entity!r} is advertised as {collection} but the provider "
                f"could not build a layer: {layer.error().summary()}"
            )
            assert layer.featureCount() == EXPECTED_COUNTS[entity], (
                f"{entity}: expected {EXPECTED_COUNTS[entity]} feature(s), provider "
                f"reported {layer.featureCount()}"
            )
            resolved[entity] = layer.featureCount()

        # Spec entity sets this server does not advertise. Recorded rather than
        # asserted against: the cell certifies that what is advertised is usable,
        # and an unadvertised set is a server coverage gap, not a client one.
        absent = sorted(
            {"Locations", "HistoricalLocations", "FeaturesOfInterest"} - advertised)
        sensorthings_evidence.record(
            "CERT-DISC-01", "pass", measured_count=len(resolved),
            notes=(
                f"All {len(resolved)} advertised entity sets resolved through the "
                f"sensorthings provider with the seeded counts {resolved}. "
                f"Not advertised by this server, so not exercised: {absent}."
            ),
            evidence_ref=_service_root(base_url),
        )

    # CERT-SCHM-01 / $expand.
    def test_expansion_adds_the_child_entity_fields(
        self, qgis_app, base_url: str,
        sensorthings_evidence: CertificationEvidenceCollector,
    ) -> None:
        plain = _make_layer(base_url, "Observation")
        assert plain.isValid(), plain.error().summary()
        plain_fields = {field.name() for field in plain.fields()}

        expanded = _make_layer(base_url, "Observation", expand_to="Datastream")
        assert expanded.isValid(), (
            f"expandTo='Datastream' did not yield a usable layer: "
            f"{expanded.error().summary()}"
        )
        expanded_fields = {field.name() for field in expanded.fields()}

        gained = sorted(expanded_fields - plain_fields)
        # Comparing field sets is what makes this non-vacuous: a provider that
        # silently ignored expandTo would produce an identical schema and a layer
        # that still looks valid.
        assert gained, (
            "expandTo='Datastream' produced the same schema as no expansion, so "
            "the expansion was accepted and ignored rather than performed: "
            f"{sorted(plain_fields)}"
        )
        assert any(name.startswith("Datastream") for name in gained), (
            f"expansion added fields but none belong to the child entity: {gained}")
        sensorthings_evidence.record(
            "CERT-SCHM-01", "pass", measured_count=len(gained),
            notes=(
                f"expandTo='Datastream' on Observation added {gained} to the "
                "schema, so the child entity is traversed rather than the "
                "expansion being silently dropped."
            ),
        )

    # CERT-PAGE-01 / filtering and paging.
    def test_paging_and_count_are_honoured(
        self, qgis_app, base_url: str,
        sensorthings_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = _make_layer(base_url, "Observation")
        assert layer.isValid(), layer.error().summary()

        # The provider must follow @iot.nextLink to reach the whole series; the
        # server's default page is smaller than it.
        iterated = sum(1 for _ in layer.getFeatures())
        assert iterated == OBSERVATION_COUNT, (
            f"iterating the layer yielded {iterated} of {OBSERVATION_COUNT} "
            "observations, so the provider did not follow the collection's paging"
        )

        root = _service_root(base_url)
        counted = _get_json(
            f"{root}/Observations?" + urllib.parse.urlencode(
                {"$count": "true", "$top": 2}))
        assert counted.get("@iot.count") == OBSERVATION_COUNT, (
            f"$count=true reported {counted.get('@iot.count')!r}, not "
            f"{OBSERVATION_COUNT}; a client cannot size the collection"
        )
        assert len(counted.get("value", [])) == 2, (
            f"$top=2 returned {len(counted.get('value', []))} entities")
        assert counted.get("@iot.nextLink"), (
            "$top=2 over a larger collection returned no @iot.nextLink, so a "
            "client cannot page forward"
        )

        # The last page must be short and terminate, not wrap or repeat.
        tail_size = 3
        tail = _get_json(
            f"{root}/Observations?" + urllib.parse.urlencode(
                {"$top": 5, "$skip": OBSERVATION_COUNT - tail_size}))
        assert len(tail.get("value", [])) == tail_size, (
            f"$skip={OBSERVATION_COUNT - tail_size} returned "
            f"{len(tail.get('value', []))} entities, expected {tail_size}"
        )
        assert not tail.get("@iot.nextLink"), (
            "the final page advertised a @iot.nextLink, so a paging client would "
            "not terminate"
        )
        sensorthings_evidence.record(
            "CERT-PAGE-01", "pass", measured_count=iterated,
            notes=(
                f"The provider followed @iot.nextLink to all {iterated} "
                f"observations; $count=true reported {OBSERVATION_COUNT}, $top "
                "bounded the page and advertised a next link, and the final page "
                "returned the remaining entities with no next link. Recorded "
                "against honua-server#4200, which described a paging/count "
                "defect: it does not reproduce on this build."
            ),
        )
