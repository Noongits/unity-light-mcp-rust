from services.tools import compact_schema


def test_optional_scalar_collapses_to_plain_type():
    schema = {
        "properties": {
            "name": {
                "anyOf": [{"type": "string"}, {"type": "null"}],
                "default": None,
                "description": "Object name",
            }
        }
    }
    assert compact_schema(schema) == {
        "properties": {"name": {"type": "string", "description": "Object name"}}
    }


def test_optional_union_keeps_remaining_branches():
    prop = {"anyOf": [{"type": "integer"}, {"type": "string"}, {"type": "null"}], "default": None}
    assert compact_schema(prop) == {"anyOf": [{"type": "integer"}, {"type": "string"}]}


def test_nested_schemas_are_compacted():
    prop = {
        "anyOf": [
            {"type": "array", "items": {"anyOf": [{"type": "number"}, {"type": "null"}]}},
            {"type": "null"},
        ]
    }
    assert compact_schema(prop) == {"type": "array", "items": {"type": "number"}}


def test_type_list_drops_null():
    assert compact_schema({"type": ["string", "null"]}) == {"type": "string"}


def test_non_null_defaults_and_required_params_are_untouched():
    schema = {
        "properties": {
            "action": {"enum": ["get", "set"], "type": "string"},
            "count": {"type": "integer", "default": 10},
        },
        "required": ["action"],
    }
    assert compact_schema(schema) == schema

