"""Serialize SdkMetadata to JSON matching the .NET SdkMetadataJson.Options format.

Produces camelCase keys, enum values as camelCase strings, indented JSON,
null for None (not absent). Includes schemaVersion in envelope.
"""

from __future__ import annotations

import dataclasses
import json
import re
from enum import Enum
from typing import Any

SCHEMA_VERSION = "1"


def _to_camel_case(name: str) -> str:
    """Convert snake_case to camelCase."""
    parts = name.split("_")
    return parts[0] + "".join(p.capitalize() for p in parts[1:])


def _serialize_value(value: Any) -> Any:
    """Recursively serialize a value to a JSON-compatible structure with camelCase keys."""
    if value is None:
        return None
    if isinstance(value, Enum):
        return value.value
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float, str)):
        return value
    if isinstance(value, list):
        return [_serialize_value(item) for item in value]
    if dataclasses.is_dataclass(value) and not isinstance(value, type):
        result = {}
        for f in dataclasses.fields(value):
            key = _to_camel_case(f.name)
            result[key] = _serialize_value(getattr(value, f.name))
        return result
    return str(value)


def serialize_adapter_result(result: Any) -> str:
    """Serialize an AdapterResult to JSON with schemaVersion envelope.

    Output format:
    {
      "schemaVersion": "1",
      "metadata": { ... },
      "diagnostics": [ ... ]
    }
    """
    envelope = {
        "schemaVersion": SCHEMA_VERSION,
        "metadata": _serialize_value(result.metadata),
        "diagnostics": _serialize_value(result.diagnostics),
    }
    return json.dumps(envelope, indent=2, ensure_ascii=False)


def serialize_metadata(metadata: Any) -> str:
    """Serialize SdkMetadata to JSON (without envelope, for testing)."""
    return json.dumps(_serialize_value(metadata), indent=2, ensure_ascii=False)
