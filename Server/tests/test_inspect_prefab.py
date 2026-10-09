"""Tests for the inspect_prefab tool (Python side: validation, param mapping, text passthrough)."""
from __future__ import annotations

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastmcp.exceptions import ToolError

from services.registry import get_registered_tools
from services.tools.inspect_prefab import inspect_prefab

TREE_TEXT = "Car.prefab  6 objs  depth<=2  (shown 4)\nCar [InspectPrefabFixture]\n  Wheel_FL..RR x4 [BoxCollider]"


@pytest.fixture
def unity(monkeypatch):
    captured: dict[str, object] = {}
    reply: dict[str, object] = {"success": True, "message": "ok", "data": {"text": TREE_TEXT}}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return reply

    monkeypatch.setattr(
        "services.tools.inspect_prefab.get_unity_instance_from_context",
        AsyncMock(return_value="unity-1"),
    )
    monkeypatch.setattr("services.tools.inspect_prefab.preflight", AsyncMock(return_value=None))
    monkeypatch.setattr("services.tools.inspect_prefab.send_with_unity_instance", fake_send)
    captured["reply"] = reply
    return captured


def run(**kwargs):
    return asyncio.run(inspect_prefab(SimpleNamespace(), **kwargs))


def test_returns_plain_text(unity):
    text = run(prefab_path="Assets/Car.prefab")
    assert text == TREE_TEXT
    assert unity["tool_name"] == "inspect_prefab"
    assert unity["params"] == {"mode": "tree", "prefab_path": "Assets/Car.prefab"}


def test_maps_every_parameter(unity):
    run(
        mode="node",
        prefab_path="Assets/Car.prefab",
        path="Body/Hood",
        component="Door",
        all_fields=True,
        max_chars="3000",
    )
    assert unity["params"] == {
        "mode": "node",
        "prefab_path": "Assets/Car.prefab",
        "path": "Body/Hood",
        "component": "Door",
        "all_fields": True,
        "max_chars": 3000,
    }


def test_tree_options_and_false_flags_are_dropped(unity):
    run(prefab_path="Assets/Car.prefab", depth=1, root="Body", filter="Light", all_fields=False)
    assert unity["params"] == {
        "mode": "tree",
        "prefab_path": "Assets/Car.prefab",
        "root": "Body",
        "filter": "Light",
        "depth": 1,
    }


def test_usages_needs_script_or_asset(unity):
    with pytest.raises(ToolError, match="script="):
        run(mode="usages")
    run(mode="usages", script="VehicleController")
    assert unity["params"] == {"mode": "usages", "script": "VehicleController"}


def test_other_modes_need_prefab_path(unity):
    with pytest.raises(ToolError, match="prefab_path"):
        run(mode="refs")


def test_unknown_mode_is_rejected(unity):
    with pytest.raises(ToolError, match="Valid modes"):
        run(mode="everything", prefab_path="Assets/Car.prefab")


def test_unity_error_becomes_tool_error(unity):
    unity["reply"].clear()
    unity["reply"].update({"success": False, "error": "No object 'Body/Hod' in Car. Did you mean: Body/Hood?"})
    with pytest.raises(ToolError, match="Did you mean: Body/Hood"):
        run(mode="node", prefab_path="Assets/Car.prefab", path="Body/Hod")


def test_busy_editor_is_reported(unity, monkeypatch):
    busy = SimpleNamespace(model_dump=lambda: {"success": False, "message": "Unity is compiling; retry."})
    monkeypatch.setattr("services.tools.inspect_prefab.preflight", AsyncMock(return_value=busy))
    with pytest.raises(ToolError, match="compiling"):
        run(prefab_path="Assets/Car.prefab")


def test_registration_is_read_only_core_text_tool():
    entry = next(t for t in get_registered_tools() if t["name"] == "inspect_prefab")
    assert entry["group"] == "core"
    annotations = entry["kwargs"]["annotations"]
    assert annotations.readOnlyHint is True
    assert annotations.destructiveHint is False
    assert entry["kwargs"]["output_schema"] is None
