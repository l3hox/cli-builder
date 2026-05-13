"""Synthetic SDK exercising PEP 692 Unpack[TypedDict] patterns.

Modelled after Stripe's class-method surface (`stripe.Customer.list(**params)`):
TypedDicts live in a `params/` sub-package and are imported only under
`if TYPE_CHECKING:`. The runtime module namespace does NOT contain them —
which is what makes `typing.get_type_hints()` fail and forces the adapter
into the AST-walk resolution path.
"""

from .service import (
    BareKwargsService,
    CustomerService,
    InheritanceService,
    PlainParamsService,
    UnresolvableUnpackService,
)

__all__ = [
    "BareKwargsService",
    "CustomerService",
    "InheritanceService",
    "PlainParamsService",
    "UnresolvableUnpackService",
]
