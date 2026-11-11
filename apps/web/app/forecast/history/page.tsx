import Link from "next/link";
import { AlertBanner, Badge, Breadcrumb, Button, PageHeader, buttonClassName, type BadgeTone } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { LinkPills } from "@/components/LinkPills";
import { RefreshWhile } from "@/components/RefreshWhile";
import { apiGet, type DayStatus, type HistoryDay, type HistoryMonth, type HistorySummary, type HistoryYear } from "@/lib/api";
import { CURRENCY_DIGITS, formatDateTime, formatNumber, formatRate, formatSigned, type MessageKey } from "@/lib/i18n";
import { getSession } from "@/lib/session";
import { markHoliday, requestSync } from "./actions";

export const dynamic = "force-dynamic";

const SOURCE = "banxico_fix";
const BASE = `/api/tenant/rates/${SOURCE}`;
type View = "year" | "month" | "day";
type Params = { view?: string; y?: string; m?: string; d?: string; done?: string };

const STATUS_TONE: Record<DayStatus, BadgeTone> = {
  ok: "green", revised: "neutral", gap: "orange", holiday: "neutral", holiday_marked: "violet", weekend: "neutral",
};

/** Full history (R-02): coverage and issues, year / month / day views with fixes, and the sync sources panel. */
export default async function FullHistoryPage({ searchParams }: { searchParams: Promise<Params> }) {
  const params = await searchParams;
  const { tenant, profile, locale, t } = await getSession();
  const crumbs = (
    <Breadcrumb label={t("breadcrumb.label")} linkAs={Link} items={[{ label: t("nav.forecast"), href: "/forecast" }, { label: t("hist.crumb") }]} />
  );
  const summary = tenant && profile?.ok ? await apiGet<HistorySummary>(`${BASE}/history/summary`, tenant) : null;

  if (!tenant || !summary?.ok || !summary.data.firstDate || !summary.data.lastDate) {
    return (
      <AppShell active="forecast">
        <div className="stack-sm">
          <PageHeader breadcrumb={crumbs} title={t("hist.title")} />
          {!tenant ? (
            <AlertBanner tone="info" title={t("dev.pickTenantTitle")}>{t("dev.pickTenantBody")}</AlertBanner>
          ) : summary && !summary.ok ? (
            <AlertBanner tone="danger" title={t("hist.apiError")}>{`${summary.status || ""} ${summary.message}`.trim()}</AlertBanner>
          ) : (
            <AlertBanner tone="brand" title={t("fx.emptyTitle")}>{t("fx.emptyBody")}</AlertBanner>
          )}
        </div>
      </AppShell>
    );
  }

  const s = summary.data;
  const tz = profile!.ok ? profile!.data.timeZone : "America/Mexico_City";
  const first = s.firstDate!;
  const last = s.lastDate!;
  const fmt = (iso: string, opts: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat(locale, { ...opts, timeZone: "UTC" }).format(new Date(`${iso}T00:00:00Z`));

  // ── Which view and period (all in the URL) ───────────────
  const view: View = params.view === "year" || params.view === "day" ? params.view : "month";
  const clampDay = (d: string) => (d < first ? first : d > last ? last : d);
  const day = clampDay(/^\d{4}-\d{2}-\d{2}$/.test(params.d ?? "") ? params.d! : last);
  const month = /^\d{4}-\d{2}$/.test(params.m ?? "") ? clampDay(`${params.m}-01`).slice(0, 7) : (view === "day" ? day : last).slice(0, 7);
  const year = /^\d{4}$/.test(params.y ?? "") ? Math.min(Math.max(Number(params.y), Number(first.slice(0, 4))), Number(last.slice(0, 4))) : Number(month.slice(0, 4));

  const href = (p: Record<string, string>) => `/forecast/history?${new URLSearchParams(p).toString()}`;
  const here = view === "year" ? href({ view, y: String(year) }) : view === "day" ? href({ view, d: day }) : href({ view, m: month });
  const shiftMonth = (ym: string, n: number) => {
    const d = new Date(Date.UTC(Number(ym.slice(0, 4)), Number(ym.slice(5, 7)) - 1 + n, 1));
    return d.toISOString().slice(0, 7);
  };
  const shiftDay = (iso: string, n: number) => {
    const d = new Date(`${iso}T00:00:00Z`);
    do d.setUTCDate(d.getUTCDate() + n); while (d.getUTCDay() === 0 || d.getUTCDay() === 6);
    return d.toISOString().slice(0, 10);
  };
  const nav =
    view === "year"
      ? { label: String(year), prev: year > Number(first.slice(0, 4)) ? href({ view, y: String(year - 1) }) : null, next: year < Number(last.slice(0, 4)) ? href({ view, y: String(year + 1) }) : null }
      : view === "month"
        ? { label: fmt(`${month}-01`, { month: "long", year: "numeric" }), prev: shiftMonth(month, -1) >= first.slice(0, 7) ? href({ view, m: shiftMonth(month, -1) }) : null, next: shiftMonth(month, 1) <= last.slice(0, 7) ? href({ view, m: shiftMonth(month, 1) }) : null }
        : { label: fmt(day, { dateStyle: "long" }), prev: shiftDay(day, -1) >= first ? href({ view, d: shiftDay(day, -1) }) : null, next: shiftDay(day, 1) <= last ? href({ view, d: shiftDay(day, 1) }) : null };

  const [yearData, monthData, dayData] = await Promise.all([
    view === "year" ? apiGet<HistoryYear>(`${BASE}/history/year/${year}`, tenant) : null,
    view === "month" ? apiGet<HistoryMonth>(`${BASE}/history/month/${month.slice(0, 4)}/${Number(month.slice(5, 7))}`, tenant) : null,
    view === "day" ? apiGet<HistoryDay>(`${BASE}/history/day/${day}`, tenant) : null,
  ]);

  const rate = (v: number) => formatRate(locale, v);
  const statusChip = (st: DayStatus, revision = 1) => (
    <Badge tone={STATUS_TONE[st]} dot={false}>{st === "revised" ? t("hist.st.revised", { n: revision }) : t(`hist.st.${st}` as MessageKey)}</Badge>
  );
  const gapActions = (date: string) => (
    <span className="hist-actions">
      <form action={markHoliday}>
        <input type="hidden" name="date" value={date} />
        <input type="hidden" name="back" value={here} />
        <Button size="sm" type="submit">{t("hist.markHoliday")}</Button>
      </form>
      <form action={requestSync}>
        <input type="hidden" name="from" value={date} />
        <input type="hidden" name="to" value={date} />
        <input type="hidden" name="back" value={here} />
        <Button size="sm" variant="outline" type="submit">{t("hist.refetch")}</Button>
      </form>
    </span>
  );

  // ── Status message after an action ───────────────────────
  const [doneKind, doneArg] = (params.done ?? "").split(":");
  const doneText =
    doneKind === "sync" ? t("hist.done.sync")
    : doneKind === "holiday" && doneArg ? t("hist.done.holiday", { date: fmt(doneArg, { dateStyle: "medium" }) })
    : doneKind === "refetch" && doneArg ? t("hist.done.refetch", { date: fmt(doneArg, { dateStyle: "medium" }) })
    : doneKind === "error" ? t("hist.done.error")
    : null;

  // ── Sync panel ───────────────────────────────────────────
  const health = !s.lastLoad ? "none" : s.lastLoad.status === "failed" ? "failed" : "ok";
  const pending = s.lastRequest && (s.lastRequest.status === "pending" || s.lastRequest.status === "running") ? s.lastRequest : null;
  const exportHref = `/forecast/history/export?${new URLSearchParams(view === "year" ? { view, y: String(year) } : { view: "month", m: month }).toString()}`;

  return (
    <AppShell active="forecast">
      <div className="stack-sm">
        <PageHeader
          breadcrumb={crumbs}
          title={t("hist.title")}
          actions={<a href={exportHref} className={buttonClassName({ variant: "secondary" })} download>{t("hist.export")}</a>}
        />

        <section className="hist-cards">
          <div className="hist-card">
            <span>{t("hist.coverage")}</span>
            <strong>{t("hist.coverageValue", { from: fmt(first, { dateStyle: "medium" }), to: fmt(last, { dateStyle: "medium" }) })}</strong>
          </div>
          <div className="hist-card">
            <span>{t("hist.observations")}</span>
            <strong>{formatNumber(locale, s.observations)}</strong>
          </div>
          <div className={`hist-card${s.openGaps + s.duplicates > 0 ? " hist-card--issue" : ""}`}>
            <span>{t("hist.issues")}</span>
            <strong>{s.openGaps + s.duplicates > 0 ? t("hist.issuesValue", { gaps: s.openGaps, dups: s.duplicates }) : t("hist.issuesNone")}</strong>
          </div>
          <div className="hist-card">
            <span>{t("hist.corrections")}</span>
            <strong>{t("hist.correctionsValue", { n: s.corrections })}</strong>
          </div>
        </section>

        <div className="hist-layout">
          <section className="card hist-main">
            <div className="hist-toolbar">
              <LinkPills
                label={t("hist.viewBy")}
                value={view}
                options={[
                  { value: "year", label: t("hist.view.year"), href: href({ view: "year", y: String(year) }) },
                  { value: "month", label: t("hist.view.month"), href: href({ view: "month", m: month }) },
                  { value: "day", label: t("hist.view.day"), href: href({ view: "day", d: view === "month" ? clampDay(`${month}-28`) : day }) },
                ]}
              />
              <div className="hist-period">
                {nav.prev ? <Link href={nav.prev} aria-label={t("hist.prev")} className="hist-step">‹</Link> : <span className="hist-step is-off" aria-hidden="true">‹</span>}
                <strong>{nav.label}</strong>
                {nav.next ? <Link href={nav.next} aria-label={t("hist.next")} className="hist-step">›</Link> : <span className="hist-step is-off" aria-hidden="true">›</span>}
              </div>
            </div>

            {doneText && (
              <div role="status" className={`hist-status${doneKind === "error" ? " is-error" : ""}`}>{doneText}</div>
            )}

            {(() => {
              // Average for the period being shown (year: weighted by days with a FIX, so it equals the daily mean).
              const stats =
                view === "year" && yearData?.ok
                  ? (() => {
                      const ms = yearData.data.months.filter((m) => m.days > 0 && m.average !== null);
                      const n = ms.reduce((acc, m) => acc + m.days, 0);
                      return n === 0 ? null : {
                        avg: ms.reduce((acc, m) => acc + m.average! * m.days, 0) / n,
                        lo: Math.min(...ms.map((m) => m.low!)),
                        hi: Math.max(...ms.map((m) => m.high!)),
                        n,
                      };
                    })()
                  : view === "month" && monthData?.ok
                    ? (() => {
                        const rates = monthData.data.days.filter((x) => x.rate !== null).map((x) => x.rate!);
                        return rates.length === 0 ? null : {
                          avg: rates.reduce((acc, v) => acc + v, 0) / rates.length,
                          lo: Math.min(...rates),
                          hi: Math.max(...rates),
                          n: rates.length,
                        };
                      })()
                    : null;
              return stats && (
                <div className="hist-avg" role="group" aria-label={t("hist.avgLabel", { period: nav.label })}>
                  <span className="hist-avg__label">{t("hist.avgLabel", { period: nav.label })}</span>
                  <strong className="hist-avg__value">{rate(stats.avg)} MXN</strong>
                  <span className="hist-avg__detail">{t("hist.avgDetail", { lo: rate(stats.lo), hi: rate(stats.hi), n: stats.n })}</span>
                </div>
              );
            })()}

            {view === "year" && yearData?.ok && (
              <div className="table-wrap">
                <table className="table hist-table">
                  <thead>
                    <tr>
                      <th scope="col">{t("hist.month")}</th>
                      <th scope="col" className="num">{t("hist.average")}</th>
                      <th scope="col" className="num">{t("hist.low")}</th>
                      <th scope="col" className="num">{t("hist.high")}</th>
                      <th scope="col" className="num">{t("hist.days")}</th>
                      <th scope="col">{t("hist.issuesCol")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {yearData.data.months.map((m) => {
                      const issues = [
                        m.gaps === 1 ? t("hist.gaps1") : m.gaps > 1 ? t("hist.gapsN", { n: m.gaps }) : null,
                        m.revised > 0 ? t("hist.revisedN", { n: m.revised }) : null,
                      ].filter(Boolean).join(" · ");
                      return (
                        <tr key={m.month}>
                          <td><Link href={href({ view: "month", m: m.month })} className="hist-link">{fmt(`${m.month}-01`, { month: "long" })}</Link></td>
                          <td className="num">{m.average !== null ? rate(m.average) : "—"}</td>
                          <td className="num">{m.low !== null ? rate(m.low) : "—"}</td>
                          <td className="num">{m.high !== null ? rate(m.high) : "—"}</td>
                          <td className="num">{m.days}</td>
                          <td className={issues ? "hist-issue" : "muted"}>{issues || "—"}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}

            {view === "month" && monthData?.ok && (
              <>
                {monthData.data.days.length === 0 ? (
                  <p className="subtitle">{t("hist.emptyMonth")}</p>
                ) : (
                  <div className="table-wrap">
                    <table className="table hist-table">
                      <thead>
                        <tr>
                          <th scope="col">{t("hist.date")}</th>
                          <th scope="col" className="num">{t("hist.fix")}</th>
                          <th scope="col">{t("hist.source")}</th>
                          <th scope="col">{t("hist.status")}</th>
                          <th scope="col">{t("hist.action")}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {monthData.data.days.map((d) => (
                          <tr key={d.date} className={d.status === "gap" ? "is-gap" : undefined}>
                            <td><Link href={href({ view: "day", d: d.date })} className="hist-link">{fmt(d.date, { weekday: "short", day: "numeric", month: "short" })}</Link></td>
                            <td className="num">{d.rate !== null ? rate(d.rate) : "—"}</td>
                            <td className="muted">{d.rate !== null ? t("hist.sourceBanxico") : d.status === "gap" ? t("hist.missing") : "—"}</td>
                            <td>{statusChip(d.status, d.revision)}</td>
                            <td>
                              {d.status === "gap" && gapActions(d.date)}
                              {d.status === "revised" && <Link href={href({ view: "day", d: d.date })} className={buttonClassName({ size: "sm", variant: "outline" })}>{t("hist.viewVersions")}</Link>}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                <p className="caption">{t("hist.monthNote")}</p>
              </>
            )}

            {view === "day" && dayData?.ok && (() => {
              const d = dayData.data;
              return (
                <div className="hist-day">
                  <div className="hist-day__main">
                    {d.rate !== null ? (
                      <span className="hist-day__rate">{t("hist.dayRate", { rate: rate(d.rate), quote: d.quoteCurrency })}</span>
                    ) : (
                      <span className="hist-day__none">{t("hist.dayNoRate")}</span>
                    )}
                    <div>{statusChip(d.status, d.revision ?? 1)}</div>
                    <dl className="hist-dl">
                      <dt>{t("hist.daySource")}</dt>
                      <dd>{t("hist.daySourceValue", { series: d.seriesId })}</dd>
                      {d.rate !== null && (
                        <>
                          <dt>{t("hist.dayDetermined")}</dt>
                          <dd>{t("hist.dayDeterminedValue", { date: fmt(d.date, { dateStyle: "medium" }) })}</dd>
                          {d.loadedAt && (<><dt>{t("hist.dayRetrieved")}</dt><dd>{formatDateTime(locale, d.loadedAt, tz)}</dd></>)}
                          {d.change !== null && d.changePct !== null && (
                            <>
                              <dt>{t("hist.dayChange")}</dt>
                              <dd className={d.change > 0 ? "fx-up" : d.change < 0 ? "fx-down" : undefined}>
                                {t("hist.dayChangeValue", { change: formatSigned(locale, d.change, CURRENCY_DIGITS), pct: formatSigned(locale, d.changePct, 2, "%") })}
                              </dd>
                            </>
                          )}
                        </>
                      )}
                    </dl>
                    {d.resolution && <p className="caption">{t("hist.dayMarkedBy", { who: d.resolution.resolvedBy, at: formatDateTime(locale, d.resolution.resolvedAt, tz) })}</p>}
                    {d.status === "gap" && gapActions(d.date)}
                  </div>
                </div>
              );
            })()}

          </section>

          <aside aria-label={t("hist.syncTitle")} className="card hist-sync">
            <strong className="hist-sync__title">{t("hist.syncTitle")}</strong>
            <span className="caption">{t("hist.syncLead")}</span>
            <div className="hist-source is-active">
              <div className="hist-source__head">
                <strong>{t("hist.syncBanxico")}</strong>
                <span className={`hist-health hist-health--${health}`}>● {t(`hist.health.${health}` as MessageKey)}</span>
              </div>
              <span className="hist-source__label">{t("hist.schedule")}</span>
              <span className="hist-source__value">{t("hist.scheduleValue")}</span>
              <form action={requestSync}>
                <input type="hidden" name="back" value={here} />
                <Button type="submit" full disabled={!!pending}>{pending ? t("hist.syncing") : t("hist.syncNow")}</Button>
              </form>
              <RefreshWhile active={!!pending} />
              <span className="caption">
                {pending
                  ? t("hist.syncPending", { at: formatDateTime(locale, pending.requestedAt, tz) })
                  : s.lastLoad && t("hist.lastSync", { at: formatDateTime(locale, s.lastLoad.at, tz) })}
              </span>
              {!pending && s.lastRequest?.result && <span className="caption">{t("hist.lastRequest", { result: s.lastRequest.result })}</span>}
            </div>
            <div className="hist-source is-off">
              <strong>{t("hist.dof")}</strong>
              <span className="caption">{t("hist.notConfigured")}</span>
            </div>
            <div className="hist-source is-off">
              <strong>{t("hist.fred")}</strong>
              <span className="caption">{t("hist.notConfigured")}</span>
            </div>
            <span className="caption">{t("hist.syncNote")}</span>
          </aside>
        </div>
      </div>
    </AppShell>
  );
}
