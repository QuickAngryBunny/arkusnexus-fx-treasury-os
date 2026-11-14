"""Deterministic dummy company data for internal tenants.

The same tenant slug always produces the same dataset, with stable ids (uuid5), so re-seeding is a no-op.
Nothing here resembles a real company: people come from a fixed name list and use .example email domains.
"""

from __future__ import annotations

import hashlib
import random
import uuid
from dataclasses import dataclass, field

# Fixed namespace so ids are stable across runs and machines.
NAMESPACE = uuid.UUID("6f1c2d3e-8a4b-4c5d-9e0f-1a2b3c4d5e6f")

FIRST_NAMES = [
    "Ana", "Luis", "María", "Jorge", "Sofía", "Diego", "Valeria", "Carlos",
    "Fernanda", "Ricardo", "Daniela", "Andrés", "Lucía", "Miguel", "Paula", "Javier",
]

LAST_NAMES = [
    "García", "Hernández", "López", "Martínez", "Rodríguez", "Pérez", "Sánchez",
    "Ramírez", "Torres", "Flores", "Rivera", "Gómez", "Díaz", "Cruz", "Morales",
]


@dataclass(frozen=True)
class UserProfile:
    id: uuid.UUID
    email: str
    display_name: str
    role: str


@dataclass(frozen=True)
class Dataset:
    slug: str
    users: list[UserProfile] = field(default_factory=list)


def _id(slug: str, kind: str, key: str) -> uuid.UUID:
    return uuid.uuid5(NAMESPACE, f"{slug}:{kind}:{key}")


def _rng(slug: str) -> random.Random:
    seed = int.from_bytes(hashlib.sha256(slug.encode("utf-8")).digest()[:8], "big")
    return random.Random(seed)


def _strip_accents(text: str) -> str:
    table = str.maketrans("áéíóúñÁÉÍÓÚÑ", "aeiounAEIOUN")
    return text.translate(table)


def build_dataset(slug: str, user_count: int = 10) -> Dataset:
    """Build the dummy dataset for one tenant. Pure function: no I/O."""
    if user_count < 1:
        raise ValueError("user_count must be at least 1")

    rng = _rng(slug)

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
            )
        )

    return Dataset(slug=slug, users=users)
