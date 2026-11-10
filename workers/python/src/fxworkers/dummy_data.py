"""Deterministic dummy company data for internal tenants.

The same tenant slug always produces the same dataset, with stable ids (uuid5), so re-seeding is a no-op.
Nothing here resembles a real company: names come from a fixed list and tax ids start with "DUMMY".
"""

from __future__ import annotations

import hashlib
import random
import uuid
from dataclasses import dataclass, field

# Fixed namespace so ids are stable across runs and machines.
NAMESPACE = uuid.UUID("6f1c2d3e-8a4b-4c5d-9e0f-1a2b3c4d5e6f")

DEPARTMENTS = [
    "Finanzas",
    "Tesorería",
    "Ingeniería",
    "Operaciones",
    "Recursos Humanos",
    "Ventas",
    "Entrega de proyectos",
]

FIRST_NAMES = [
    "Ana", "Luis", "María", "Jorge", "Sofía", "Diego", "Valeria", "Carlos",
    "Fernanda", "Ricardo", "Daniela", "Andrés", "Lucía", "Miguel", "Paula", "Javier",
]

LAST_NAMES = [
    "García", "Hernández", "López", "Martínez", "Rodríguez", "Pérez", "Sánchez",
    "Ramírez", "Torres", "Flores", "Rivera", "Gómez", "Díaz", "Cruz", "Morales",
]


@dataclass(frozen=True)
class LegalEntity:
    id: uuid.UUID
    name: str
    tax_id: str
    country: str
    functional_currency: str


@dataclass(frozen=True)
class Department:
    id: uuid.UUID
    name: str
    legal_entity_id: uuid.UUID


@dataclass(frozen=True)
class UserProfile:
    id: uuid.UUID
    email: str
    display_name: str
    role: str
    department_id: uuid.UUID


@dataclass(frozen=True)
class Dataset:
    slug: str
    legal_entities: list[LegalEntity] = field(default_factory=list)
    departments: list[Department] = field(default_factory=list)
    users: list[UserProfile] = field(default_factory=list)


def _id(slug: str, kind: str, key: str) -> uuid.UUID:
    return uuid.uuid5(NAMESPACE, f"{slug}:{kind}:{key}")


def _rng(slug: str) -> random.Random:
    seed = int.from_bytes(hashlib.sha256(slug.encode("utf-8")).digest()[:8], "big")
    return random.Random(seed)


def _strip_accents(text: str) -> str:
    table = str.maketrans("áéíóúñÁÉÍÓÚÑ", "aeiounAEIOUN")
    return text.translate(table)


def build_dataset(slug: str, company_name: str, user_count: int = 10) -> Dataset:
    """Build the dummy dataset for one tenant. Pure function: no I/O."""
    if user_count < 1:
        raise ValueError("user_count must be at least 1")

    rng = _rng(slug)
    base = company_name.strip() or slug.replace("_", " ").title()

    mx = LegalEntity(
        id=_id(slug, "legal_entity", "mx"),
        name=f"{base} S.A. de C.V.",
        tax_id=f"DUMMY-MX-{rng.randrange(10**8):08d}",
        country="MX",
        functional_currency="MXN",
    )
    us = LegalEntity(
        id=_id(slug, "legal_entity", "us"),
        name=f"{base} LLC",
        tax_id=f"DUMMY-US-{rng.randrange(10**8):08d}",
        country="US",
        functional_currency="USD",
    )

    # Mexican entity runs most departments; the US entity books sales.
    mx_departments = rng.sample([d for d in DEPARTMENTS if d != "Ventas"], k=4)
    departments = [Department(_id(slug, "department", f"mx:{n}"), n, mx.id) for n in sorted(mx_departments)]
    departments.append(Department(_id(slug, "department", "us:Ventas"), "Ventas", us.id))

    domain = f"{slug.replace('_', '-')}.example"
    users: list[UserProfile] = []
    used_emails: set[str] = set()
    for i in range(user_count):
        first, last = rng.choice(FIRST_NAMES), rng.choice(LAST_NAMES)
        local = _strip_accents(f"{first}.{last}").lower()
        email = f"{local}@{domain}"
        suffix = 2
        while email in used_emails:
            email = f"{local}{suffix}@{domain}"
            suffix += 1
        used_emails.add(email)
        users.append(
            UserProfile(
                id=_id(slug, "user", email),
                email=email,
                display_name=f"{first} {last}",
                # First user is always an admin so every tenant can manage settings once roles exist.
                role="admin" if i == 0 or rng.random() < 0.15 else "standard",
                department_id=rng.choice(departments).id,
            )
        )

    return Dataset(slug=slug, legal_entities=[mx, us], departments=departments, users=users)
