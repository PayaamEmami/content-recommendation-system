import io
import json
from datetime import datetime, timedelta, timezone
from urllib.error import HTTPError, URLError

import pytest

import crs_client
import mcp_tools
from mcp_tools import ToolError, public_tool_list


class FakeResponse:
    def __init__(self, status=200, body=None, raw=b""):
        self.status = status
        if body is not None:
            self._raw = json.dumps(body).encode("utf-8")
        else:
            self._raw = raw

    def read(self):
        return self._raw

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False


@pytest.fixture(autouse=True)
def isolate_client(monkeypatch):
    crs_client.reset_token_cache()
    monkeypatch.setenv("CRS_API_BASE_URL", "https://crs.example.test")
    monkeypatch.setenv("CRS_EMAIL", "user@example.com")
    monkeypatch.setenv("CRS_PASSWORD", "password1")
    yield
    crs_client.reset_token_cache()


def future_expiry():
    return (datetime.now(timezone.utc) + timedelta(minutes=50)).isoformat().replace(
        "+00:00", "Z"
    )


def test_public_tool_list_omits_handlers():
    tools = public_tool_list()
    names = {tool["name"] for tool in tools}
    assert "crs_list_sources" in names
    assert "crs_ingest_source" in names
    assert all("handler" not in tool for tool in tools)
    assert all("inputSchema" in tool for tool in tools)


def test_list_sources_calls_active_endpoint(monkeypatch):
    captured = {}

    def fake_request(method, path, **kwargs):
        captured["method"] = method
        captured["path"] = path
        return [{"id": "src-1"}]

    monkeypatch.setattr(mcp_tools, "request", fake_request)

    result = mcp_tools.call_tool("crs_list_sources", {"activeOnly": True})

    assert captured == {"method": "GET", "path": "/sources/active"}
    assert result[0]["id"] == "src-1"


def test_create_source_requires_category():
    with pytest.raises(ToolError, match="category"):
        mcp_tools.call_tool(
            "crs_create_source",
            {"name": "Arxiv", "url": "https://example.com/rss"},
        )


def test_update_source_requires_a_field():
    with pytest.raises(ToolError, match="at least one field"):
        mcp_tools.call_tool("crs_update_source", {"sourceId": "src-1"})


def test_vote_posts_vote_type(monkeypatch):
    captured = {}

    def fake_request(method, path, **kwargs):
        captured["method"] = method
        captured["path"] = path
        captured["payload"] = kwargs.get("payload")
        return {"voteType": "Upvote"}

    monkeypatch.setattr(mcp_tools, "request", fake_request)

    mcp_tools.call_tool(
        "crs_vote", {"contentId": "c1", "voteType": "Upvote"}
    )

    assert captured["method"] == "POST"
    assert captured["path"] == "/content/c1/vote"
    assert captured["payload"] == {"voteType": "Upvote"}


def test_ingest_source_uses_long_timeout_and_notes_stale_feed(monkeypatch):
    captured = {}

    def fake_request(method, path, **kwargs):
        captured["timeout"] = kwargs.get("timeout")
        return {"success": True, "saved": 3}

    monkeypatch.setattr(mcp_tools, "request", fake_request)

    result = mcp_tools.call_tool("crs_ingest_source", {"sourceId": "src-1"})

    assert captured["timeout"] == crs_client.INGEST_TIMEOUT_SECONDS
    assert "stale" in result["note"]


def test_unknown_tool_is_a_tool_error():
    with pytest.raises(ToolError, match="Unknown tool"):
        mcp_tools.call_tool("crs_explode", {})


def test_api_errors_become_tool_errors(monkeypatch):
    def fake_request(*_args, **_kwargs):
        raise crs_client.CrsApiError("Source not found", 404)

    monkeypatch.setattr(mcp_tools, "request", fake_request)

    with pytest.raises(ToolError, match="Source not found"):
        mcp_tools.call_tool("crs_get_source", {"sourceId": "missing"})


def test_login_is_cached_across_requests(monkeypatch):
    calls = []

    def fake_urlopen(request, timeout=None):
        calls.append((request.full_url, request.get_method(), timeout))
        if request.full_url.endswith("/auth/login"):
            return FakeResponse(
                body={
                    "accessToken": "access-1",
                    "refreshToken": "refresh-1",
                    "expiresAt": future_expiry(),
                }
            )
        return FakeResponse(body=[{"id": "src-1"}])

    monkeypatch.setattr(crs_client.urllib.request, "urlopen", fake_urlopen)

    crs_client.request("GET", "/sources")
    crs_client.request("GET", "/sources")

    login_calls = [call for call in calls if call[0].endswith("/auth/login")]
    assert len(login_calls) == 1
    assert len(calls) == 3


def test_expired_token_refreshes_instead_of_logging_in_again(monkeypatch):
    crs_client._access_token = "old-access"
    crs_client._refresh_token = "refresh-1"
    crs_client._expires_at = 1

    calls = []

    def fake_urlopen(request, timeout=None):
        calls.append(request.full_url)
        if request.full_url.endswith("/auth/refresh"):
            return FakeResponse(
                body={
                    "accessToken": "access-2",
                    "refreshToken": "refresh-2",
                    "expiresAt": future_expiry(),
                }
            )
        if request.full_url.endswith("/auth/login"):
            raise AssertionError("login should not be called when refresh succeeds")
        return FakeResponse(body={"id": "me"})

    monkeypatch.setattr(crs_client.urllib.request, "urlopen", fake_urlopen)

    crs_client.request("GET", "/users/me")

    assert any(url.endswith("/auth/refresh") for url in calls)
    assert all(not url.endswith("/auth/login") for url in calls)


def test_unauthorized_api_call_retries_after_relogin(monkeypatch):
    attempts = {"sources": 0}

    def fake_urlopen(request, timeout=None):
        if request.full_url.endswith("/auth/login"):
            return FakeResponse(
                body={
                    "accessToken": "access-1",
                    "refreshToken": "refresh-1",
                    "expiresAt": future_expiry(),
                }
            )
        if request.full_url.endswith("/sources"):
            attempts["sources"] += 1
            if attempts["sources"] == 1:
                raise HTTPError(
                    request.full_url,
                    401,
                    "Unauthorized",
                    hdrs={},
                    fp=io.BytesIO(b'{"message":"expired"}'),
                )
            return FakeResponse(body=[])
        raise AssertionError(request.full_url)

    monkeypatch.setattr(crs_client.urllib.request, "urlopen", fake_urlopen)

    result = crs_client.request("GET", "/sources")

    assert result == []
    assert attempts["sources"] == 2


def test_unreachable_api_becomes_crs_error(monkeypatch):
    def fake_urlopen(request, timeout=None):
        raise URLError("timed out")

    monkeypatch.setattr(crs_client.urllib.request, "urlopen", fake_urlopen)

    with pytest.raises(crs_client.CrsApiError, match="Could not reach Crs.Api"):
        crs_client.request("GET", "/sources")
