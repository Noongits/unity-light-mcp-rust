"""Capture the Python reference API. Run with Server's dependencies installed.

PYTHONPATH=Server/src python ServerRust/tests/capture_contract.py
Generated snapshots are runtime data for Rust, never Python runtime dependencies.
"""
import asyncio
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Server/src'))
os.environ.setdefault('DISABLE_TELEMETRY', 'true')
os.environ.setdefault('UNITY_MCP_LOG_DIR', '/tmp/unity-contract-logs')

async def main():
    from fastmcp import FastMCP
    from services.tools import register_all_tools
    from services.resources import register_all_resources
    from services.registry import get_registered_tools, get_registered_resources
    from core.config import config
    config.transport_mode = 'stdio'
    server = FastMCP('unity-mcp-light')
    register_all_tools(server, project_scoped_tools=True)
    register_all_resources(server, project_scoped_tools=True)
    definitions = {x['name']: x for x in get_registered_tools()}
    tools = []
    for tool in await server.list_tools():
        info = definitions[tool.name]
        item = tool.to_mcp_tool().model_dump(mode='json', exclude_none=True, by_alias=True)
        item['group'] = info['group']
        item['unity_target'] = info['unity_target']
        tools.append(item)
    resources = []
    for r in await server.list_resources():
        resources.append(r.to_mcp_resource().model_dump(mode='json', exclude_none=True, by_alias=True))
    for r in await server.list_resource_templates():
        resources.append(r.to_mcp_template().model_dump(mode='json', exclude_none=True, by_alias=True))
    out = ROOT / 'ServerRust/contracts'
    out.mkdir(parents=True, exist_ok=True)
    for name, items in [('tools', tools), ('resources', resources)]:
        (out / f'{name}.json').write_text(json.dumps(sorted(items, key=lambda x:x['name']), indent=2, ensure_ascii=False)+'\n')
    from services.resources.gameobject import get_gameobject_api_docs
    from services.resources.prefab import get_prefab_api_docs
    from services.resources.tool_groups import get_tool_groups
    static = {
        'mcpforunity://scene/gameobject-api': (await get_gameobject_api_docs(None)).model_dump(mode='json'),
        'mcpforunity://prefab-api': (await get_prefab_api_docs(None)).model_dump(mode='json'),
        'mcpforunity://tool-groups': await get_tool_groups(None),
    }
    (out / 'static_resources.json').write_text(json.dumps(static, indent=2, ensure_ascii=False)+'\n')
    import ast
    import importlib
    from models import MCPResponse
    from services.resources.editor_state import EditorStateData
    response_schemas = {}
    for definition in get_registered_resources():
        func = definition['func']
        while hasattr(func, '__wrapped__'):
            func = func.__wrapped__
        module = importlib.import_module(func.__module__)
        tree = ast.parse(Path(module.__file__).read_text())
        cls = MCPResponse
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == 'parse_resource_response' and len(node.args) > 1 and isinstance(node.args[1], ast.Name):
                cls = getattr(module, node.args[1].id)
        response_schemas[definition['uri']] = cls.model_json_schema()
    response_schemas['editor_state_data'] = EditorStateData.model_json_schema()
    (out / 'resource_response_schemas.json').write_text(json.dumps(response_schemas, indent=2, ensure_ascii=False)+'\n')
    from main import _build_instructions
    (out / 'instructions.json').write_text(json.dumps({'project_scoped':_build_instructions(True),'unscoped':_build_instructions(False)},indent=2)+'\n')
    print(f'Captured {len(tools)} tools, {len(resources)} resources/templates')

if __name__ == '__main__':
    asyncio.run(main())
