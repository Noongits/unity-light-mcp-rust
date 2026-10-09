"""Keep generated type documentation stable across supported Python versions."""
import importlib.util
from pathlib import Path
import sys
from typing import Annotated, Union

spec = importlib.util.spec_from_file_location(
    "docs_generator", Path(__file__).resolve().parents[1] / "generate_docs_reference.py"
)
generator = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = generator
spec.loader.exec_module(generator)


def test_pep604_and_typing_unions_render_identically():
    assert generator._render_type(list | float | None) == "list[Any] | float | None"
    assert generator._render_type(list | float | None) == generator._render_type(Union[list, float, None])
    assert generator._render_type(dict | bool | None) == "dict[Any] | bool | None"


def test_optional_annotated_description_is_preserved():
    assert generator._annotation_description(Annotated[str, "Asset path"] | None) == "Asset path"
