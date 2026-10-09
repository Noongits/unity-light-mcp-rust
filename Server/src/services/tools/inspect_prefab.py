"""Read-only prefab inspector that answers in compact plain text instead of JSON."""
from typing import Annotated, Any, Literal

from fastmcp import Context
from fastmcp.exceptions import ToolError
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
from services.tools.utils import coerce_bool, coerce_int
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance

MODES = ("tree", "node", "refs", "overrides", "usages", "problems")


@mcp_for_unity_tool(
    description=(
        "Read-only prefab inspector with compact text output; prefer it over manage_prefabs get_hierarchy. "
        "Usual path: manage_prefabs get_info -> tree -> node or refs. "
        "tree: hierarchy (depth=2, root=subtree, filter=name/component). "
        "node: one object's non-default fields and listeners (path, component, all_fields). "
        "refs: object references and UnityEvent listeners, by object. "
        "overrides: variant and nested-prefab changes. "
        "usages: prefabs/scenes using script=Class or asset=path. "
        "problems: missing scripts/refs, broken prefab links, event and material issues (prefab or folder)."
    ),
    annotations=ToolAnnotations(
        title="Inspect Prefab",
        readOnlyHint=True,
        destructiveHint=False,
        idempotentHint=True,
        openWorldHint=False,
    ),
    # Plain text only: no structured copy of the same payload.
    output_schema=None,
)
async def inspect_prefab(
    ctx: Context,
    mode: Annotated[Literal["tree", "node", "refs", "overrides", "usages", "problems"], "What to show."] = "tree",
    prefab_path: Annotated[str | None, "Assets/...prefab, a folder (problems), or scene:Path/To/Object."] = None,
    path: Annotated[str | None, "node: object path in the prefab."] = None,
    component: Annotated[str | None, "node/refs: component type."] = None,
    root: Annotated[str | None, "tree/refs: only this subtree."] = None,
    depth: Annotated[int | None, "tree: depth, default 2."] = None,
    filter: Annotated[str | None, "tree: name or component substring."] = None,
    all_fields: Annotated[bool | None, "node: include default values."] = None,
    script: Annotated[str | None, "usages: class name."] = None,
    asset: Annotated[str | None, "usages: asset path."] = None,
    max_chars: Annotated[int | None, "Output budget, default 6000."] = None,
) -> str:
    mode_value = (mode or "tree").lower()
    if mode_value not in MODES:
        raise ToolError(f"Unknown mode '{mode}'. Valid modes: {', '.join(MODES)}.")
    if mode_value == "usages":
        if not script and not asset:
            raise ToolError("usages needs script= (class name) or asset= (Assets/... path).")
    elif not prefab_path:
        raise ToolError(f"{mode_value} needs prefab_path (Assets/...prefab or scene:Path/To/Object).")

    gate = await preflight(ctx, wait_for_no_compile=True)
    if gate is not None:
        raise ToolError(_message(gate.model_dump()) or "Unity is busy; retry shortly.")

    params: dict[str, Any] = {"mode": mode_value}
    for key, value in (
        ("prefab_path", prefab_path),
        ("path", path),
        ("component", component),
        ("root", root),
        ("filter", filter),
        ("script", script),
        ("asset", asset),
    ):
        if value is not None:
            params[key] = value
    for key, value in (("depth", depth), ("max_chars", max_chars)):
        number = coerce_int(value)
        if number is not None:
            params[key] = number
    if coerce_bool(all_fields):
        params["all_fields"] = True

    unity_instance = await get_unity_instance_from_context(ctx)
    response = await send_with_unity_instance(
        async_send_command_with_retry, unity_instance, "inspect_prefab", params
    )
    if hasattr(response, "model_dump"):
        response = response.model_dump()
    if not isinstance(response, dict):
        raise ToolError(f"Unexpected response from Unity: {response!r}")
    if not response.get("success"):
        raise ToolError(_message(response) or "inspect_prefab failed.")

    data = response.get("data")
    text = data.get("text") if isinstance(data, dict) else None
    if not isinstance(text, str):
        raise ToolError("Unity returned no text; update the Unity MCP Light package.")
    return text


def _message(response: dict[str, Any]) -> str | None:
    for key in ("error", "message"):
        value = response.get(key)
        if isinstance(value, str) and value:
            return value
    return None
