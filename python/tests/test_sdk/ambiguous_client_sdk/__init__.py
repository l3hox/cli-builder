"""Two ≥10-method classes that both match the entry-class heuristic.

Used by `test_entry_class_ambiguous_emits_cb609` to verify the adapter
emits CB609 + None when single-client auto-detection finds multiple
candidates. Both classes end in `Client` and have ≥10 public methods.
"""

from ._classes import AmbigAdmin, AmbigMain

__all__ = ["AmbigAdmin", "AmbigMain"]
