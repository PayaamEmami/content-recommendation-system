"""HTTP client for Crs.Api. Caches the user JWT so login stays off the hot path.

Crs.Api rate-limits `/api/v1/auth/login` to 10 requests per minute per IP, so
every tool call must reuse a cached access token and refresh it before expiry.
"""

from __future__ import annotations

import json
import os
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime
from typing import Any, Dict, Optional


DEFAULT_TIMEOUT_SECONDS = 30
INGEST_TIMEOUT_SECONDS = 120
LOGIN_TIMEOUT_SECONDS = 15
# Refresh a minute before expiry so a slow ingest does not race the JWT TTL.
ACCESS_TOKEN_SKEW_SECONDS = 60


class CrsApiError(Exception):
    """Raised when Crs.Api returns a non-success status. Message is agent-visible."""

    def __init__(self, message: str, status_code: Optional[int] = None):
        super().__init__(message)
        self.status_code = status_code


_lock = threading.Lock()
_access_token: Optional[str] = None
_refresh_token: Optional[str] = None
_expires_at: float = 0.0


def reset_token_cache() -> None:
    """Test helper and 401 recovery: drop cached tokens so the next call logs in."""
    global _access_token, _refresh_token, _expires_at
    with _lock:
        _access_token = None
        _refresh_token = None
        _expires_at = 0.0


def require_api_base_url() -> str:
    url = os.environ.get("CRS_API_BASE_URL", "").strip().rstrip("/")
    if not url:
        raise CrsApiError("Server misconfigured: CRS_API_BASE_URL is not set")
    return url


def api_url(path: str) -> str:
    relative = path if path.startswith("/") else f"/{path}"
    return f"{require_api_base_url()}/api/v1{relative}"


def _read_error_body(error: urllib.error.HTTPError) -> str:
    try:
        raw = error.read().decode("utf-8")
    except (OSError, UnicodeDecodeError):
        return error.reason or f"HTTP {error.code}"

    try:
        payload = json.loads(raw)
    except json.JSONDecodeError:
        return raw.strip() or error.reason or f"HTTP {error.code}"

    if isinstance(payload, dict):
        for key in ("detail", "message", "title", "error"):
            value = payload.get(key)
            if isinstance(value, str) and value.strip():
                return value.strip()
    return raw.strip() or error.reason or f"HTTP {error.code}"


def _http_json(
    method: str,
    url: str,
    payload: Optional[Dict[str, Any]] = None,
    token: Optional[str] = None,
    timeout: float = DEFAULT_TIMEOUT_SECONDS,
) -> Any:
    headers = {"Accept": "application/json"}
    body = None
    if payload is not None:
        body = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"

    request = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            if response.status == 204:
                return None
            raw = response.read().decode("utf-8")
            if not raw:
                return None
            return json.loads(raw)
    except urllib.error.HTTPError as exc:
        raise CrsApiError(_read_error_body(exc), exc.code) from exc
    except urllib.error.URLError as exc:
        raise CrsApiError(f"Could not reach Crs.Api: {exc.reason}") from exc


def _store_tokens(payload: Dict[str, Any]) -> None:
    global _access_token, _refresh_token, _expires_at
    access = payload.get("accessToken")
    refresh = payload.get("refreshToken")
    if not isinstance(access, str) or not access:
        raise CrsApiError("Crs.Api login did not return an access token")
    _access_token = access
    if isinstance(refresh, str) and refresh:
        _refresh_token = refresh

    expires_at = payload.get("expiresAt")
    if isinstance(expires_at, str):
        try:
            parsed = datetime.fromisoformat(expires_at.replace("Z", "+00:00"))
            _expires_at = parsed.timestamp()
            return
        except ValueError:
            pass
    _expires_at = time.time() + 50 * 60


def _login() -> None:
    email = os.environ.get("CRS_EMAIL", "").strip()
    password = os.environ.get("CRS_PASSWORD", "")
    if not email or not password:
        raise CrsApiError("Server misconfigured: CRS_EMAIL and CRS_PASSWORD are required")

    payload = _http_json(
        "POST",
        api_url("/auth/login"),
        {"email": email, "password": password},
        timeout=LOGIN_TIMEOUT_SECONDS,
    )
    if not isinstance(payload, dict):
        raise CrsApiError("Crs.Api login returned an unexpected payload")
    _store_tokens(payload)


def _refresh() -> None:
    if not _refresh_token:
        raise CrsApiError("No refresh token is cached")
    payload = _http_json(
        "POST",
        api_url("/auth/refresh"),
        {"refreshToken": _refresh_token},
        timeout=LOGIN_TIMEOUT_SECONDS,
    )
    if not isinstance(payload, dict):
        raise CrsApiError("Crs.Api refresh returned an unexpected payload")
    _store_tokens(payload)


def get_access_token() -> str:
    global _access_token, _refresh_token, _expires_at
    with _lock:
        now = time.time()
        if _access_token and now < _expires_at - ACCESS_TOKEN_SKEW_SECONDS:
            return _access_token

        if _refresh_token:
            try:
                _refresh()
                assert _access_token is not None
                return _access_token
            except CrsApiError:
                _access_token = None
                _refresh_token = None
                _expires_at = 0.0

        _login()
        assert _access_token is not None
        return _access_token


def request(
    method: str,
    path: str,
    payload: Optional[Dict[str, Any]] = None,
    query: Optional[Dict[str, Any]] = None,
    timeout: float = DEFAULT_TIMEOUT_SECONDS,
) -> Any:
    """Authenticated JSON request against `/api/v1`. Retries once after a 401."""
    query_items = []
    for key, value in (query or {}).items():
        if value is None:
            continue
        query_items.append((key, str(value)))
    encoded = urllib.parse.urlencode(query_items)
    url = api_url(path)
    if encoded:
        url = f"{url}?{encoded}"

    token = get_access_token()
    try:
        return _http_json(method, url, payload, token=token, timeout=timeout)
    except CrsApiError as exc:
        if exc.status_code != 401:
            raise
        reset_token_cache()
        token = get_access_token()
        return _http_json(method, url, payload, token=token, timeout=timeout)
