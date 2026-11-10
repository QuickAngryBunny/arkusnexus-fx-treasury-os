from fxworkers.dummy_data import build_dataset


def test_same_slug_gives_identical_dataset():
    assert build_dataset("demo_norte", "Demo Norte") == build_dataset("demo_norte", "Demo Norte")


def test_different_tenants_share_no_ids_or_emails():
    a = build_dataset("demo_norte", "Demo Norte")
    b = build_dataset("demo_bajio", "Demo Bajío")

    ids_a = {x.id for x in [*a.legal_entities, *a.departments, *a.users]}
    ids_b = {x.id for x in [*b.legal_entities, *b.departments, *b.users]}
    assert ids_a.isdisjoint(ids_b)
    assert {u.email for u in a.users}.isdisjoint({u.email for u in b.users})


def test_references_are_consistent():
    ds = build_dataset("demo_norte", "Demo Norte", user_count=25)
    entity_ids = {le.id for le in ds.legal_entities}
    department_ids = {d.id for d in ds.departments}

    assert all(d.legal_entity_id in entity_ids for d in ds.departments)
    assert all(u.department_id in department_ids for u in ds.users)
    assert len({u.email for u in ds.users}) == len(ds.users)
    assert len({(d.legal_entity_id, d.name) for d in ds.departments}) == len(ds.departments)


def test_data_is_obviously_dummy_and_valid_for_the_schema():
    ds = build_dataset("demo_norte", "Demo Norte")

    assert all(le.tax_id.startswith("DUMMY-") for le in ds.legal_entities)
    assert {le.functional_currency for le in ds.legal_entities} == {"MXN", "USD"}
    assert all(u.email.endswith("@demo-norte.example") for u in ds.users)
    assert all(u.email.isascii() for u in ds.users)
    assert all(u.role in {"standard", "admin"} for u in ds.users)
    assert ds.users[0].role == "admin"
    assert all(1 <= len(d.name) <= 120 for d in ds.departments)
