"""Return types — no methods worth surfacing (sub-resource discovery is Step 19+)."""

from dataclasses import dataclass


@dataclass
class Repo:
    name: str
    private: bool = False


@dataclass
class User:
    login: str
    email: str | None = None
