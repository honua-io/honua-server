#!/usr/bin/env bash
# Readiness check for the owned GPServer desktop fixture (honua-server#4614, #4973, #4974, #4975).
#
#   ready-check.sh sha256:<expected image digest>
#
# Every probe goes through the fixture TLS origin with --cacert against the fixture Caddy root; no
# certificate verification is disabled. Exits non-zero on the first failed assertion.
set -euo pipefail

digest="${1:?usage: ready-check.sh sha256:<digest>}"
evidence_root="${GP_FIXTURE_EVIDENCE_ROOT:-/home/mike/honua-io/gpserver-4614-4616-evidence}"
ca="$evidence_root/ca/root.crt"
origin=https://127.0.0.1:18464
server=gpserver-4614-4616-server
postgres=gpserver-4614-4616-postgres
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

fail() { echo "NOT READY: $*" >&2; exit 1; }

echo "# identity"
read -r image_id state health revision < <(docker inspect "$server" \
  --format '{{.Image}} {{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} {{index .Config.Labels "org.opencontainers.image.revision"}}')
echo "$server image=$image_id state=$state health=$health revision=$revision"
[[ "$image_id" == "$digest" ]] || fail "$server runs $image_id, expected $digest"
[[ "$state $health" == "running healthy" ]] || fail "$server is $state/$health"

pg_env="$(docker inspect "$postgres" --format '{{range .Config.Env}}{{println .}}{{end}}')"
for var in POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL POSTGIS_ENABLE_OUTDB_RASTERS=1; do
  grep -qx "$var" <<<"$pg_env" || fail "$postgres lacks $var"
  echo "$postgres $var"
done

echo "# catalog schema"
docker logs "$server" 2>&1 | grep -o -E "Executing Database Server script '[^']+'|Database migrations completed: [0-9]+ scripts applied" | sort -u || true
# Only the non-secret Database and Username fields of the connection string are read.
conn="$(docker inspect "$server" --format '{{range .Config.Env}}{{println .}}{{end}}' | sed -n 's/^ConnectionStrings__DefaultConnection=//p')"
db_name="$(tr ';' '\n' <<<"$conn" | sed -n 's/^Database=//Ip')"
db_user="$(tr ';' '\n' <<<"$conn" | sed -n 's/^Username=//Ip')"
unset conn
latest="$(docker exec -u postgres "$postgres" psql -U "$db_user" -d "$db_name" -Atc \
  "select scriptname from public.schema_versions where scriptname ~ 'Migrations\.[0-9]{3}_' order by scriptname desc limit 1")"
echo "latest applied migration: $latest (dbSchema $(grep -o -E '\.[0-9]{3}_' <<<"$latest" | tr -d '._'))"
[[ -n "$latest" ]] || fail "no applied migrations found in $db_name"

probe() { # method path expected-status [extra curl args...]
  local method="$1" path="$2" expected="$3"; shift 3
  local out="$scratch/body" code type size
  IFS='|' read -r code type size < <(curl -sS --cacert "$ca" -X "$method" -o "$out" \
    -w '%{http_code}|%{content_type}|%{size_download}\n' "$@" "$origin$path")
  printf '%-4s %-90s -> HTTP %s %s %s bytes\n' "$method" "$path" "$code" "${type:-none}" "$size"
  [[ "$code" == "$expected" ]] || fail "$method $path answered $code, expected $expected"
}

echo "# TLS surface ($origin, --cacert $ca)"
probe GET  '/rest/info?f=json' 200
probe GET  '/services?wsdl' 200
probe GET  '/rest/services/desktop_ui_features/GPServer?f=json' 200
tasks="$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1])).get("tasks",[])))' "$scratch/body")"
echo "     desktop_ui_features GPServer tasks: $tasks"
[[ "$tasks" -gt 0 ]] || fail "desktop_ui_features advertises no GP tasks"
# Unauthenticated GetJobStatus: 401 means the SOAP GP route exists and challenges (#4614 was a 404).
job_status='<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:tns="http://www.esri.com/schemas/ArcGIS/10.8"><soap:Body><tns:GetJobStatus><JobID>j00000000000000000000000000000000</JobID></tns:GetJobStatus></soap:Body></soap:Envelope>'
probe POST '/services/desktop_ui_features/GPServer' 401 \
  -H 'Content-Type: text/xml; charset=utf-8' -H 'SOAPAction: ""' --data-binary "$job_status"

echo "# SOAP catalog site-root and argument binding (#4973, #4974)"
# #4974: Pro's site-root connection form is a bare GET, no ?wsdl.
probe GET '/services' 200
# #4973: Pro 3.7.1's captured GetServiceDescriptionsEx envelope, byte for byte, with the
# unqualified <FolderName> child that the catalog used to reject with a 400.
pro_get_service_descriptions_ex='<?xml version="1.0" encoding="utf-8" ?>
<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
               xmlns:xsd="http://www.w3.org/2001/XMLSchema"
               xmlns:tns="http://www.esri.com/schemas/ArcGIS/10.8">
  <soap:Body>
    <tns:GetServiceDescriptionsEx>
      <FolderName></FolderName>
    </tns:GetServiceDescriptionsEx>
  </soap:Body>
</soap:Envelope>
'
probe POST '/services' 200 \
  -H 'Content-Type: text/xml; charset=utf-8' -H 'SOAPAction: ""' --data-binary "$pro_get_service_descriptions_ex"
python3 - "$scratch/body" <<'PY'
import sys
import xml.etree.ElementTree as ET

def local(tag):
    return tag.rsplit('}', 1)[-1]

root = ET.parse(sys.argv[1]).getroot()
descriptions = [
    {local(child.tag): (child.text or '') for child in element}
    for element in root.iter()
    if local(element.tag) == 'ServiceDescription'
]
gp_services = [d for d in descriptions if d.get('Type') == 'GPServer']
print(f"     GetServiceDescriptionsEx (Pro envelope): {len(descriptions)} services, "
      f"GPServer: {[d.get('Name') for d in gp_services]}")
assert gp_services, f"no GPServer ServiceDescription in response: {descriptions}"
PY

echo "# ImageServer exportImage (#4975: needs the PostGIS GDAL drivers)"
# The Redis-backed output cache outlives a server recreate and stores Esri error envelopes (HTTP 200
# with a JSON body) for 60 s, so a 501 cached before the Postgres fix can be replayed right after it.
# Retry past that window (#4980); a genuine missing-driver failure still fails every attempt.
image_path='/rest/services/native_raster_public/ImageServer/exportImage?f=image&format=tiff&bbox=-122.44,37.74,-122.40,37.78&bboxSR=4326&imageSR=4326&size=256,256'
for attempt in 1 2 3 4 5; do
  probe GET "$image_path" 200
  magic="$(head -c 4 "$scratch/body" | od -An -tx1 | tr -d ' \n')"
  echo "     attempt $attempt: body mime=$(file -b --mime-type "$scratch/body") magic=$magic"
  [[ "$magic" == 49492a00 || "$magic" == 4d4d002a ]] && break
  echo "     not TIFF: $(head -c 160 "$scratch/body")"
  [[ "$attempt" == 5 ]] && fail "exportImage format=tiff did not return TIFF bytes after 5 attempts"
  sleep 20
done

echo "READY: $server on $digest"
