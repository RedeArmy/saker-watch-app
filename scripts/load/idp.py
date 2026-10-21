#!/usr/bin/env python3
"""A stand-in identity provider for the load test (scripts/run-k6.sh): static OIDC discovery and JWKS, and a token minter.

The API validates tokens against the Authority's discovery document and JWKS (docs/clavaris-contract.md), so serving two static
documents and signing RS256 tokens with the matching key is all a load test needs. The key is generated for each run in a
temporary directory and is never written anywhere else. Python standard library plus the openssl command only.

  idp.py init  DIR AUTHORITY AUDIENCE   create the key, discovery and jwks documents under DIR
  idp.py serve DIR PORT                 serve DIR on 127.0.0.1:PORT (the path of AUTHORITY is the path under DIR)
  idp.py token DIR SUBJECT              print an access token for SUBJECT, valid for an hour
"""
import base64
import functools
import http.server
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path
from urllib.parse import urlparse

KEY_ID = "load-1"


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def openssl(*args: str, data: bytes | None = None) -> bytes:
    return subprocess.run(["openssl", *args], input=data, check=True, capture_output=True).stdout


def init(directory: Path, authority: str, audience: str) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    key = directory / "key.pem"
    openssl("genrsa", "-out", str(key), "2048")
    modulus = openssl("rsa", "-in", str(key), "-noout", "-modulus").decode().strip().split("=", 1)[1]
    base = directory / urlparse(authority).path.strip("/")
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


def token(directory: Path, subject: str) -> str:
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


def serve(directory: Path, port: int) -> None:
    handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(directory))
    http.server.ThreadingHTTPServer(("127.0.0.1", port), handler).serve_forever()


def main(argv: list[str]) -> int:
    if len(argv) == 5 and argv[1] == "init":
        init(Path(argv[2]), argv[3], argv[4])
    elif len(argv) == 4 and argv[1] == "serve":
        serve(Path(argv[2]), int(argv[3]))
    elif len(argv) == 4 and argv[1] == "token":
        print(token(Path(argv[2]), argv[3]))
    else:
        print(__doc__, file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
