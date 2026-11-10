"""Series the worker knows how to fetch. Each tenant's rate_source table says which ones it loads."""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass


@dataclass(frozen=True)
class Source:
    code: str
    provider: str
    series_id: str
    base_currency: str
    quote_currency: str
    first_date: dt.date


SOURCES: dict[str, Source] = {
    "banxico_fix": Source(
        code="banxico_fix",
        provider="banxico",
        series_id="SF43718",  # Pesos por dólar, FIX, fecha de determinación
        base_currency="USD",
        quote_currency="MXN",
        first_date=dt.date(1991, 11, 12),
    ),
}


def get_source(code: str) -> Source:
    try:
        return SOURCES[code]
    except KeyError:
        raise ValueError(f"Unknown rate source '{code}'. Known: {', '.join(sorted(SOURCES))}.") from None
