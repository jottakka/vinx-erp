#!/usr/bin/env python3
"""Mock local da Storage API do Supabase para os testes de contrato do shell.

Espelha o contrato REAL observado no storage-api oficial (rota
/list/:bucketName -> storage.search):
  - resposta hierarquica: para o prefixo P, devolve placeholders de pasta
    (id null) para caminhos mais fundos e SOMENTE os objetos do proprio nivel;
  - nomes RELATIVOS ao prefixo (path_tokens[niveis do prefixo]);
  - ordenacao por nome asc dentro de cada grupo, pastas antes dos objetos.

Uso: mock-storage-server.py <fixture.json>
Variaveis: MOCK_PORT_FILE (obrigatoria) e MOCK_LOG (opcional).
Fixture:
  {
    "service_key": "...",
    "bucket_response": "ok" | "garbage" | "notalist",
    "buckets": [{"name": "docs"}],
    "objects": {"docs": ["reports/2026/q1/deep.txt", "root-a.txt"]}
  }
"""
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

FIXTURE_PATH = sys.argv[1]
PORT_FILE = os.environ["MOCK_PORT_FILE"]
LOG_PATH = os.environ.get("MOCK_LOG")

with open(FIXTURE_PATH, encoding="utf-8") as fh:
    FIXTURE = json.load(fh)


def log(message: str) -> None:
    if LOG_PATH:
        with open(LOG_PATH, "a", encoding="utf-8") as fh:
            fh.write(message + "\n")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send(self, status: int, body: str) -> None:
        payload = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _authorized(self) -> bool:
        expected = "Bearer " + FIXTURE["service_key"]
        return self.headers.get("Authorization") == expected

    def log_message(self, *_args) -> None:  # silencia stderr do servidor
        pass

    def do_GET(self) -> None:
        if not self._authorized():
            self._send(401, '{"error":"unauthorized"}')
            return
        if self.path.rstrip("/").endswith("/bucket"):
            mode = FIXTURE.get("bucket_response", "ok")
            if mode == "garbage":
                self._send(200, "{{not-json")
                return
            if mode == "notalist":
                self._send(200, '{"error":"inesperado"}')
                return
            self._send(200, json.dumps(FIXTURE["buckets"]))
            return
        self._send(404, '{"error":"not found"}')

    def do_POST(self) -> None:
        if not self._authorized():
            self._send(401, '{"error":"unauthorized"}')
            return
        marker = "/object/list/"
        if marker not in self.path:
            self._send(404, '{"error":"not found"}')
            return
        bucket = self.path.split(marker, 1)[1].split("?", 1)[0]
        length = int(self.headers.get("Content-Length", "0"))
        request = json.loads(self.rfile.read(length).decode("utf-8"))
        prefix = request.get("prefix", "")
        limit = int(request.get("limit", 100))
        offset = int(request.get("offset", 0))
        order = (request.get("sortBy") or {}).get("order", "asc")
        page = list_page(FIXTURE["objects"].get(bucket, []), prefix, limit, offset, order)
        log(f"POST /object/list/{bucket} prefix={prefix!r} offset={offset} limit={limit}")
        if FIXTURE.get("list_sleep"):
            import time

            time.sleep(float(FIXTURE["list_sleep"]))
        self._send(200, json.dumps(page))


def list_page(objects, prefix, limit, offset, order):
    """Uma pagina hierarquica do nivel de `prefix`, como storage.search faz."""
    depth = prefix.count("/") + 1
    folders = {}
    leafs = []
    for name in objects:
        if not name.startswith(prefix):
            continue
        segments = name[len(prefix):].split("/")
        if len(segments) == 1:
            leafs.append({"name": segments[0], "id": f"id-{name}"})
        else:
            folders[segments[0]] = {"name": segments[0], "id": None}
    key = lambda entry: entry["name"]  # noqa: E731
    reverse = order == "desc"
    page = sorted(folders.values(), key=key, reverse=reverse)
    page += sorted(leafs, key=key, reverse=reverse)
    return page[offset:offset + limit]


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
with open(PORT_FILE, "w", encoding="utf-8") as fh:
    fh.write(str(server.server_port))
server.serve_forever()
