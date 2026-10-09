"""Model import CLI command (local model files).

Thin pass-through to Unity over HTTP: the command sends only the file path;
the C# side copies the file under Assets/ and imports it.
"""

import click

from cli.utils.config import get_config
from cli.utils.output import format_output
from cli.utils.connection import run_command, handle_unity_errors


@click.group(name="asset-gen")
def asset_gen():
    """Model import - import local model files into the project."""
    pass


@asset_gen.command("import-model-file")
@click.option("--source-path", "source_path", required=True,
              help="Path to a local model file (.fbx/.obj/.glb/.gltf/.zip).")
@click.option("--name", default=None, help="Base name for the imported asset.")
@click.option("--output-folder", default=None, help="Destination folder under Assets/.")
@click.option("--target-size", default=None, type=float, help="Normalize largest dimension (meters).")
@click.option("--animation-type", "animation_type", default=None,
              type=click.Choice(["none", "generic", "humanoid", "legacy"]),
              help="FBX/OBJ rig mode: generic/humanoid surface animation clips; "
                   "legacy selects Unity's legacy Animation system (glTF ignores this).")
@handle_unity_errors
def import_model_file(source_path, name, output_folder, target_size, animation_type):
    """Import a local 3D model file (e.g. a Blender export) into the Unity project."""
    config = get_config()
    params = {
        "sourcePath": source_path,
        "name": name,
        "outputFolder": output_folder,
        "targetSize": target_size,
        "animationType": animation_type,
    }
    params = {k: v for k, v in params.items() if v is not None}
    result = run_command("import_model_file", params, config)
    click.echo(format_output(result, config.format))
