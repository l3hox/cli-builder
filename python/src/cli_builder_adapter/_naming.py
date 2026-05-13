"""Naming policy for single-client SDK discovery (Step 18 / ADR-023).

Defines the verb vocabulary the adapter recognizes as CLI-worthy and the
prefix patterns that mark a method's noun as descriptive (not a resource).
Imported by `extractor.py` and any future sub-resource walker (Step 19+).

Module identity: naming policy, not string mechanics. `_utils.py` houses
structural utilities (`SERVICE_SUFFIXES`, `class_to_noun`, `pascal_to_kebab`);
this module houses semantic classification (what's a verb? what's a noun?).
"""

from __future__ import annotations

VERB_WHITELIST: frozenset[str] = frozenset({
    "get", "list", "create", "update", "delete",
    "search", "find", "retrieve",
})

# Methods whose noun starts with these prefixes are skipped — the "noun"
# is descriptive (modifier), not a resource name. Examples:
#   create_from_raw_data  → "from_raw_data" is descriptive
#   convert_to_dict       → "to_dict" is descriptive
DESCRIPTIVE_NOUN_PREFIXES: tuple[str, ...] = ("from_", "to_", "with_", "for_")

# Minimum public method count for a class to be considered a single-client
# entry candidate. Calibrated for PyGithub (Github: 40 methods) and similar
# SDKs. Smaller utility classes with <10 methods are filtered out.
MIN_ENTRY_CLASS_METHODS: int = 10


def parse_verb_noun(method_name: str) -> tuple[str, str] | None:
    """Parse a method name into (verb, noun) per single-client conventions.

    Returns None if the method should be skipped (caller emits CB610 with reason).

    Rules:
      1. Skip methods without `_` (no verb_noun split possible).
      2. Skip methods whose verb is not in VERB_WHITELIST.
      3. Skip methods whose noun starts with any DESCRIPTIVE_NOUN_PREFIXES entry.

    The `type[T]` first-param filter (rule 4) is the caller's responsibility —
    it requires parameter-level inspection that doesn't belong in pure naming.
    """
    if "_" not in method_name:
        return None  # rule 1
    verb, _, noun = method_name.partition("_")
    if verb not in VERB_WHITELIST:
        return None  # rule 2
    if any(noun.startswith(p) for p in DESCRIPTIVE_NOUN_PREFIXES):
        return None  # rule 3
    return verb, noun


def skip_reason(method_name: str) -> str:
    """Human-readable reason why a method was skipped per parse_verb_noun rules.

    Used in CB610 diagnostic messages. Mirrors parse_verb_noun's filter order
    so reasons stay stable across refactors. The `type[T]` reason (rule 4) is
    the caller's responsibility and is NOT covered here.

    Returns an empty string if the method should NOT be skipped — callers
    should check parse_verb_noun()'s None return before calling this.
    """
    if "_" not in method_name:
        return "no underscore — cannot split into verb_noun"
    verb, _, noun = method_name.partition("_")
    if verb not in VERB_WHITELIST:
        return f"verb '{verb}' not in whitelist"
    if any(noun.startswith(p) for p in DESCRIPTIVE_NOUN_PREFIXES):
        return "descriptive noun prefix (starts with from_/to_/with_/for_)"
    return ""


def noun_to_resource_name(noun: str) -> str:
    """Convert a method-name noun to a CLI-style resource name.

    `repo` → `repo`. `pull_request` → `pull-request`. `users` stays `users`
    (singular/plural NOT normalized — see ADR-023).
    """
    return noun.replace("_", "-").lower()
