"""Github — single-client SDK shape fixture for Step 18 tests.

Method inventory designed to exercise:
- All whitelisted verbs (get/list/create/update/delete/search/find/retrieve)
- Multi-word noun (get_pull_request → pull-request resource)
- Singular vs plural NOT normalized (get_repo and list_repos → repo + repos)
- `@classmethod` and async-def method handling
- CB610 skip reasons:
    - no underscore (close, withLazy)
    - verb not in whitelist (render_markdown)
    - descriptive noun (create_from_raw_data — also exercises type[T] filter)
    - first param type[T] (register_class)

Constructor has a `login_or_token` parameter so the auth detector test in
PR 2 can validate the heuristic against a realistic ctor shape.

NOTE: deliberately NO `from __future__ import annotations`. With future
annotations on, `klass: type` becomes the string `"type"` — the `type[T]`
filter test cannot then verify the bare-type annotation case at runtime.
Real SDKs like PyGithub don't use future annotations either (we confirmed
during Step 17), so this fixture mirrors real-world conditions.
"""

from ._types import Repo, User


class Github:
    """Synthetic single-client SDK fixture mirroring PyGithub's shape."""

    def __init__(
        self,
        login_or_token: str | None = None,
        password: str | None = None,
        base_url: str = "https://api.github.com",
        timeout: int = 15,
    ) -> None:
        self._token = login_or_token

    # ---- Whitelisted verbs across repo resource (singular) ----

    def get_repo(self, name: str) -> Repo:
        """Get a repository by full name."""
        return Repo(name=name)

    def create_repo(self, name: str, private: bool = False) -> Repo:
        """Create a new repository."""
        return Repo(name=name, private=private)

    def update_repo(self, name: str, description: str | None = None) -> Repo:
        return Repo(name=name)

    def delete_repo(self, name: str) -> None:
        return None

    # ---- list_repos exercises the plural-distinct-resource case ----

    def list_repos(self, user: str) -> list[Repo]:
        """List user's repositories."""
        return []

    # ---- Multi-word noun (pull-request) ----

    def get_pull_request(self, repo: str, number: int) -> dict:
        """Get a pull request."""
        return {}

    # ---- search_X verb ----

    def search_repositories(self, query: str, sort: str = "stars") -> list[Repo]:
        """Search repositories by query."""
        return []

    # ---- Verbs `get`, `retrieve`, `find` (all in whitelist, separate ops on `user` resource) ----

    def get_user(self, login: str) -> User:
        """Get a user."""
        return User(login=login)

    def retrieve_user(self, login: str) -> User:
        """Retrieve a user — exercises verb-not-canonicalized behavior."""
        return User(login=login)

    @classmethod
    def find_user(cls, login: str) -> User:
        """Classmethod — exercises classmethod-counting + extraction."""
        return User(login=login)

    # ---- Async method on `organizations` resource ----

    async def list_organizations(self) -> list[dict]:
        """Async def — must NOT be silently skipped by method counter."""
        return []

    # ---- Methods that should be SKIPPED with CB610 ----

    def close(self) -> None:
        """Skipped: no underscore."""
        return None

    def withLazy(self) -> "Github":  # noqa: N802 — intentional camelCase
        """Skipped: no underscore."""
        return self

    def render_markdown(self, text: str) -> str:
        """Skipped: verb 'render' not in whitelist."""
        return text

    def create_from_raw_data(self, klass: type, data: dict) -> object:
        """Skipped: descriptive noun (rule 3 fires before rule 4)."""
        return klass()

    def register_class(self, klass: type, name: str) -> None:
        """Skipped: first param is type[T] (rule 4 — pure type[T] filter test).

        verb=register is not in whitelist, but rule 2 (whitelist) fires first
        in parse_verb_noun. To isolate the type[T] filter for a dedicated test,
        we use a whitelisted verb so rules 1-3 pass and only rule 4 catches it.
        """
        return None

    def create_repo_for(self, klass: type, name: str) -> Repo:
        """Skipped: first param is type[T] AND descriptive noun starts with for_.

        Both rules 3 and 4 apply; rule 3 fires first per parse_verb_noun. To
        isolate type[T] cleanly we rely on `get_template` below instead.
        """
        return Repo(name=name)

    def get_template(self, klass: type, name: str) -> dict:
        """Skipped: whitelisted verb, non-descriptive noun → only rule 4 (type[T]) applies."""
        return {}
