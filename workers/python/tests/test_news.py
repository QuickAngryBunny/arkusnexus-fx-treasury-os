import datetime as dt
import json

from fxworkers.news import gnews, moves
from fxworkers.news.sync import MEXICO_CITY, move_window, today_window


def series(n=60, base=18.0, step=0.01):
    dates, rates, day = [], [], dt.date(2026, 1, 5)
    while len(dates) < n:
        if day.weekday() < 5:
            dates.append(day)
            rates.append(base + step * ((len(dates) % 3) - 1))  # small zig-zag
        day += dt.timedelta(days=1)
    return dates, rates


def test_a_jump_beyond_two_sigma_is_flagged_and_normal_days_are_not():
    dates, rates = series()
    rates[40] = rates[39] * 1.02  # +2% in a day after a quiet month
    flagged = moves.flagged(dates, rates)
    assert [m.date for m in flagged][:1] == [dates[40]]
    m = flagged[0]
    assert m.sigma > 2 and round(m.change_pct, 1) == 2.0 and m.vol30_pct > 0
    assert moves.score(dates, rates, 5) is None  # not enough history yet


def test_windows_are_in_mexico_city_time():
    w = move_window(dt.date(2026, 10, 9))
    assert w.key == "move:2026-10-09"
    assert w.start == dt.datetime(2026, 10, 8, 18, 0, tzinfo=MEXICO_CITY)
    assert w.end == dt.datetime(2026, 10, 10, 12, 0, tzinfo=MEXICO_CITY)
    free = gnews.Plan(history_days=30, delay_hours=12, max_articles=10)
    now = dt.datetime(2026, 11, 11, 8, 0, tzinfo=dt.timezone.utc)  # 02:00 in Mexico City
    t = today_window(now, free)
    assert t.key == "today:2026-11-11" and t.end == now - dt.timedelta(hours=12)
    assert t.end - t.start == dt.timedelta(hours=24)  # delayed plan early in the day: latest 24 h it can see
    assert free.earliest(dt.date(2026, 11, 11)) == dt.date(2026, 10, 12)


def test_parse_keeps_relevance_order_and_skips_broken_rows():
    raw = json.dumps({"totalArticles": 3, "articles": [
        {"title": "Peso sube", "description": None, "url": "https://a.mx/1", "publishedAt": "2026-11-10T15:00:00Z",
         "lang": "es", "source": {"name": "A", "url": "https://a.mx"}},
        {"title": "", "url": "https://a.mx/2", "publishedAt": "2026-11-10T15:00:00Z", "source": {}},
        {"title": "No date", "url": "https://a.mx/3", "source": {}},
    ]}).encode()
    arts = gnews.parse(raw, "es")
    assert [a.url for a in arts] == ["https://a.mx/1"]
    assert arts[0].published_at.tzinfo is not None


def test_interleave_alternates_languages_without_duplicates():
    def art(url, lang):
        return gnews.Article(url, None, url, "S", None, dt.datetime(2026, 11, 10, tzinfo=dt.timezone.utc), lang)
    es = [art("u1", "es"), art("u2", "es"), art("u3", "es")]
    en = [art("u1", "en"), art("v1", "en")]
    assert [a.url for a in gnews.interleave({"es": es, "en": en}, 4)] == ["u1", "v1", "u2", "u3"]
