"""Deterministic dummy treasury data: connected bank accounts and their income and expenses.

Shape of a USD/MXN nearshore services company (the exposure this product is for):
- The US LLC collects from US clients in USD and sends most of it to the Mexican entity's USD account.
- The Mexican S.A. de C.V. pays payroll, IMSS, SAT, rent and utilities in MXN and collects from local clients in MXN.
- Once a month the S.A. converts enough USD to cover its peso shortfall, at the day's Banxico FIX minus a bank
  spread; the rest of the USD stays in Mexico, waiting to be converted. Both legs record the applied rate.

Every value derives from (slug, date, item), so a day's movements never change when the range grows: re-seeding
later only adds the new days. Names are obviously fictitious.
"""

from __future__ import annotations

import calendar
import datetime as dt
from collections.abc import Callable
import hashlib
import random
import uuid
from dataclasses import dataclass
from decimal import ROUND_HALF_UP, Decimal

from .dummy_data import NAMESPACE

HISTORY_START = dt.date(2025, 1, 1)
CENT = Decimal("0.01")
RATE_STEP = Decimal("0.000001")
BANK_SPREAD = Decimal("0.0350")  # the dummy bank buys USD this many pesos below the FIX
CONVERSION_TIME = dt.time(13, 0)
MEXICO_CITY = dt.timezone(dt.timedelta(hours=-6))  # Mexico City has no DST since 2022

# Returns the FIX (MXN per USD) and its date on or before the given day, or None if none is stored.
RateLookup = Callable[[dt.date], "tuple[dt.date, Decimal] | None"]


@dataclass(frozen=True)
class BankAccount:
    id: uuid.UUID
    legal_entity_key: str  # "mx" | "us" (matches dummy_data legal entities)
    bank_name: str
    account_name: str
    account_last4: str
    currency: str
    kind: str
    opening_balance: Decimal
    opening_balance_date: dt.date


@dataclass(frozen=True)
class Transaction:
    id: uuid.UUID
    bank_account_id: uuid.UUID
    booked_on: dt.date
    direction: str
    amount: Decimal
    currency: str
    category: str
    counterparty: str
    description: str
    conversion_id: uuid.UUID | None = None
    counter_amount: Decimal | None = None
    counter_currency: str | None = None
    fx_rate: Decimal | None = None
    fx_rate_source: str | None = None
    fx_rate_at: dt.datetime | None = None


@dataclass(frozen=True)
class TreasuryDataset:
    slug: str
    accounts: list[BankAccount]
    transactions: list[Transaction]


US_CLIENTS = ["Cliente Ficticio Alfa Inc.", "Cliente Ficticio Beta LLC", "Cliente Ficticio Gamma Corp.",
              "Cliente Ficticio Delta Inc.", "Cliente Ficticio Épsilon LLC"]
MX_CLIENTS = ["Cliente Demo Uno S.A. de C.V.", "Cliente Demo Dos S.C."]


def _id(slug: str, kind: str, key: str) -> uuid.UUID:
    return uuid.uuid5(NAMESPACE, f"{slug}:{kind}:{key}")


def _rng(*parts: object) -> random.Random:
    seed = hashlib.sha256(":".join(map(str, parts)).encode()).digest()[:8]
    return random.Random(int.from_bytes(seed, "big"))


def _money(value: float) -> Decimal:
    return Decimal(str(value)).quantize(CENT, rounding=ROUND_HALF_UP)


def scale_for(slug: str) -> float:
    """Company size factor: demo_norte is the reference (1.0); other tenants are smaller."""
    return 1.0 if slug == "demo_norte" else round(0.45 + _rng(slug, "scale").random() * 0.3, 2)


def business_day_on_or_after(day: dt.date) -> dt.date:
    while day.weekday() >= 5:
        day += dt.timedelta(days=1)
    return day


def _month_day(year: int, month: int, day: int) -> dt.date:
    return dt.date(year, month, min(day, calendar.monthrange(year, month)[1]))


def build_accounts(slug: str) -> list[BankAccount]:
    s = scale_for(slug)
    r = _rng(slug, "accounts")
    last4 = lambda: f"{r.randrange(10_000):04d}"  # noqa: E731
    return [
        BankAccount(_id(slug, "bank_account", "us:operating"), "us", "Dummy Bank USA", "US Operating", last4(),
                    "USD", "operating", _money(420_000 * s), HISTORY_START),
        BankAccount(_id(slug, "bank_account", "mx:operativa"), "mx", "Banco Ficticio del Norte", "Operativa MXN",
                    last4(), "MXN", "operating", _money(6_800_000 * s), HISTORY_START),
        BankAccount(_id(slug, "bank_account", "mx:usd"), "mx", "Banco Ficticio del Norte", "Cuenta en dólares",
                    last4(), "USD", "operating", _money(150_000 * s), HISTORY_START),
    ]


def build_treasury(slug: str, until: dt.date, rate_on: RateLookup, start: dt.date = HISTORY_START) -> TreasuryDataset:
    """All accounts and every movement booked from `start` to `until` (inclusive). Pure given `rate_on`.

    Conversions need the FIX; a month whose conversion day has no stored FIX gets no conversion.
    """
    accounts = build_accounts(slug)
    us_op, mx_mxn, mx_usd = accounts
    s = scale_for(slug)
    clients = US_CLIENTS[: 3 + int(s * 2)]  # 4 clients at 0.5, 5 at 1.0
    txns: list[Transaction] = []

    def add(account: BankAccount, day: dt.date, key: str, direction: str, amount: Decimal, category: str,
            counterparty: str, description: str, **conversion: object) -> None:
        booked = business_day_on_or_after(day)
        if start <= booked <= until:
            txns.append(Transaction(_id(slug, "txn", f"{account.account_name}:{day.isoformat()}:{key}"),
                                    account.id, booked, direction, amount, account.currency, category,
                                    counterparty, description, **conversion))  # type: ignore[arg-type]

    year, month = start.year, start.month
    while dt.date(year, month, 1) <= until:
        r = lambda key: _rng(slug, year, month, key)  # noqa: E731 - one stream per item and month
        growth = 1 + 0.012 * ((year - HISTORY_START.year) * 12 + month - 1)  # ~1.2% a month

        # US LLC: client collections in USD.
        us_income = Decimal(0)
        for i, client in enumerate(clients):
            rc = r(f"client{i}")
            amount = _money((38_000 + 26_000 * i) * s * growth * rc.uniform(0.9, 1.1))
            us_income += amount
            add(us_op, _month_day(year, month, 8 + 2 * i + rc.randrange(3)), f"client{i}", "income", amount,
                "client_payment", client, f"Pago de factura {year}{month:02d}-{i + 1:02d}")

        # US LLC: costs in USD, then most of the month's collections go to Mexico.
        add(us_op, _month_day(year, month, 3), "cloud", "expense", _money(16_500 * s * growth * r("cloud").uniform(0.95, 1.08)),
            "cloud_software", "Proveedor Cloud Ficticio", "Servicios de nube y licencias")
        add(us_op, _month_day(year, month, 20), "contractors", "expense", _money(22_000 * s * r("ctr").uniform(0.85, 1.15)),
            "contractors", "Contratistas Ficticios LLC", "Servicios de contratistas")
        add(us_op, _month_day(year, month, 28), "fees", "expense", _money(185 * r("fee").uniform(0.9, 1.1)),
            "bank_fees", "Dummy Bank USA", "Comisiones bancarias")
        if r("travel").random() < 0.4:
            add(us_op, _month_day(year, month, 12 + r("travel").randrange(10)), "travel", "expense",
                _money(3_500 * s * r("travel2").uniform(0.6, 1.6)), "travel", "Agencia de Viajes Ficticia",
                "Viaje con cliente")
        transfer = _money(float(us_income) * 0.8)
        add(us_op, _month_day(year, month, 25), "ic", "expense", transfer, "intercompany", "Entidad mexicana (dummy)",
            "Pago de servicios intercompañía")
        add(mx_usd, _month_day(year, month, 25), "ic", "income", transfer, "intercompany", "Entidad de EE. UU. (dummy)",
            "Cobro de servicios intercompañía")

        # Mexican S.A.: local collections and peso costs.
        month_start = len(txns)
        for i, client in enumerate(MX_CLIENTS):
            add(mx_mxn, _month_day(year, month, 6 + 5 * i), f"mxclient{i}", "income",
                _money((650_000 - 270_000 * i) * s * growth * r(f"mx{i}").uniform(0.9, 1.1)),
                "client_payment", client, f"Cobro de factura {year}{month:02d}-MX{i + 1}")
        payroll = 1_080_000 * s * growth
        for day, label in ((15, "1a quincena"), (31, "2a quincena")):
            add(mx_mxn, _month_day(year, month, day), f"payroll{day}", "expense",
                _money(payroll * r(f"pay{day}").uniform(0.98, 1.02)), "payroll", "Nómina (dummy)", f"Nómina {label}")
        if month == 12:
            add(mx_mxn, dt.date(year, 12, 18), "aguinaldo", "expense", _money(payroll * 1.0), "year_end_bonus",
                "Nómina (dummy)", "Aguinaldo")
        add(mx_mxn, _month_day(year, month, 17), "imss", "expense", _money(payroll * 0.31 * r("imss").uniform(0.98, 1.02)),
            "social_security", "IMSS / INFONAVIT", "Cuotas obrero-patronales")
        add(mx_mxn, _month_day(year, month, 17), "sat", "expense", _money(payroll * 0.42 * r("sat").uniform(0.85, 1.15)),
            "taxes", "SAT", "ISR e IVA del mes anterior")
        add(mx_mxn, _month_day(year, month, 1), "rent", "expense", _money(185_000 * s * (1.05 if year > 2025 else 1)),
            "rent", "Inmobiliaria Ficticia S.A.", "Renta de oficinas")
        add(mx_mxn, _month_day(year, month, 10), "utilities", "expense", _money(42_000 * s * r("util").uniform(0.8, 1.2)),
            "utilities", "Servicios Ficticios", "Luz, agua e internet")
        add(mx_usd, _month_day(year, month, 28), "fees", "expense", _money(35 * r("feeusd").uniform(0.9, 1.1)),
            "bank_fees", "Banco Ficticio del Norte", "Comisión cuenta en dólares")

        # Monthly USD→MXN conversion covering the peso shortfall plus 5%, at FIX minus the bank spread.
        month_mxn = [t for t in txns[month_start:] if t.bank_account_id == mx_mxn.id]
        shortfall = sum((t.amount if t.direction == "expense" else -t.amount for t in month_mxn), Decimal(0))
        conversion_day = business_day_on_or_after(_month_day(year, month, 26))
        fix = rate_on(conversion_day) if shortfall > 0 and conversion_day <= until else None
        if fix is not None:
            fix_date, fix_rate = fix
            applied = (fix_rate - BANK_SPREAD).quantize(RATE_STEP)
            usd = (shortfall * Decimal("1.05") / applied).quantize(CENT, rounding=ROUND_HALF_UP)
            mxn = (usd * applied).quantize(CENT, rounding=ROUND_HALF_UP)
            conversion = {
                "conversion_id": _id(slug, "conversion", f"{year}-{month:02d}"),
                "fx_rate": applied,
                "fx_rate_source": f"Banco Ficticio del Norte: FIX {fix_date.isoformat()} ({fix_rate}) - {BANK_SPREAD}",
                "fx_rate_at": dt.datetime.combine(conversion_day, CONVERSION_TIME, MEXICO_CITY),
            }
            add(mx_usd, conversion_day, "conversion", "expense", usd, "fx_conversion", "Banco Ficticio del Norte",
                "Venta de dólares (conversión a pesos)", counter_amount=mxn, counter_currency="MXN", **conversion)
            add(mx_mxn, conversion_day, "conversion", "income", mxn, "fx_conversion", "Banco Ficticio del Norte",
                "Compra de pesos (conversión de dólares)", counter_amount=usd, counter_currency="USD", **conversion)

        month += 1
        if month == 13:
            year, month = year + 1, 1

    txns.sort(key=lambda t: (t.booked_on, t.bank_account_id.hex, t.id.hex))
    return TreasuryDataset(slug, accounts, txns)
