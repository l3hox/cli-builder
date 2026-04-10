"""TestSdk service classes — mirrors the .NET TestSdk services."""

from __future__ import annotations

from typing import Optional

from .models import (
    CreateCustomerOptions,
    CreateOrderOptions,
    Customer,
    Message,
    Order,
    SendMessageOptions,
)


class CustomerClient:
    """Standard service with api_key auth — matches .NET CustomerService."""

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def get(self, id: str) -> Customer:
        return Customer(id=id, email="test@example.com")

    def list(self, limit: int = 10) -> list[Customer]:
        return [Customer(id="cust_001", email="a@b.com"), Customer(id="cust_002", email="c@d.com")]

    def create(self, options: CreateCustomerOptions) -> Customer:
        return Customer(id="cust_new", email=options.email, name=options.name)

    def delete(self, id: str) -> None:
        pass


class OrderClient:
    """Service with direct list param — matches .NET OrderClient."""

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def get(self, id: str) -> Order:
        return Order(id=id, amount=99.99)

    def create(self, options: CreateOrderOptions) -> Order:
        return Order(id="ord_001", amount=options.amount)


class MessageClient:
    """Service with abstract type direct param — matches .NET MessageClient."""

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def send(
        self,
        messages: list[Message],
        options: Optional[SendMessageOptions] = None,
    ) -> Order:
        model_str = f" with model {options.model}" if options and options.model else ""
        return Order(id="msg_001", name=f"Sent {len(messages)} messages{model_str}")

    def batch(self, ids: list[str]) -> Order:
        return Order(id="batch_001", name=",".join(ids))
