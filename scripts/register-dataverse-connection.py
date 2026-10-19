#!/usr/bin/env python3
"""Register real Dataverse IDs in dynamics.connections through the scoped DholeAuth API.

The secret is AES-GCM encrypted by the connector, never stored in .env or CLI argv.
Only run on an operator-controlled terminal. Do not paste tokens into tickets/logs.
"""
import getpass
import json
import re
import sys
import urllib.error
import urllib.request
import uuid
from urllib.parse import urlsplit

ORIGINS = {
    "staging": "https://dhole-api.customcodecr.com",
    "production": "https://api.logisticacastrofallas.com",
}

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        return None

def read_uuid(label):
    raw = input(f"{label} (GUID): ").strip()
    try:
        parsed = uuid.UUID(raw)
    except ValueError:
        raise ValueError(f"Invalid {label} GUID") from None
    if parsed.int == 0:
        raise ValueError(f"Invalid {label} GUID")
    return str(parsed)

def main():
    if len(sys.argv) != 2 or sys.argv[1] not in ORIGINS:
        print("Usage: python3 register-dataverse-connection.py staging|production", file=sys.stderr)
        return 2
    target = sys.argv[1]
    print(f"Target: {target} / {ORIGINS[target]}")
    print("Connection credentials are sent over HTTPS to your own Dhole API Gateway.")
    code = input("Connection code (letters/numbers/_/-): ").strip().lower()
    if not re.fullmatch(r"[a-z0-9_-]{1,64}", code):
        print("Invalid connection code", file=sys.stderr)
        return 2
    address = input("Dataverse organization HTTPS URL: ").strip().rstrip("/")
    uri = urlsplit(address)
    if (uri.scheme != "https" or uri.username or uri.password
        or uri.query or uri.fragment or uri.path not in ("", "/")
        or not re.fullmatch(r"[a-z0-9-]+\.crm[0-9]*\.dynamics\.com", uri.hostname or "", re.I)
        or uri.port is not None):
        print("Invalid Dataverse URL (expected https://name.crm.dynamics.com)", file=sys.stderr)
        return 2
    try:
        tenant_id = read_uuid("Tenant ID")
        client_id = read_uuid("Application Client ID")
    except ValueError as err:
        print(str(err), file=sys.stderr)
        return 2
    currency = input("Default currency GUID (leave blank if not applicable): ").strip()
    if currency:
        try:
            currency = str(uuid.UUID(currency))
        except ValueError:
            print("Invalid currency GUID", file=sys.stderr)
            return 2
    secret = getpass.getpass("Client Secret VALUE (hidden): ")
    token = getpass.getpass("DholeAuth bearer JWT with dynamics.admin scope (hidden): ")
    if not secret or not token:
        print("Secret and DholeAuth JWT are mandatory", file=sys.stderr)
        return 2
    payload = {
        "code": code,
        "dataverseUrl": address,
        "tenantId": tenant_id,
        "clientId": client_id,
        "clientSecret": secret,
        "defaultCurrencyId": currency or None,
    }
    request = urllib.request.Request(
        ORIGINS[target] + "/api/dynamics-admin/connections",
        data=json.dumps(payload).encode("utf-8"),
        headers={
            "Authorization": "Bearer " + token,
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
        method="POST",
    )
    # Request payload and token are held in process memory only, not written to files.
    opener = urllib.request.build_opener(NoRedirect)
    try:
        with opener.open(request, timeout=30) as response:
            summary = json.load(response)
            print("Connection stored successfully (secret omitted):")
            for field in ("id", "code", "dataverseUrl", "tenantId", "clientId", "defaultCurrencyId"):
                if field in summary:
                    print(f"  {field}: {summary[field]}")
            return 0
    except urllib.error.HTTPError as err:
        # Never dump upstream response/body in case it contains credentials.
        print(f"Connection not stored; API returned HTTP {err.code}.", file=sys.stderr)
        if err.code in (401, 403):
            print("Check JWT expiration and the explicit dynamics.admin scope.", file=sys.stderr)
        return 1
    except (urllib.error.URLError, TimeoutError) as err:
        print(f"Connection not confirmed; network/TLS error ({type(err).__name__}).", file=sys.stderr)
        return 1

if __name__ == "__main__":
    sys.exit(main())
