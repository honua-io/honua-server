# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""OWSLib canonical OGC client certification lane (#3392, parent #3389).

OWSLib is the reference Python client for the classic OGC service stack
(WMS 1.3.0/1.1.1, WMTS 1.0.0, WFS 2.0/1.1.0/1.0.0) and for OGC API - Features.
It parses capabilities documents strictly and derives every subsequent request
from them, so this lane certifies the server the way a real analyst script
consumes it: discover first, then request only what the server advertised.

Four evidence envelopes are produced per run, one per protocol:
``{run_id}-py-owslib-{ogc-features,wfs,wms,wmts}.cert.json``.
"""
