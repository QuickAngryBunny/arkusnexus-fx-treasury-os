-- USD/MXN news (Phase 2): flagged rate moves and the GNews headlines around them, copied into every tenant.
-- Only the ingestion worker calls GNews (fetched once into the raw store); the API and web read these tables only,
-- so news for a past date never triggers a GNews request.

-- Days whose move was unusually large: |daily change| beyond 2 standard deviations of the previous 30 days.
create table rate_move (
    source_code  text not null references rate_source (code),
    rate_date    date not null,
    rate         numeric(18, 6) not null,
    change_pct   numeric(10, 4) not null,  -- vs the previous FIX, percent
    sigma        numeric(8, 2) not null,   -- change in standard deviations of daily changes over the previous 30 days
    vol30_pct    numeric(8, 2) not null,   -- annualized 30-day volatility before the move, percent
    computed_at  timestamptz not null default now(),
    primary key (source_code, rate_date)
);

-- One news search window: "today" (since midnight Mexico City) or around a flagged move
-- (6 pm the day before to noon the day after, Mexico City).
create table news_window (
    window_key     text primary key check (window_key ~ '^(today|move):\d{4}-\d{2}-\d{2}$'),
    kind           text not null check (kind in ('today', 'move')),
    rate_date      date not null,
    window_from    timestamptz not null,
    window_to      timestamptz not null,
    provider       text not null default 'gnews',
    raw_refs       text[] not null default '{}',
    article_count  int not null default 0,
    fetched_at     timestamptz not null default now(),
    check (window_from < window_to)
);

-- Headlines in relevance order (rank 1 = most relevant), Spanish and English interleaved.
create table news_article (
    id            uuid primary key default gen_random_uuid(),
    window_key    text not null references news_window (window_key) on delete cascade,
    rank          int not null check (rank >= 1),
    lang          char(2) not null check (lang in ('es', 'en')),
    title         text not null,
    description   text,
    url           text not null,
    source_name   text not null,
    source_url    text,
    published_at  timestamptz not null,
    unique (window_key, url)
);

create index news_article_window_idx on news_article (window_key, rank);
