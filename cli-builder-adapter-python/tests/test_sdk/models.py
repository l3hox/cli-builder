"""TestSdk model types — mirrors the .NET TestSdk models."""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import Enum
from typing import Optional


@dataclass
class Address:
    line1: str
    line2: Optional[str] = None
    city: Optional[str] = None


@dataclass
class Customer:
    id: str
    email: str
    name: Optional[str] = None


@dataclass
class Order:
    id: str
    amount: float
    name: Optional[str] = None
    shipping_address: Optional[Address] = None


@dataclass
class Product:
    id: str
    name: str


class CustomerStatus(Enum):
    ACTIVE = "active"
    INACTIVE = "inactive"
    SUSPENDED = "suspended"


# Options classes (mirror .NET CreateCustomerOptions, etc.)
@dataclass
class CreateCustomerOptions:
    email: str
    name: Optional[str] = None
    phone: Optional[str] = None
    preferred_contact: bool = False
    credit_limit: Optional[int] = None
    initial_status: Optional[CustomerStatus] = None


@dataclass
class CreateOrderOptions:
    customer_id: str
    product_id: str
    amount: float
    description: Optional[str] = None
    is_priority: bool = False


@dataclass
class SendMessageOptions:
    model: Optional[str] = None
    temperature: Optional[float] = None


# Abstract message type (mirrors .NET Message with JsonDerivedType)
class Message(ABC):
    content: str = ""


class UserMessage(Message):
    pass


class SystemMessage(Message):
    pass
