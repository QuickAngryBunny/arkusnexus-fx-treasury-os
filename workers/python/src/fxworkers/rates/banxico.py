"""Banxico SIE API client and parser.

API: https://www.banxico.org.mx/SieAPIRest/service/v1/ (token in the Bmx-Token header, never in the URL).
A range request returns {"bmx":{"series":[{"idSerie":..., "datos":[{"fecha":"dd/mm/yyyy","dato":"18.6678"}]}]}}.
Days without a published value come back as "N/E".
"""

from __future__ import annotations

import datetime as dt
import json
import os
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation

BASE_URL = "https://www.banxico.org.mx/SieAPIRest/service/v1"
USER_AGENT = "fxworkers/0.1 (rate ingestion)"

# Sanity bounds for a pesos-per-dollar style rate; anything outside is a parsing or source error.
MIN_RATE = Decimal("0.0001")
MAX_RATE = Decimal("1000")


class SourceUnavailableError(RuntimeError):
    """The source could not be reached or answered with an error after retries."""


class InvalidPayloadError(ValueError):
    """The response did not have the expected shape or held values that fail validation."""


@dataclass(frozen=True)
class Observation:
    date: dt.date
    value: Decimal | None  # None = the source published no value that day (N/E)


def token() -> str:
    value = os.environ.get("BANXICO_TOKEN", "").strip()
    if not value:
        raise RuntimeError("BANXICO_TOKEN is not set. Put it in workers/python/.env (see .env.example).")
    return value


def range_url(series_id: str, start: dt.date, end: dt.date) -> str:
    return f"{BASE_URL}/series/{series_id}/datos/{start.isoformat()}/{end.isoformat()}"


def fetch_range(series_id: str, start: dt.date, end: dt.date, *, attempts: int = 3, timeout: float = 60) -> bytes:
    """Return the raw response body. Retries timeouts and 5xx/429 with backoff; 4xx fail at once."""
    if end < start:
        raise ValueError(f"Range end {end} is before start {start}.")
    request = urllib.request.Request(
        range_url(series_id, start, end),
        headers={"Bmx-Token": token(), "Accept": "application/json", "User-Agent": USER_AGENT},
    )
    last_error: Exception | None = None
    for attempt in range(1, attempts + 1):
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return response.read()
        except urllib.error.HTTPError as e:
            if e.code not in (429, 500, 502, 503, 504):
                raise SourceUnavailableError(f"Banxico answered HTTP {e.code} for series {series_id}.") from e
            last_error = e
        except (urllib.error.URLError, TimeoutError) as e:
            last_error = e
        if attempt < attempts:
            time.sleep(2 ** attempt)
    raise SourceUnavailableError(f"Banxico unreachable for series {series_id} after {attempts} attempts: {last_error}")


def parse(raw: bytes, series_id: str) -> list[Observation]:
    """Parse and validate a range response. Raises InvalidPayloadError on anything unexpected."""
    try:
        body = json.loads(raw)
        series = body["bmx"]["series"]
    except (ValueError, KeyError, TypeError) as e:
        raise InvalidPayloadError("Response is not a Banxico series payload.") from e

    match = [s for s in series if s.get("idSerie") == series_id]
    if len(match) != 1:
        raise InvalidPayloadError(f"Response does not hold exactly one series {series_id}.")

    observations: list[Observation] = []
    seen: set[dt.date] = set()
    for row in match[0].get("datos") or []:  # no "datos" = nothing published in the range
        try:
            date = dt.datetime.strptime(row["fecha"], "%d/%m/%Y").date()
        except (KeyError, TypeError, ValueError) as e:
            raise InvalidPayloadError(f"Bad date in row {row!r}.") from e
        if date in seen:
            raise InvalidPayloadError(f"Duplicate date {date} in series {series_id}.")
        seen.add(date)

        text = str(row.get("dato", "")).strip().replace(",", "")
        if text in ("N/E", ""):
            observations.append(Observation(date, None))
            continue
        try:
            value = Decimal(text)
        except InvalidOperation as e:
            raise InvalidPayloadError(f"Bad value {text!r} on {date}.") from e
        if not (MIN_RATE <= value <= MAX_RATE):
            raise InvalidPayloadError(f"Value {value} on {date} is outside {MIN_RATE}–{MAX_RATE}.")
        observations.append(Observation(date, value))

    observations.sort(key=lambda o: o.date)
    return observations
