"""Guard the Light fork's tool set when merging upstream releases."""
from pathlib import Path

from services.registry import get_registered_tools
from utils.module_discovery import discover_modules
import services.tools as tools_package


def test_upstream_sync_preserves_light_tools():
    list(discover_modules(Path(tools_package.__file__).parent, tools_package.__package__))
    names = {tool["name"] for tool in get_registered_tools()}
    assert {"inspect_prefab", "blender_bridge", "import_model_file"} <= names
    assert names.isdisjoint({
        "generate_image", "generate_model", "generate_audio", "import_model",
        "manage_probuilder", "manage_profiler", "manage_vfx", "manage_animation",
        "manage_packages", "unity_docs", "debug_request_context", "manage_script_capabilities",
    })
    assert len(names) == 38
