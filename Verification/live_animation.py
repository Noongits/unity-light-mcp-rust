"""Verify restored animation through native Rust in an explicitly owned ReviewBridge project.

Requires MCP SDK 2.x and REVIEW_ANIMATION_PROJECT, REVIEW_ANIMATION_BINARY,
UNITY_MCP_STATUS_DIR, REVIEW_EVIDENCE. Run against a fresh disposable project.
"""
import asyncio
import json
import os
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

PROJECT = Path(os.environ["REVIEW_ANIMATION_PROJECT"]).resolve()
BINARY = Path(os.environ["REVIEW_ANIMATION_BINARY"]).resolve()
STATUS = Path(os.environ["UNITY_MCP_STATUS_DIR"])
OUT = Path(os.environ["REVIEW_EVIDENCE"])
records = []


def record(step, result):
    records.append({"step": step, "result": result})
    (OUT / "live-animation.json").write_text(json.dumps(records, indent=2))
    print(step, flush=True)


async def main():
    files = list(STATUS.glob("unity-mcp-status-*.json"))
    assert len(files) == 1, files
    status = json.loads(files[0].read_text())
    assert Path(status["project_path"]).resolve() in (PROJECT, PROJECT / "Assets"), status
    instance = status["project_name"] + "@" + files[0].stem.removeprefix("unity-mcp-status-")
    params = StdioServerParameters(command=str(BINARY), args=["--transport", "stdio", "--project-scoped-tools"],
                                   env={**os.environ, "DISABLE_TELEMETRY": "true"})
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            record("initialize", init.model_dump(mode="json", by_alias=True))
            assert init.server_info.name == "unity-mcp-light"

            async def call(name, args):
                result = await session.call_tool(name, args)
                data = result.structured_content
                if data is None:
                    data = json.loads(next(c.text for c in result.content if c.type == "text"))
                if set(data) == {"result"}:
                    data = data["result"]
                record(name, data)
                assert not result.is_error and data.get("success", True), data
                return data

            await call("set_active_instance", {"instance": instance})
            info = (await session.read_resource("mcpforunity://project/info")).model_dump(mode="json", by_alias=True)
            record("project/info", info)
            assert PROJECT.name in json.dumps(info)
            listed = await session.list_tools()
            record("tools/list", {"names": [t.name for t in listed.tools]})
            assert "manage_animation" in {t.name for t in listed.tools}
            await call("manage_gameobject", {"action": "create", "name": "AnimationLiveTarget"})
            await call("manage_gameobject", {"action": "create", "name": "Visual", "parent": "AnimationLiveTarget"})
            clip = "Assets/AnimationLive/Move.anim"
            controller = "Assets/AnimationLive/Actor.controller"

            async def animation(action, properties=None, **fields):
                return await call("manage_animation", {"action": action, "clip_path": clip,
                                                       "controller_path": controller,
                                                       "properties": properties, **fields})

            await animation("clip_create", {"length": 1, "loop": True})
            await animation("clip_set_curve", {"type": "Transform", "relative_path": "Visual",
                                               "property_path": "m_LocalPosition.x", "keys": [[0, 0], [1, 2]]})
            await animation("controller_create")
            await animation("controller_add_parameter", {"parameter_name": "Speed", "parameter_type": "float"})
            await animation("controller_add_state", {"state_name": "Move", "is_default": True})
            await animation("controller_create_blend_tree_1d", {"state_name": "Locomotion", "blend_parameter": "Speed"})
            await animation("controller_add_blend_tree_child", {"state_name": "Locomotion", "threshold": 1})
            await animation("controller_assign", target="AnimationLiveTarget", search_method="by_name")
            await animation("animator_set_parameter", {"parameter_name": "Speed", "parameter_type": "float", "value": 2},
                            target="AnimationLiveTarget", search_method="by_name")
            clip_info = await animation("clip_get_info")
            assert clip_info["data"]["curveCount"] > 0
            await animation("controller_get_info")
            await call("manage_editor", {"action": "play"})
            try:
                # Only readiness reads may be retried across Unity's Play Mode reload.
                before = None
                for _ in range(60):
                    await asyncio.sleep(0.5)
                    try:
                        before = await animation("animator_get_info", target="AnimationLiveTarget", search_method="by_name")
                        if before["data"]["layers"] and before["data"]["layers"][0]["currentStateNormalizedTime"] > 0:
                            break
                    except Exception as exc:
                        record("play-readiness", {"error": str(exc)})
                else:
                    raise AssertionError("Animator did not begin evaluating in Play Mode")
                await animation("animator_play", {"state_name": "Move", "layer": 0},
                                target="AnimationLiveTarget", search_method="by_name")
                await animation("animator_set_speed", {"speed": 1.5}, target="AnimationLiveTarget", search_method="by_name")
                await asyncio.sleep(0.7)
                after = await animation("animator_get_info", target="AnimationLiveTarget", search_method="by_name")
                assert after["data"]["layers"][0]["currentStateNormalizedTime"] > 0
                assert after["data"]["speed"] == 1.5
                record("playback-verified", {"state_hash": after["data"]["layers"][0]["currentStateHash"],
                                              "normalized_time": after["data"]["layers"][0]["currentStateNormalizedTime"]})
            finally:
                await call("manage_editor", {"action": "stop"})


if __name__ == "__main__":
    asyncio.run(main())
