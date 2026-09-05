# Curated edge-data corpus

`v1/manifest.json` is the frozen first tranche of the release/2026.1 test-data
corpus. Test code must resolve assets through `CuratedCorpus`; the resolver
checks the recorded byte length and SHA-256 digest before exposing bytes.

The data is synthetic, contains no production information, and is released
under the repository license. Each manifest entry records its scenario facets,
media type, provenance, and license. A new corpus revision gets a new directory;
never change `v1` to alter the meaning of an existing test receipt.

To intentionally regenerate the binary Zarr chunk, run:

```sh
python3 scripts/test-data/generate-curated-corpus-zarr.py
```

Review the byte-level diff, update the corresponding manifest length and digest,
and run `CuratedCorpusTests`. The generator writes only the deterministic chunk;
metadata and all other assets remain review-authored.
