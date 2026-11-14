import uuid

import pytest

from fxworkers.control_plane import Tenant
from fxworkers.dummy_data import build_dataset
from fxworkers.seed import NotADummyTenantError, seed_tenant


def _tenant(slug: str, is_dummy: bool) -> Tenant:
    return Tenant(
        id=uuid.uuid4(), slug=slug, display_name=slug, status="active", is_dummy=is_dummy, server_key="default",
        database_name=f"fx_tenant_{slug}", login_role=f"fx_tenant_{slug}", secret_ref=f"tenant/{slug}",
    )


def test_same_slug_gives_identical_dataset():
    assert build_dataset("demo_norte") == build_dataset("demo_norte")


def test_different_tenants_share_no_ids_or_emails():
    a = build_dataset("demo_norte")
    b = build_dataset("demo_bajio")

    assert {u.id for u in a.users}.isdisjoint({u.id for u in b.users})
    assert {u.email for u in a.users}.isdisjoint({u.email for u in b.users})


def test_emails_are_unique_within_a_tenant():
    ds = build_dataset("demo_norte", user_count=25)
    assert len({u.email for u in ds.users}) == len(ds.users)


def test_data_is_obviously_dummy_and_valid_for_the_schema():
    ds = build_dataset("demo_norte")

    assert all(u.email.endswith("@demo-norte.example") for u in ds.users)
    assert all(u.email.isascii() for u in ds.users)
    assert all(u.role in {"standard", "admin"} for u in ds.users)
    assert ds.users[0].role == "admin"


def test_seeder_refuses_a_real_data_tenant_before_touching_the_database():
    # conn=None: the check must happen before any database call.
    with pytest.raises(NotADummyTenantError):
        seed_tenant(None, _tenant("arkus_nexus", is_dummy=False), build_dataset("arkus_nexus"))  # type: ignore[arg-type]


def test_seeder_refuses_another_tenants_dataset():
    with pytest.raises(ValueError):
        seed_tenant(None, _tenant("demo_norte", is_dummy=True), build_dataset("demo_bajio"))  # type: ignore[arg-type]
