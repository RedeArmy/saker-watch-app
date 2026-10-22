#!/usr/bin/env python3
"""A stand-in identity provider for the load test (scripts/run-k6.sh): static OIDC discovery and JWKS over TLS, and a token minter.

The API validates tokens against the Authority's discovery document and JWKS (docs/clavaris-contract.md), so serving two static
documents and signing RS256 tokens with the matching key is all a load test needs. Everything lives in a private directory (mode 700) of
the temporary directory of the current user, under a name the caller picks: the key is generated for each run and never written anywhere
else, and the server uses a certificate made for the run, which the API is told to trust (SSL_CERT_FILE) so that it validates the metadata
over HTTPS like it does against a real provider. Python standard library plus the openssl command only.

  idp.py init  NAME AUTHORITY AUDIENCE   create the keys, the certificate and the discovery and jwks documents (AUTHORITY is an https URL)
  idp.py serve NAME PORT                 serve the public documents on https://127.0.0.1:PORT (the key is not among them)
  idp.py token NAME SUBJECT              print an access token for SUBJECT, valid for an hour
  idp.py cert  NAME                      print the path of the certificate, for SSL_CERT_FILE
  idp.py clean NAME                      delete everything of NAME

NAME is one to 63 lower-case letters, digits or hyphens, never a path.
"""
import base64
import functools
import http.server
import json
import os
import re
import shutil
import ssl
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path
from urllib.parse import urlparse

KEY_ID = "load-1"
PUBLIC = "public"
NAME = re.compile(r"[a-z0-9][a-z0-9-]{0,62}")


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def openssl(*args: str, data: bytes | None = None) -> bytes:
    return subprocess.run(["openssl", *args], input=data, check=True, capture_output=True).stdout


def workdir(name: str) -> Path:
    """The private directory of a run. The name cannot climb out of it: it is a plain name and the base is ours."""
    if NAME.fullmatch(name) is None:
        raise SystemExit("NAME must be 1 to 63 lower-case letters, digits or hyphens")
    base = Path(tempfile.gettempdir()) / f"saker-load-idp-{os.getuid()}"
    base.mkdir(mode=0o700, exist_ok=True)
    base.chmod(0o700)
    return base / name


def init(name: str, authority: str, audience: str) -> None:
    if urlparse(authority).scheme != "https":
        raise SystemExit("AUTHORITY must be an https URL")
    directory = workdir(name)
    directory.mkdir(mode=0o700, exist_ok=True)
    key = directory / "key.pem"
    openssl("genrsa", "-out", str(key), "2048")
    key.chmod(0o600)
    modulus = openssl("rsa", "-in", str(key), "-noout", "-modulus").decode().strip().split("=", 1)[1]
    # The TLS certificate of the stand-in server: made for this run, valid for a day, for the loopback address only.
    tls_key = directory / "tls-key.pem"
    openssl(
        "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=127.0.0.1",
        "-addext", "subjectAltName=IP:127.0.0.1", "-keyout", str(tls_key), "-out", str(directory / "cert.pem"))
    tls_key.chmod(0o600)
    # Only the public documents are served: the keys and the settings stay beside them, not inside.
    base = directory / PUBLIC / urlparse(authority).path.strip("/")
    (base / ".well-known").mkdir(parents=True, exist_ok=True)
    (base / ".well-known" / "openid-configuration").write_text(json.dumps({
        "issuer": authority,
        "jwks_uri": f"{authority}/jwks",
        "id_token_signing_alg_values_supported": ["RS256"],
    }))
    (base / "jwks").write_text(json.dumps({"keys": [{
        "kty": "RSA", "use": "sig", "alg": "RS256", "kid": KEY_ID,
        "n": b64url(bytes.fromhex(modulus)), "e": b64url((65537).to_bytes(3, "big")),
    }]}))
    (directory / "settings.json").write_text(json.dumps({"authority": authority, "audience": audience}))


def token(name: str, subject: str) -> str:
    directory = workdir(name)
    settings = json.loads((directory / "settings.json").read_text())
    now = int(time.time())
    header = {"alg": "RS256", "typ": "JWT", "kid": KEY_ID}
    claims = {
        "iss": settings["authority"], "aud": settings["audience"], "sub": subject,
        "scope": ["openid", "profile", "email"], "iat": now, "nbf": now, "exp": now + 3600, "jti": str(uuid.uuid4()),
        "email": f"{subject}@load.test", "name": "Load User", "locale": "es-GT",
    }
    signing_input = ".".join(b64url(json.dumps(part, separators=(",", ":")).encode()) for part in (header, claims))
    signature = openssl("dgst", "-sha256", "-sign", str(directory / "key.pem"), data=signing_input.encode())
    return f"{signing_input}.{b64url(signature)}"


def serve(name: str, port: int) -> None:
    directory = workdir(name)
    handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(directory / PUBLIC))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(str(directory / "cert.pem"), str(directory / "tls-key.pem"))
    server.socket = context.wrap_socket(server.socket, server_side=True)
    server.serve_forever()


def main(argv: list[str]) -> int:
    command, arguments = (argv[1], argv[2:]) if len(argv) > 1 else ("", [])
    if command == "init" and len(arguments) == 3:
        init(arguments[0], arguments[1], arguments[2])
    elif command == "serve" and len(arguments) == 2:
        serve(arguments[0], int(arguments[1]))
    elif command == "token" and len(arguments) == 2:
        print(token(arguments[0], arguments[1]))
    elif command == "cert" and len(arguments) == 1:
        print(workdir(arguments[0]) / "cert.pem")
    elif command == "clean" and len(arguments) == 1:
        shutil.rmtree(workdir(arguments[0]), ignore_errors=True)
    else:
        print(__doc__, file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
