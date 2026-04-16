"""Shared utilities for extractor and stub parser."""

from __future__ import annotations

import re

# Service class name suffixes (same as .NET adapter)
SERVICE_SUFFIXES = ("Client", "Service", "Api")

# CRUD classmethod names that indicate a resource class (e.g., stripe.Customer)
RESOURCE_CRUD_METHODS = {"create", "retrieve", "list", "delete"}


def class_to_noun(class_name: str) -> str:
    """Convert class name to CLI noun: CustomerClient -> customer."""
    for suffix in SERVICE_SUFFIXES:
        if class_name.endswith(suffix) and len(class_name) > len(suffix):
            class_name = class_name[:-len(suffix)]
            break
    return pascal_to_kebab(class_name)


def pascal_to_kebab(name: str) -> str:
    """Convert PascalCase to kebab-case."""
    s = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1-\2", name)
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1-\2", s)
    return s.lower()
