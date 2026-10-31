# Stand-in entrypoint. rc1 refuses to serve when a reader-incompatible script is above its label.
# The candidate applies the real 140 and 142 scripts, then serves.
import os
import subprocess
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROLE = os.environ.get("HONUA_FIXTURE_ROLE", "rc1")
MAX_SCHEMA = int(os.environ.get("HONUA_MAX_SCHEMA", "141"))
HOST = os.environ.get("HONUA_PGHOST", "postgres")
USER = os.environ.get("HONUA_PGUSER", "honua")
DATABASE = os.environ.get("HONUA_PGDATABASE", "honua")


def psql(sql, database=None):
    return subprocess.run(
        ["psql", "-h", HOST, "-U", USER, "-d", database or DATABASE, "-tA", "-v", "ON_ERROR_STOP=1", "-c", sql],
        check=False,
        capture_output=True,
        text=True,
    )


def wait_for_postgres():
    for _ in range(60):
        if psql("SELECT 1").returncode == 0:
            return
        time.sleep(1)
    sys.exit(1)


def script_number(name):
    digits = []
    for char in name:
        if char.isdigit():
            digits.append(char)
        else:
            break
    return int("".join(digits)) if digits else None


def refuse_incompatible():
    present = psql("SELECT to_regclass('public.schema_version_reader_compatibility')")
    if present.returncode != 0:
        sys.exit(1)
    if not present.stdout.strip():
        return
    rows = psql(
        "SELECT script_name FROM public.schema_version_reader_compatibility "
        "WHERE classification = 'reader-incompatible'"
    )
    if rows.returncode != 0:
        sys.exit(1)
    for line in rows.stdout.splitlines():
        number = script_number(line.strip())
        if number is not None and number > MAX_SCHEMA:
            sys.exit(2)


def apply_candidate():
    for name in ("140_CreateAiExecutionAdmission.sql", "142_CreateAiFinancialAccounting.sql"):
        text = open(f"/migrations/{name}", encoding="utf-8").read().replace("$HonuaSchema$", "public")
        path = f"/tmp/{name}"
        open(path, "w", encoding="utf-8").write(text)
        applied = subprocess.run(
            ["psql", "-h", HOST, "-U", USER, "-d", DATABASE, "-v", "ON_ERROR_STOP=1", "-f", path],
            check=False,
        )
        if applied.returncode != 0:
            sys.exit(1)
    journal = psql(
        "INSERT INTO public.schema_versions (scriptname, applied) VALUES "
        "('140_CreateAiExecutionAdmission.sql', now()), "
        "('142_CreateAiFinancialAccounting.sql', now())"
    )
    if journal.returncode != 0:
        sys.exit(1)
    compatibility = psql(
        "INSERT INTO public.schema_version_reader_compatibility "
        "(script_name, declaration_format, classification, script_sha256, min_reader_schema, script_text) "
        "VALUES ('142_CreateAiFinancialAccounting.sql', 1, 'reader-incompatible', repeat('ab', 32), 142, 'fixture') "
        "ON CONFLICT (script_name) DO NOTHING"
    )
    if compatibility.returncode != 0:
        sys.exit(1)


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/healthz/ready"):
            body = b"ok"
        elif self.path.startswith("/customer"):
            marker = psql("SELECT marker FROM public.customer_marker WHERE id = 1")
            schema = psql(
                "SELECT COALESCE(MAX((regexp_match(scriptname, '^([0-9]+)'))[1]::int), 0) "
                "FROM public.schema_versions"
            )
            if marker.returncode != 0 or schema.returncode != 0:
                self.send_response(500)
                self.end_headers()
                return
            body = f"{marker.stdout.strip()} schema={schema.stdout.strip()}".encode()
        else:
            self.send_response(404)
            self.end_headers()
            return
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        return


def main():
    wait_for_postgres()
    if ROLE == "candidate":
        apply_candidate()
    else:
        refuse_incompatible()
    ThreadingHTTPServer(("0.0.0.0", 8080), Handler).serve_forever()


if __name__ == "__main__":
    main()
