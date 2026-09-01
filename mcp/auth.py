"""Bearer-token helpers for the CRS MCP Lambda.

The assistant authenticates with a long-lived `cak_…` key. Only the SHA-256
hash is stored on the function (`MCP_API_KEY_SHA256`); the raw key is minted
once by `create_api_key.py` and pasted into the assistant.
"""

from __future__ import annotations

import hashlib
import hmac
import os
from typing import Any, Dict


class AuthError(Exception):
    """Raised when an API request is missing or has invalid auth."""


def get_http_method(event: Dict[str, Any]) -> str:
    return (
        event.get("requestContext", {})
        .get("http", {})
        .get("method", event.get("httpMethod", ""))
        .upper()
    )


def get_client_ip(event: Dict[str, Any]) -> str:
    return (
        event.get("requestContext", {})
        .get("http", {})
        .get("sourceIp", event.get("headers", {}).get("x-forwarded-for", "unknown"))
    )


def get_bearer_token(event: Dict[str, Any]) -> str:
    headers = event.get("headers", {}) or {}
    auth_header = headers.get("authorization") or headers.get("Authorization")

    if not auth_header or not auth_header.startswith("Bearer "):
        raise AuthError("Missing or invalid authorization header")

    token = auth_header.split(" ", 1)[1].strip()
    if not token:
        raise AuthError("Missing bearer token")

    return token


def hash_api_key(raw_key: str) -> str:
    return hashlib.sha256(raw_key.strip().encode("utf-8")).hexdigest()


def configured_key_hash() -> str:
    expected = os.environ.get("MCP_API_KEY_SHA256", "").strip().lower()
    if not expected:
        raise AuthError("Server misconfigured: MCP_API_KEY_SHA256 is not set")
    if len(expected) != 64 or any(ch not in "0123456789abcdef" for ch in expected):
        raise AuthError("Server misconfigured: MCP_API_KEY_SHA256 is invalid")
    return expected


def authenticate(event: Dict[str, Any]) -> str:
    """Return the key hash when the bearer token matches the configured hash."""
    raw_key = get_bearer_token(event)
    actual = hash_api_key(raw_key)
    expected = configured_key_hash()
    if not hmac.compare_digest(actual, expected):
        raise AuthError("Invalid or revoked API key")
    return actual
