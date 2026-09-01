"""Tool definitions and handlers exposed by the CRS MCP server.

Handlers go through Crs.Api over HTTP so validation, embeddings, and indexing
stay in the API. This process never talks to Postgres or OpenSearch.
"""

from __future__ import annotations

from typing import Any, Callable, Dict, List, Optional

from crs_client import (
    CrsApiError,
    INGEST_TIMEOUT_SECONDS,
    request,
)


FEED_TYPES = ("Paper", "Video", "BlogPost")
VOTE_TYPES = ("Upvote", "Downvote")


class ToolError(Exception):
    """Raised when a tool call cannot be completed. Message is agent-visible."""


def require_string(arguments: Dict[str, Any], key: str) -> str:
    value = arguments.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ToolError(f'Expected "{key}" to be a non-empty string')
    return value.strip()


def optional_string(arguments: Dict[str, Any], key: str) -> Optional[str]:
    if key not in arguments or arguments[key] is None:
        return None
    value = arguments[key]
    if not isinstance(value, str):
        raise ToolError(f'Expected "{key}" to be a string')
    trimmed = value.strip()
    return trimmed or None


def optional_bool(arguments: Dict[str, Any], key: str) -> Optional[bool]:
    if key not in arguments or arguments[key] is None:
        return None
    value = arguments[key]
    if not isinstance(value, bool):
        raise ToolError(f'Expected "{key}" to be a boolean')
    return value


def optional_int(arguments: Dict[str, Any], key: str, minimum: int, maximum: int) -> Optional[int]:
    if key not in arguments or arguments[key] is None:
        return None
    value = arguments[key]
    if isinstance(value, bool) or not isinstance(value, int):
        raise ToolError(f'Expected "{key}" to be an integer')
    if value < minimum or value > maximum:
        raise ToolError(f'Expected "{key}" to be between {minimum} and {maximum}')
    return value


def require_enum(arguments: Dict[str, Any], key: str, allowed: tuple[str, ...]) -> str:
    value = require_string(arguments, key)
    if value not in allowed:
        raise ToolError(f'Expected "{key}" to be one of: {", ".join(allowed)}')
    return value


def optional_enum(arguments: Dict[str, Any], key: str, allowed: tuple[str, ...]) -> Optional[str]:
    value = optional_string(arguments, key)
    if value is None:
        return None
    if value not in allowed:
        raise ToolError(f'Expected "{key}" to be one of: {", ".join(allowed)}')
    return value


def wrap_api(method: str, path: str, **kwargs: Any) -> Any:
    try:
        result = request(method, path, **kwargs)
    except CrsApiError as exc:
        raise ToolError(str(exc)) from exc
    return {} if result is None else result


def tool_get_me(_arguments: Dict[str, Any]) -> Any:
    return wrap_api("GET", "/users/me")


def tool_list_sources(arguments: Dict[str, Any]) -> Any:
    if optional_bool(arguments, "activeOnly"):
        return wrap_api("GET", "/sources/active")
    return wrap_api("GET", "/sources")


def tool_get_source(arguments: Dict[str, Any]) -> Any:
    source_id = require_string(arguments, "sourceId")
    return wrap_api("GET", f"/sources/{source_id}")


def tool_create_source(arguments: Dict[str, Any]) -> Any:
    payload: Dict[str, Any] = {
        "name": require_string(arguments, "name"),
        "url": require_string(arguments, "url"),
        "category": require_enum(arguments, "category", FEED_TYPES),
    }
    description = optional_string(arguments, "description")
    if description is not None:
        payload["description"] = description
    is_active = optional_bool(arguments, "isActive")
    if is_active is not None:
        payload["isActive"] = is_active
    return wrap_api("POST", "/sources", payload=payload)


def tool_update_source(arguments: Dict[str, Any]) -> Any:
    source_id = require_string(arguments, "sourceId")
    payload: Dict[str, Any] = {}
    name = optional_string(arguments, "name")
    if name is not None:
        payload["name"] = name
    url = optional_string(arguments, "url")
    if url is not None:
        payload["url"] = url
    description = optional_string(arguments, "description")
    if description is not None:
        payload["description"] = description
    category = optional_enum(arguments, "category", FEED_TYPES)
    if category is not None:
        payload["category"] = category
    is_active = optional_bool(arguments, "isActive")
    if is_active is not None:
        payload["isActive"] = is_active
    if not payload:
        raise ToolError("Provide at least one field to update")
    return wrap_api("PUT", f"/sources/{source_id}", payload=payload)


def tool_delete_source(arguments: Dict[str, Any]) -> Any:
    source_id = require_string(arguments, "sourceId")
    wrap_api("DELETE", f"/sources/{source_id}")
    return {"deleted": True, "sourceId": source_id}


def tool_get_todays_feeds(_arguments: Dict[str, Any]) -> Any:
    return wrap_api("GET", "/recommendations")


def tool_get_feed(arguments: Dict[str, Any]) -> Any:
    feed_type = require_enum(arguments, "feedType", FEED_TYPES)
    date = optional_string(arguments, "date")
    query = {"date": date} if date else None
    return wrap_api("GET", f"/recommendations/{feed_type}", query=query)


def tool_list_content(arguments: Dict[str, Any]) -> Any:
    query: Dict[str, Any] = {
        "pageNumber": optional_int(arguments, "pageNumber", 1, 10_000) or 1,
        "pageSize": optional_int(arguments, "pageSize", 1, 100) or 20,
    }
    content_type = optional_enum(arguments, "type", FEED_TYPES)
    if content_type is not None:
        query["type"] = content_type
    return wrap_api("GET", "/content", query=query)


def tool_get_content(arguments: Dict[str, Any]) -> Any:
    content_id = require_string(arguments, "contentId")
    return wrap_api("GET", f"/content/{content_id}")


def tool_get_vote(arguments: Dict[str, Any]) -> Any:
    content_id = require_string(arguments, "contentId")
    return wrap_api("GET", f"/content/{content_id}/vote")


def tool_list_votes(_arguments: Dict[str, Any]) -> Any:
    return wrap_api("GET", "/users/me/votes")


def tool_list_vote_history(_arguments: Dict[str, Any]) -> Any:
    return wrap_api("GET", "/users/me/vote-history")


def tool_vote(arguments: Dict[str, Any]) -> Any:
    content_id = require_string(arguments, "contentId")
    vote_type = require_enum(arguments, "voteType", VOTE_TYPES)
    return wrap_api(
        "POST",
        f"/content/{content_id}/vote",
        payload={"voteType": vote_type},
    )


def tool_remove_vote(arguments: Dict[str, Any]) -> Any:
    content_id = require_string(arguments, "contentId")
    wrap_api("DELETE", f"/content/{content_id}/vote")
    return {"deleted": True, "contentId": content_id}


def tool_create_preference(arguments: Dict[str, Any]) -> Any:
    payload: Dict[str, Any] = {
        "title": require_string(arguments, "title"),
        "voteType": require_enum(arguments, "voteType", VOTE_TYPES),
    }
    description = optional_string(arguments, "description")
    if description is not None:
        payload["description"] = description
    url = optional_string(arguments, "url")
    if url is not None:
        payload["url"] = url
    content_type = optional_enum(arguments, "contentType", FEED_TYPES)
    if content_type is not None:
        payload["contentType"] = content_type
    return wrap_api("POST", "/preferences", payload=payload)


def tool_ingest_source(arguments: Dict[str, Any]) -> Any:
    source_id = require_string(arguments, "sourceId")
    result = wrap_api(
        "POST",
        f"/ingestion/ingest-source/{source_id}",
        timeout=INGEST_TIMEOUT_SECONDS,
    )
    if isinstance(result, dict):
        result.setdefault(
            "note",
            "New items are saved and embedded. Today's precomputed feed may stay "
            "stale until scripts/run-job.sh feed runs locally.",
        )
    return result


def tool_preview_url(arguments: Dict[str, Any]) -> Any:
    url = require_string(arguments, "url")
    return wrap_api(
        "POST",
        "/ingestion/ingest-url",
        payload={"url": url},
        timeout=INGEST_TIMEOUT_SECONDS,
    )


TOOL_DEFINITIONS: List[Dict[str, Any]] = [
    {
        "name": "crs_get_me",
        "description": "Return the authenticated CRS user profile, including their sources.",
        "handler": tool_get_me,
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "crs_list_sources",
        "description": (
            "List the user's content sources (RSS, video, blogs). Set activeOnly to "
            "true to hide paused sources."
        ),
        "handler": tool_list_sources,
        "inputSchema": {
            "type": "object",
            "properties": {
                "activeOnly": {
                    "type": "boolean",
                    "description": "When true, return only sources that are currently ingesting.",
                }
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_get_source",
        "description": "Get one source by id, including its category and content count.",
        "handler": tool_get_source,
        "inputSchema": {
            "type": "object",
            "properties": {
                "sourceId": {"type": "string", "description": "Source id from crs_list_sources."}
            },
            "required": ["sourceId"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_create_source",
        "description": "Add a content source for the user. category is Paper, Video, or BlogPost.",
        "handler": tool_create_source,
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string"},
                "url": {"type": "string", "description": "RSS, Atom, or site URL to ingest."},
                "description": {"type": "string"},
                "category": {"type": "string", "enum": list(FEED_TYPES)},
                "isActive": {"type": "boolean"},
            },
            "required": ["name", "url", "category"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_update_source",
        "description": "Patch a source's name, URL, description, category, or active flag.",
        "handler": tool_update_source,
        "inputSchema": {
            "type": "object",
            "properties": {
                "sourceId": {"type": "string"},
                "name": {"type": "string"},
                "url": {"type": "string"},
                "description": {"type": "string"},
                "category": {"type": "string", "enum": list(FEED_TYPES)},
                "isActive": {"type": "boolean"},
            },
            "required": ["sourceId"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_delete_source",
        "description": "Delete a source. Existing ingested content is not removed.",
        "handler": tool_delete_source,
        "inputSchema": {
            "type": "object",
            "properties": {"sourceId": {"type": "string"}},
            "required": ["sourceId"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_get_todays_feeds",
        "description": (
            "Get today's precomputed recommendations for every feed type (Paper, "
            "Video, BlogPost)."
        ),
        "handler": tool_get_todays_feeds,
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "crs_get_feed",
        "description": (
            "Get recommendations for one feed type. date is YYYY-MM-DD and defaults "
            "to today. This reads the precomputed feed, not a live vector search."
        ),
        "handler": tool_get_feed,
        "inputSchema": {
            "type": "object",
            "properties": {
                "feedType": {"type": "string", "enum": list(FEED_TYPES)},
                "date": {"type": "string", "description": "YYYY-MM-DD. Defaults to today."},
            },
            "required": ["feedType"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_list_content",
        "description": "List ingested content with optional type filter and pagination.",
        "handler": tool_list_content,
        "inputSchema": {
            "type": "object",
            "properties": {
                "pageNumber": {"type": "integer", "minimum": 1},
                "pageSize": {"type": "integer", "minimum": 1, "maximum": 100},
                "type": {"type": "string", "enum": list(FEED_TYPES)},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_get_content",
        "description": "Get one content item by id.",
        "handler": tool_get_content,
        "inputSchema": {
            "type": "object",
            "properties": {"contentId": {"type": "string"}},
            "required": ["contentId"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_get_vote",
        "description": "Get the user's vote on a content item, if any.",
        "handler": tool_get_vote,
        "inputSchema": {
            "type": "object",
            "properties": {"contentId": {"type": "string"}},
            "required": ["contentId"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_list_votes",
        "description": "List the user's votes on content.",
        "handler": tool_list_votes,
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "crs_list_vote_history",
        "description": "List the user's votes with the related content title, URL, and type.",
        "handler": tool_list_vote_history,
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "crs_vote",
        "description": "Upvote or downvote a content item. Replaces an existing vote on that item.",
        "handler": tool_vote,
        "inputSchema": {
            "type": "object",
            "properties": {
                "contentId": {"type": "string"},
                "voteType": {"type": "string", "enum": list(VOTE_TYPES)},
            },
            "required": ["contentId", "voteType"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_remove_vote",
        "description": "Remove the user's vote from a content item.",
        "handler": tool_remove_vote,
        "inputSchema": {
            "type": "object",
            "properties": {"contentId": {"type": "string"}},
            "required": ["contentId"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_create_preference",
        "description": (
            "Record manual feedback about a title or URL so the recommender can "
            "learn likes and dislikes that are not already ingested as content."
        ),
        "handler": tool_create_preference,
        "inputSchema": {
            "type": "object",
            "properties": {
                "title": {"type": "string"},
                "description": {"type": "string"},
                "url": {"type": "string"},
                "contentType": {"type": "string", "enum": list(FEED_TYPES)},
                "voteType": {"type": "string", "enum": list(VOTE_TYPES)},
            },
            "required": ["title", "voteType"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_ingest_source",
        "description": (
            "Run the ingestion pipeline for one source: extract, dedupe, save, and "
            "embed. This can take up to two minutes. Today's ranked feed may not "
            "include the new items until the local feed job runs."
        ),
        "handler": tool_ingest_source,
        "inputSchema": {
            "type": "object",
            "properties": {
                "sourceId": {"type": "string", "description": "Source id from crs_list_sources."}
            },
            "required": ["sourceId"],
            "additionalProperties": False,
        },
    },
    {
        "name": "crs_preview_url",
        "description": (
            "Preview what the ingestion agent would extract from a URL without "
            "saving or indexing it."
        ),
        "handler": tool_preview_url,
        "inputSchema": {
            "type": "object",
            "properties": {"url": {"type": "string"}},
            "required": ["url"],
            "additionalProperties": False,
        },
    },
]


TOOLS_BY_NAME: Dict[str, Dict[str, Any]] = {tool["name"]: tool for tool in TOOL_DEFINITIONS}


def public_tool_list() -> List[Dict[str, Any]]:
    return [
        {
            "name": tool["name"],
            "description": tool["description"],
            "inputSchema": tool["inputSchema"],
        }
        for tool in TOOL_DEFINITIONS
    ]


def call_tool(name: str, arguments: Dict[str, Any]) -> Any:
    tool = TOOLS_BY_NAME.get(name)
    if not tool:
        raise ToolError(f'Unknown tool "{name}"')

    handler: Callable[[Dict[str, Any]], Any] = tool["handler"]
    return handler(arguments or {})
