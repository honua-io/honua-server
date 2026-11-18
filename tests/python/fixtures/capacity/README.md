# Capacity producer contract fixture

`lock.json` is the frozen contract from honua-release commit
`33fbb04e43f3d8168ff23e91ec2b8b9d945cd822` (frozen 2026-11-16), with retired cache
names in four ruling and derivation notes spelled generically. It is test input,
not a second production lock. The workflow fetches the lock and verifier at one
resolved release commit and verifies its manifest candidate.

The tests construct an analytical population: 60 intervals of 72,000 requests,
each with 68,400 at 200 ms, 2,880 at 600 ms and 720 at 900 ms. Two requests
in the first interval fail (one HTTP 500, one HTTP 200 with an in-band error).
Expected values are independently calculated: 4,320,000 requests, two errors,
1,200 requests/s, nearest-rank p95 200 ms and p99 600 ms, queue age 7 s,
maximum saturation 0.4, and maximum recovery duration 4 s.

These synthetic observations and certificate-shaped verifier fixtures test the
producer/consumer contract. They are never signed or published as qualification.
`test_capacity_observations.py` additionally exercises a real local HTTP server,
including malformed JSON, in-band errors, server failures and timeout outcomes.
