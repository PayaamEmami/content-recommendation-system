import hashlib
import json

import pytest

import mcp_server
from auth import hash_api_key
from mcp_tools import TOOL_DEFINITIONS


VALID_KEY = "cak_test_key"
VALID_HASH = hash_api_key(VALID_KEY)


@pytest.fixture(autouse=True)
def isolate_server(monkeypatch):
    mcp_server.request_counts.clear()
    monkeypatch.setenv("MCP_API_KEY_SHA256", VALID_HASH)
    yield
    mcp_server.request_counts.clear()


def rpc_event(method, params=None, request_id=1, token=VALID_KEY):
    body = {"jsonrpc": "2.0", "method": method}
    if request_id is not None:
        body["id"] = request_id
    if params is not None:
        body["params"] = params

    return {
        "requestContext": {"http": {"method": "POST", "sourceIp": "198.51.100.9"}},
        "headers": {"authorization": f"Bearer {token}"},
        "body": json.dumps(body),
    }


def body_of(response):
    return json.loads(response["body"])


def test_options_returns_cors_preflight():
    response = mcp_server.lambda_handler(
        {"requestContext": {"http": {"method": "OPTIONS"}}}, None
    )

    assert response["statusCode"] == 200


def test_get_is_rejected():
    response = mcp_server.lambda_handler(
        {"requestContext": {"http": {"method": "GET"}}}, None
    )

    assert response["statusCode"] == 405


def test_missing_authorization_header_returns_401():
    response = mcp_server.lambda_handler(
        {
            "requestContext": {"http": {"method": "POST"}},
            "headers": {},
            "body": "{}",
        },
        None,
    )

    assert response["statusCode"] == 401


def test_unknown_key_returns_401():
    response = mcp_server.lambda_handler(rpc_event("tools/list", token="cak_nope"), None)

    assert response["statusCode"] == 401
    assert "Invalid or revoked API key" in body_of(response)["error"]


def test_oversized_body_returns_413():
    event = rpc_event("tools/list")
    event["body"] = "x" * (mcp_server.MAX_REQUEST_BYTES + 1)

    response = mcp_server.lambda_handler(event, None)

    assert response["statusCode"] == 413


def test_initialize_advertises_crs_server():
    response = mcp_server.lambda_handler(rpc_event("initialize"), None)

    result = body_of(response)["result"]
    assert result["protocolVersion"] == mcp_server.PROTOCOL_VERSION
    assert result["capabilities"]["tools"] == {"listChanged": False}
    assert result["serverInfo"]["name"] == "crs"


def test_tools_list_returns_every_tool():
    response = mcp_server.lambda_handler(rpc_event("tools/list"), None)

    tools = body_of(response)["result"]["tools"]
    assert {tool["name"] for tool in tools} == {tool["name"] for tool in TOOL_DEFINITIONS}


def test_ping_returns_empty_result():
    response = mcp_server.lambda_handler(rpc_event("ping"), None)

    assert body_of(response)["result"] == {}


def test_unknown_method_returns_method_not_found():
    response = mcp_server.lambda_handler(rpc_event("resources/list"), None)

    assert body_of(response)["error"]["code"] == mcp_server.JSONRPC_METHOD_NOT_FOUND


def test_invalid_json_returns_parse_error():
    event = rpc_event("tools/list")
    event["body"] = "{not json"

    response = mcp_server.lambda_handler(event, None)

    assert body_of(response)["error"]["code"] == mcp_server.JSONRPC_PARSE_ERROR


def test_initialized_notification_is_acknowledged():
    response = mcp_server.lambda_handler(
        rpc_event("notifications/initialized", request_id=None), None
    )

    assert response["statusCode"] == 202
    assert response["body"] == ""


def test_tools_call_returns_json_content_block(monkeypatch):
    monkeypatch.setattr(mcp_server, "call_tool", lambda *_args: {"id": "user-1"})

    response = mcp_server.lambda_handler(
        rpc_event("tools/call", {"name": "crs_get_me", "arguments": {}}), None
    )

    result = body_of(response)["result"]
    assert result["isError"] is False
    payload = json.loads(result["content"][0]["text"])
    assert payload["id"] == "user-1"


def test_tools_call_reports_tool_errors_in_band(monkeypatch):
    def failing_tool(*_args):
        raise mcp_server.ToolError("source not found")

    monkeypatch.setattr(mcp_server, "call_tool", failing_tool)

    response = mcp_server.lambda_handler(
        rpc_event("tools/call", {"name": "crs_get_source", "arguments": {"sourceId": "NOPE"}}),
        None,
    )

    result = body_of(response)["result"]
    assert result["isError"] is True
    assert "source not found" in json.loads(result["content"][0]["text"])["error"]


def test_run_id_is_stripped_before_reaching_tool_arguments(monkeypatch):
    captured = {}

    def fake_call_tool(name, arguments):
        captured["name"] = name
        captured["arguments"] = arguments
        return {"ok": True}

    monkeypatch.setattr(mcp_server, "call_tool", fake_call_tool)

    mcp_server.lambda_handler(
        rpc_event(
            "tools/call",
            {
                "name": "crs_get_source",
                "arguments": {"_runId": "run-42", "sourceId": "src-1"},
            },
        ),
        None,
    )

    assert captured["arguments"] == {"sourceId": "src-1"}


def test_rate_limit_returns_429(monkeypatch):
    monkeypatch.setattr(mcp_server, "RATE_LIMIT_REQUESTS", 2)

    mcp_server.lambda_handler(rpc_event("ping"), None)
    mcp_server.lambda_handler(rpc_event("ping"), None)
    response = mcp_server.lambda_handler(rpc_event("ping"), None)

    assert response["statusCode"] == 429


def test_unexpected_errors_do_not_leak_details(monkeypatch):
    def exploding_tool(*_args):
        raise RuntimeError("internal detail that must not leak")

    monkeypatch.setattr(mcp_server, "call_tool", exploding_tool)

    response = mcp_server.lambda_handler(
        rpc_event("tools/call", {"name": "crs_get_me", "arguments": {}}), None
    )

    error = body_of(response)["error"]
    assert error["code"] == mcp_server.JSONRPC_INTERNAL_ERROR
    assert error["message"] == "Internal server error"


def test_hash_api_key_is_sha256():
    assert hash_api_key("cak_abc") == hashlib.sha256(b"cak_abc").hexdigest()
