"""Two ambiguous entry-class candidates — both match the single-client
heuristic ('starts with package-capitalized', here 'Ambig...') WITHOUT
matching multi-service suffix rules (no Client/Service/Api ending).

Package name passed to extract() is `ambig` so capitalized is `Ambig` —
both classes start with that.
"""

from __future__ import annotations


class AmbigMain:
    """First candidate — ≥10 methods, starts with `Ambig`, no service suffix."""

    def get_a(self): pass
    def get_b(self): pass
    def get_c(self): pass
    def get_d(self): pass
    def get_e(self): pass
    def list_a(self): pass
    def list_b(self): pass
    def create_a(self): pass
    def create_b(self): pass
    def delete_a(self): pass
    def update_a(self): pass


class AmbigAdmin:
    """Second candidate — also ≥10 methods, also starts with `Ambig`, no suffix."""

    def get_x(self): pass
    def get_y(self): pass
    def get_z(self): pass
    def get_w(self): pass
    def list_x(self): pass
    def list_y(self): pass
    def search_x(self): pass
    def search_y(self): pass
    def create_x(self): pass
    def delete_x(self): pass
    def update_x(self): pass
