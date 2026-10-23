# GPServer Esri alias contract — pinned-candidate replay (#4781)

PR #4903 fixed the contract/claim/published-list agreement on trunk and
released the one criterion that needs a certified image
(`Refs #4781 (released: the honua-esri-compat matrix and the alias
certification case are bound to the next certified image, per that issue's
own acceptance)`). That trunk commit (`f820af6712`) merged **after** the
2026.1 candidate re-pinned to `548b7a5263da` (release PR #349); it is not an
ancestor of the pin (`git merge-base --is-ancestor f820af6712
548b7a5263da5a3f2381eb43f232687cdf92b0bf` fails), so the fix is not inside
any published image yet.

Replayed the issue's own reproduction directly against the exact pinned
digest `ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e7
0800f412188aeccf5ec3d32d9dac675c1` (revision `548b7a5263da`), booted with a
disposable PostGIS/Redis pair and a throwaway key-ring PKCS#12
(`Operations__SecretChannel__KeyRingCertificatePath`):

```
GET /rest/services/geoprocessing/GPServer?f=json
```

`gpserver-alias-candidate-548b7a5-tasklist.json` is the raw response: 119
tasks, 37 of them aliases (not 39). `DeleteFeatures` and `CalculateField` are
absent under both the alias and canonical spellings — the exact gap the
issue reported. The pinned candidate's own
`GPServerEsriTaskAliases.cs` still maps both:

```
$ git show 548b7a5263da:src/Honua.Protocols.GeoServices/GPServer/GPServerEsriTaskAliases.cs \
    | grep -n "DeleteFeatures\|CalculateField"
115:        ["data-management.delete-features"] = "DeleteFeatures",
116:        ["data-management.calculate-field"] = "CalculateField",
```

So the runtime publish behavior at this pin was already job-callable-only
(inherited from #4409, unchanged by #4903); what #4903 corrected is the
*declared* contract and parity claim, which still overclaim 39 on this pin.

**Disposition: released, unchanged from #4903.** The honua-esri-compat
matrix regeneration and `GP-PRM-TASKNAME-ESRI-CONVENTIONAL-ALIASES` cannot
pass against a candidate that predates the fix — regenerating their matrix
before a re-pin containing `f820af6712` would fail their own drift gate (it
checks out honua-server at the candidate's `source_sha`). All three of the
issue's acceptance criteria that live in this repo (contract/claim/published
list agree, and a structural test pinning it) are satisfied on trunk since
#4903. The remaining criterion needs a re-pin past `f820af6712` before
honua-esri-compat can act on it; #4781 stays open with that dependency
recorded here and on the issue.
