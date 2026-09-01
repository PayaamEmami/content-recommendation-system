#!/usr/bin/env python3
"""Mint a long-lived CRS MCP API key.

Only the SHA-256 hash is stored on the Lambda (`MCP_API_KEY_SHA256`). The raw
key is printed once and pasted into the assistant as the MCP API key.

Usage:
    python mcp/create_api_key.py
"""

from __future__ import annotations

import hashlib
import secrets

KEY_PREFIX = "cak_"


def generate_api_key() -> str:
    return f"{KEY_PREFIX}{secrets.token_urlsafe(32)}"


def hash_api_key(raw_key: str) -> str:
    return hashlib.sha256(raw_key.strip().encode("utf-8")).hexdigest()


def main() -> int:
    raw_key = generate_api_key()
    key_hash = hash_api_key(raw_key)
    print("CRS MCP API key created. Store the raw key now; it will not be shown again.\n")
    print(f"  key:  {raw_key}")
    print(f"  hash: {key_hash}")
    print("\nSet MCP_API_KEY_SHA256 to the hash before running mcp/deploy.sh.")
    print("Paste the raw key into the assistant at /chat/automation.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
