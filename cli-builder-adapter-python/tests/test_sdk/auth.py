"""TestSdk auth types."""

from __future__ import annotations


class ApiKeyCredential:
    """Simulates an API key credential wrapper."""

    def __init__(self, key: str) -> None:
        self.key = key
