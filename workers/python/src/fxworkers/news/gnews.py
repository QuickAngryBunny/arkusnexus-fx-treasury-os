"""GNews search client (https://docs.gnews.io/endpoints/search-endpoint).

Only the ingestion worker calls it. The API key goes in the X-Api-Key header, never in a URL or a log line.
Free plan: 1 request per second, 100 per day, 10 articles per request, last 30 days, 12-hour delay.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass

SEARCH_URL = "https://gnews.io/api/v4/search"
USER_AGENT = "fxworkers/0.1 (news ingestion)"

# Queries stay under GNews's 200-character limit; operators uppercase, phrases quoted.
# Spanish results come from Mexican publishers only ("tipo de cambio" alone also matches Peru, Colombia…).
QUERIES = {
    "es": '"peso mexicano" OR "tipo de cambio" OR Banxico OR "dólar peso"',
    "en": '"Mexican peso" OR "USD/MXN" OR Banxico OR "Mexico peso"',
}
COUNTRY = {"es": "mx", "en": None}


class QuotaExceeded(RuntimeError):
    """403: the daily request quota is used up (resets at 00:00 UTC)."""


class GNewsError(RuntimeError):
    """Any other failure after retries."""


@dataclass(frozen=True)
class Article:
    title: str
    description: str | None
    url: str
    source_name: str
    source_url: str | None
    published_at: dt.datetime
    lang: str


@dataclass(frozen=True)
class Plan:
    """What the account can reach. Free: 30 days back, 12-hour delay; paid: since 2020, real time."""

    history_days: int
    delay_hours: int
    max_articles: int

    @staticmethod
    def from_env() -> "Plan":
        paid = os.environ.get("GNEWS_PLAN", "free").strip().lower() != "free"
        return Plan(
            history_days=int(os.environ.get("GNEWS_HISTORY_DAYS", "2500" if paid else "30")),
            delay_hours=int(os.environ.get("GNEWS_DELAY_HOURS", "0" if paid else "12")),
            max_articles=int(os.environ.get("GNEWS_MAX_ARTICLES", "25" if paid else "10")),
        )

    def earliest(self, today: dt.date) -> dt.date:
        return max(today - dt.timedelta(days=self.history_days), dt.date(2020, 1, 1))


class Client:
    def __init__(self, api_key: str | None = None, min_interval: float = 1.1, attempts: int = 4) -> None:
        self.api_key = (api_key or os.environ.get("GNEWS_API_KEY", "")).strip()
        if not self.api_key:
            raise RuntimeError("GNEWS_API_KEY is not set. Put it in workers/python/.env (see .env.example).")
        self.min_interval = min_interval
        self.attempts = attempts
        self._last = 0.0
        self.requests = 0

    def search(self, lang: str, start: dt.datetime, end: dt.datetime, max_articles: int) -> bytes:
        params = {
            "q": QUERIES[lang], "lang": lang, "sortby": "relevance", "max": str(max_articles),
            **({"country": COUNTRY[lang]} if COUNTRY.get(lang) else {}),
            "from": start.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "to": end.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        }
        request = urllib.request.Request(
            f"{SEARCH_URL}?{urllib.parse.urlencode(params)}",
            headers={"X-Api-Key": self.api_key, "Accept": "application/json", "User-Agent": USER_AGENT},
        )
        delay = 2.0
        for attempt in range(1, self.attempts + 1):
            wait = self.min_interval - (time.monotonic() - self._last)
            if wait > 0:
                time.sleep(wait)
            self._last = time.monotonic()
            self.requests += 1
            try:
                with urllib.request.urlopen(request, timeout=30) as response:
                    return response.read()
            except urllib.error.HTTPError as e:
                if e.code == 403:
                    raise QuotaExceeded("GNews daily quota used up (resets 00:00 UTC) or email not verified.") from e
                if e.code in (429, 500, 503) and attempt < self.attempts:
                    time.sleep(delay)
                    delay *= 2
                    continue
                raise GNewsError(f"GNews answered HTTP {e.code}.") from e
            except (urllib.error.URLError, TimeoutError) as e:
                if attempt < self.attempts:
                    time.sleep(delay)
                    delay *= 2
                    continue
                raise GNewsError(f"GNews unreachable: {e}") from e
        raise GNewsError("GNews request failed.")  # pragma: no cover


def parse(raw: bytes, lang: str) -> list[Article]:
    """Articles in the order GNews returned them (relevance). Rows missing a title, url or date are skipped."""
    body = json.loads(raw)
    if "errors" in body:
        raise GNewsError(f"GNews error: {body['errors']}")
    out = []
    for a in body.get("articles") or []:
        try:
            published = dt.datetime.fromisoformat(a["publishedAt"].replace("Z", "+00:00"))
            title, url = a["title"].strip(), a["url"].strip()
        except (KeyError, AttributeError, ValueError):
            continue
        if not title or not url.startswith(("http://", "https://")):
            continue
        source = a.get("source") or {}
        out.append(Article(title, (a.get("description") or None), url, (source.get("name") or "").strip() or "—",
                           source.get("url"), published, (a.get("lang") or lang)[:2]))
    return out


def interleave(by_lang: dict[str, list[Article]], limit: int) -> list[Article]:
    """Spanish and English alternately, each in its own relevance order, without duplicate URLs."""
    out: list[Article] = []
    seen: set[str] = set()
    lists = [list(by_lang.get("es", [])), list(by_lang.get("en", []))]
    while len(out) < limit and any(lists):
        for lst in lists:
            while lst:
                a = lst.pop(0)
                if a.url not in seen:
                    seen.add(a.url)
                    out.append(a)
                    break
            if len(out) >= limit:
                break
    return out
