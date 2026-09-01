"""MCP server for the Content Recommendation System, exposed as a Lambda Function URL.

Implements the subset of Model Context Protocol needed by a remote client:
`initialize`, `notifications/initialized`, `tools/list`, and `tools/call`, spoken
as JSON-RPC 2.0 over HTTP POST (the stateless Streamable HTTP transport).

Authentication is a long-lived `cak_…` API key presented as a bearer token and
compared to `MCP_API_KEY_SHA256`. Tool handlers then log into Crs.Api with the
configured user credentials and call existing REST endpoints.
"""

from __future__ import annotations

import base64
import json
import time
from collections import defaultdict
from typing import Any, Dict

from auth import AuthError, authenticate, get_client_ip, get_http_method
from mcp_tools import ToolError, call_tool, public_tool_list


PROTOCOL_VERSION = "2025-06-18"
SERVER_NAME = "crs"
SERVER_VERSION = "1.0.0"

MAX_REQUEST_BYTES = 256 * 1024

request_counts: defaultdict[str, list[float]] = defaultdict(list)
RATE_LIMIT_REQUESTS = 240
RATE_LIMIT_WINDOW = 900

JSONRPC_PARSE_ERROR = -32700
JSONRPC_INVALID_REQUEST = -32600
JSONRPC_METHOD_NOT_FOUND = -32601
JSONRPC_INVALID_PARAMS = -32602
JSONRPC_INTERNAL_ERROR = -32603

RESPONSE_HEADERS = {"Content-Type": "application/json"}


def is_rate_limited(identity: str) -> bool:
    now = time.time()
    request_counts[identity] = [
        timestamp
        for timestamp in request_counts[identity]
        if now - timestamp < RATE_LIMIT_WINDOW
    ]

    if len(request_counts[identity]) >= RATE_LIMIT_REQUESTS:
        return True

    request_counts[identity].append(now)
    return False


def http_response(status_code: int, body: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "statusCode": status_code,
        "headers": RESPONSE_HEADERS,
        "body": json.dumps(body),
    }


def jsonrpc_result(request_id: Any, result: Dict[str, Any]) -> Dict[str, Any]:
    return http_response(200, {"jsonrpc": "2.0", "id": request_id, "result": result})


def jsonrpc_error(
    request_id: Any,
    code: int,
    message: str,
    status_code: int = 200,
) -> Dict[str, Any]:
    return http_response(
        status_code,
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "error": {"code": code, "message": message},
        },
    )


def tool_result(payload: Any, is_error: bool = False) -> Dict[str, Any]:
    """MCP tool results are content blocks; JSON goes in a text block."""
    return {
        "content": [{"type": "text", "text": json.dumps(payload, indent=2)}],
        "isError": is_error,
    }


def handle_initialize(_params: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": {"tools": {"listChanged": False}},
        "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
    }


def handle_tools_call(params: Dict[str, Any]) -> Dict[str, Any]:
    tool_name = params.get("name")
    if not isinstance(tool_name, str) or not tool_name:
        raise ToolError('Expected "name" to be a tool name')

    arguments = params.get("arguments") or {}
    if not isinstance(arguments, dict):
        raise ToolError('Expected "arguments" to be an object')

    tool_arguments = {
        key: value for key, value in arguments.items() if not str(key).startswith("_")
    }
    payload = call_tool(tool_name, tool_arguments)
    return tool_result(payload)


def dispatch(method: str, params: Dict[str, Any], request_id: Any) -> Dict[str, Any]:
    if method == "initialize":
        return jsonrpc_result(request_id, handle_initialize(params))

    if method == "ping":
        return jsonrpc_result(request_id, {})

    if method == "tools/list":
        return jsonrpc_result(request_id, {"tools": public_tool_list()})

    if method == "tools/call":
        return jsonrpc_result(request_id, handle_tools_call(params))

    return jsonrpc_error(request_id, JSONRPC_METHOD_NOT_FOUND, f'Unknown method "{method}"')


def lambda_handler(event: Dict[str, Any], _context: Any) -> Dict[str, Any]:
    http_method = get_http_method(event)

    if http_method == "OPTIONS":
        return http_response(200, {"message": "CORS preflight"})

    if http_method != "POST":
        return http_response(
            405,
            {"error": f"Method {http_method} not allowed", "success": False},
        )

    raw_body = event.get("body") or ""
    if not isinstance(raw_body, str):
        raw_body = ""

    if is_rate_limited(get_client_ip(event)):
        return http_response(
            429,
            {"error": "Too many requests. Please try again later.", "success": False},
        )

    encoded_limit = (
        (MAX_REQUEST_BYTES * 4 // 3) + 8
        if event.get("isBase64Encoded")
        else MAX_REQUEST_BYTES
    )
    if len(raw_body) > encoded_limit:
        return http_response(413, {"error": "Request body too large", "success": False})

    body = raw_body
    if event.get("isBase64Encoded"):
        try:
            body = base64.b64decode(raw_body).decode("utf-8")
        except (ValueError, UnicodeDecodeError):
            return http_response(400, {"error": "Invalid request body", "success": False})

    if len(body) > MAX_REQUEST_BYTES:
        return http_response(413, {"error": "Request body too large", "success": False})

    try:
        key_hash = authenticate(event)
    except AuthError as exc:
        return http_response(401, {"error": str(exc), "success": False})

    if is_rate_limited(key_hash):
        return http_response(
            429,
            {"error": "Too many requests. Please try again later.", "success": False},
        )

    try:
        message = json.loads(body)
    except json.JSONDecodeError:
        return jsonrpc_error(None, JSONRPC_PARSE_ERROR, "Invalid JSON body")

    if not isinstance(message, dict):
        return jsonrpc_error(
            None, JSONRPC_INVALID_REQUEST, "Batch requests are not supported"
        )

    method = message.get("method")
    request_id = message.get("id")
    params = message.get("params", {})
    if params is None:
        params = {}

    if not isinstance(method, str) or not method:
        return jsonrpc_error(request_id, JSONRPC_INVALID_REQUEST, 'Missing "method"')

    if not isinstance(params, dict):
        return jsonrpc_error(
            request_id, JSONRPC_INVALID_PARAMS, '"params" must be an object'
        )

    if request_id is None and method.startswith("notifications/"):
        return {"statusCode": 202, "headers": RESPONSE_HEADERS, "body": ""}

    try:
        return dispatch(method, params, request_id)
    except ToolError as exc:
        return jsonrpc_result(request_id, tool_result({"error": str(exc)}, True))
    except Exception as exc:  # noqa: BLE001 - never leak a stack trace to callers
        print(f"MCP server error handling {method}: {exc}")
        return jsonrpc_error(request_id, JSONRPC_INTERNAL_ERROR, "Internal server error")
