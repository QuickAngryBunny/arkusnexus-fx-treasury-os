"use client";

import Link from "next/link";
import { useActionState, useEffect, useMemo, useRef, useState, useTransition, type ReactNode } from "react";
import { Badge, Button, ConfirmDialog, Drawer, Field, Notice, Segmented, Switch } from "@fxos/ui";
import { deleteAlert, saveAlert, type AlertFormState } from "@/app/forecast/alert-actions";
import { chanceWithin, isPast, LOOK_AHEADS, parseRate, type AlertDirection, type LookAhead } from "@/lib/alerts";
import type { RateAlert, RateAlerts } from "@/lib/api";

/** Every string the drawer shows; "{x}" placeholders are filled here (no functions cross the server boundary). */
export type AlertDrawerLabels = {
  open: string;
  kicker: string;
  titleNew: string;
  titleEdit: string;
  close: string;
  above: string; // "Rises to or above {rate}"
  below: string;
  chance30: string; // "{chance}% chance within 30 days"
  earlyOn: string; // "Early warning · {days} days"
  earlyOff: string;
  statusOn: string;
  statusOff: string;
  statusReached: string; // "Off · reached {date}"
  deleteAlert: string;
  confirmTitle: string;
  confirmBody: string;
  confirmYes: string;
  confirmNo: string;
  offNotice: string;
  offReached: string; // "{date}"
  offBody: string;
  saveTurnOn: string;
  // Form
  today: string; // "Today {rate}"
  forecast30: string; // "30D forecast {rate}"
  chartAria: string; // "{rate}"
  emailWhen: string;
  dirAbove: string;
  dirBelow: string;
  rate: string;
  rateHint: string;
  oddsEmpty: string; // "{today}"
  oddsAbove: string; // "{chance}", "{rate}"
  oddsBelow: string;
  oddsPast: string; // "{rate}"
  oddsNoForecast: string;
  trendTitle: string;
  trendSubAbove: string; // "{needed}", "{window}", "{rate}"
  trendSubBelow: string;
  lookAhead: string;
  days: string; // "{days} days"
  trendNow: string; // "{days}", "{chance}"
  trendOnce: string;
  keepTitle: string;
  keepOn: string; // "{rate}"
  keepOff: string;
  footnote: string; // "{email}", "{stop}"
  stopOn: string;
  stopOff: string;
  emailsOff: string;
  emailsOffBody: string;
  earlyEmailsOff: string;
  settingsLink: string;
  cancel: string;
  create: string;
  save: string;
  saving: string;
  saved: string;
};

type Props = {
  data: RateAlerts;
  labels: AlertDrawerLabels;
  /** Number locale from the profile (U-01): rates show 2 decimals in it. */
  numbers: string;
  /** Language, for dates. */
  locale: string;
  email: string;
  icon?: ReactNode;
  /** From ?alert=<id> (alert emails): opens that alert for editing. */
  initialAlertId?: number;
};

/** Opens the header's drawer from elsewhere on the page (the alerts card): a new alert, or one alert (id) to edit. */
const OPEN_EVENT = "fxos:rate-alerts";
type OpenRequest = { id?: number };
const requestOpen = (detail: OpenRequest) => window.dispatchEvent(new CustomEvent<OpenRequest>(OPEN_EVENT, { detail }));

const fill = (s: string, values: Record<string, string | number>) =>
  s.replace(/\{(\w+)\}/g, (m, k: string) => (k in values ? String(values[k]) : m));

export function RateAlertDrawer({ data, labels, numbers, locale, email, icon, initialAlertId }: Props) {
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<RateAlert | null>(null);
  const rate2 = (v: number) => new Intl.NumberFormat(numbers, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  const pct = (c: number) => Math.round(c * 100);

  const openFor = (alert: RateAlert | null) => {
    setEditing(alert);
    setOpen(true);
  };

  // Alert emails link to ?alert=<id>: open that alert for editing (turning it back on, say).
  useEffect(() => {
    if (initialAlertId === undefined) return;
    const alert = data.alerts.find((a) => a.id === initialAlertId);
    if (alert) openFor(alert);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialAlertId]);

  useEffect(() => {
    const onOpen = (e: Event) => {
      const { id } = (e as CustomEvent<OpenRequest>).detail;
      openFor(id === undefined ? null : data.alerts.find((a) => a.id === id) ?? null);
    };
    window.addEventListener(OPEN_EVENT, onOpen);
    return () => window.removeEventListener(OPEN_EVENT, onOpen);
  }, [data.alerts]);

  return (
    <>
      <Button variant="secondary" onClick={() => openFor(null)}>
        {icon} {labels.open}
      </Button>
      <AlertForm key={editing?.id ?? "new"} data={data} labels={labels} rate2={rate2} pct={pct} email={email} locale={locale}
        editing={editing} open={open} onClose={() => setOpen(false)} onDone={() => { setOpen(false); setEditing(null); }} />
    </>
  );
}

function AlertForm({ data, labels, rate2, pct, email, locale, editing, open, onClose, onDone }: {
  data: RateAlerts; labels: AlertDrawerLabels; rate2: (v: number) => string; pct: (c: number) => number; email: string;
  locale: string; editing: RateAlert | null; open: boolean; onClose: () => void; onDone: () => void;
}) {
  const [state, action, pending] = useActionState<AlertFormState, FormData>(saveAlert, {});
  const [confirming, setConfirming] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [deleting, startDelete] = useTransition();
  const remove = () =>
    startDelete(async () => {
      const r = await deleteAlert(editing!.id);
      setConfirming(false);
      if (r.ok) onDone();
      else setDeleteError(r.error ?? "?");
    });
  const [direction, setDirection] = useState<AlertDirection>(editing?.direction ?? "above");
  const [rateText, setRateText] = useState(editing ? rate2(editing.rate) : "");
  const [early, setEarly] = useState(editing?.earlyWarning ?? true);
  const [lookAhead, setLookAhead] = useState<LookAhead>((editing?.lookAheadDays as LookAhead) ?? 7);
  const [keepOn, setKeepOn] = useState(editing?.keepOn ?? false);
  const handled = useRef<number | undefined>(undefined);

  useEffect(() => {
    if (state.ok && state.savedAt && handled.current !== state.savedAt) {
      handled.current = state.savedAt;
      onDone();
    }
  }, [state, onDone]);

  const today = data.today?.rate ?? null;
  const rate = parseRate(rateText);
  const shown = rate ?? today ?? 0;
  const f = data.forecast;
  const chance = (days: number) => (rate !== null && f ? chanceWithin(direction, rate, f.lastRate, f.horizons, days) : null);
  const rule = data.trendRules.find((r) => r.lookAheadDays === lookAhead) ?? { needed: 3, window: 5 };
  const h30 = f?.horizons.find((h) => h.days === 30);

  let odds: string;
  if (rate === null) odds = fill(labels.oddsEmpty, { today: today !== null ? rate2(today) : "—" });
  else if (today !== null && isPast(direction, rate, today)) odds = fill(labels.oddsPast, { rate: rate2(rate) });
  else if (!f) odds = labels.oddsNoForecast;
  else odds = fill(direction === "above" ? labels.oddsAbove : labels.oddsBelow, { chance: pct(chance(30) ?? 0), rate: rate2(rate) });

  const nowChance = chance(lookAhead);
  const rateShown = rate2(shown);

  return (
    <Drawer open={open} onClose={onClose} kicker={labels.kicker} title={editing ? labels.titleEdit : labels.titleNew} closeLabel={labels.close}
      footer={
        <>
          {editing
            ? <Button variant="outline" size="lg" className="vi-alert-delete" onClick={() => setConfirming(true)} disabled={pending || deleting}>{labels.deleteAlert}</Button>
            : <Button variant="outline" size="lg" onClick={onClose}>{labels.cancel}</Button>}
          <Button type="submit" size="lg" form="rate-alert-form" disabled={pending || deleting}>
            {pending ? labels.saving : !editing ? labels.create : editing.isOn ? labels.save : labels.saveTurnOn}
          </Button>
        </>
      }>
      <form id="rate-alert-form" action={action} className="stack-sm">
        {editing && <input type="hidden" name="id" value={editing.id} />}
        {state.error && !state.rateError && <Notice tone="danger" title={state.error} />}
        {deleteError && <Notice tone="danger" title={deleteError} />}
        {editing && !editing.isOn && (
          <Notice tone="warning" title={editing.reachedAt
            ? fill(labels.offReached, { date: new Intl.DateTimeFormat(locale, { day: "numeric", month: "short" }).format(new Date(editing.reachedAt)) })
            : labels.offNotice}>
            {labels.offBody}
          </Notice>
        )}
        {!data.emails.reached && (
          <Notice tone="warning" title={labels.emailsOff}>
            {labels.emailsOffBody} <Link href="/settings/notifications">{labels.settingsLink}</Link>
          </Notice>
        )}

        <div className="vi-alert-preview">
          <div className="vi-alert-preview__head">
            <span>{fill(labels.today, { rate: today !== null ? rate2(today) : "—" })}</span>
            {h30 && <span>{fill(labels.forecast30, { rate: rate2(h30.point) })}</span>}
          </div>
          <AlertChart data={data} rate={rate} label={fill(labels.chartAria, { rate: rateShown })} tag={rate !== null ? rateShown : null} />
          <span className="vi-alert-preview__odds" aria-live="polite">{odds}</span>
        </div>

        <div className="field-block">
          <span className="field-label">{labels.emailWhen}</span>
          <Segmented label={labels.emailWhen} name="direction" value={direction} onValueChange={(v) => setDirection(v)} onWhite
            options={[{ value: "above", label: labels.dirAbove }, { value: "below", label: labels.dirBelow }]} />
        </div>

        <Field label={labels.rate} name="rate" inputMode="decimal" autoComplete="off" emphasis
          placeholder={today !== null ? rate2(today) : undefined}
          value={rateText} onChange={(e) => setRateText(e.target.value)}
          hint={labels.rateHint} error={state.rateError ? state.error : undefined} />

        <section className={early ? "vi-alert-option vi-alert-option--on" : "vi-alert-option"} aria-label={labels.trendTitle}>
          <Switch label={labels.trendTitle} name="earlyWarning" checked={early} onCheckedChange={setEarly}
            note={fill(direction === "above" ? labels.trendSubAbove : labels.trendSubBelow, { needed: rule.needed, window: rule.window, rate: rateShown })} />
          {early && (
            <div className="vi-alert-option__more">
              <span className="field-label">{labels.lookAhead}</span>
              <Segmented label={labels.lookAhead} name="lookAheadDays" value={String(lookAhead) as `${LookAhead}`}
                onValueChange={(v) => setLookAhead(Number(v) as LookAhead)} onWhite
                options={LOOK_AHEADS.map((d) => ({ value: String(d) as `${LookAhead}`, label: fill(labels.days, { days: d }) }))} />
              {nowChance !== null && <span className="field-hint">{fill(labels.trendNow, { days: lookAhead, chance: pct(nowChance) })}</span>}
              <span className="field-hint">{labels.trendOnce}{!data.emails.earlyWarning ? ` ${labels.earlyEmailsOff}` : ""}</span>
            </div>
          )}
          {!early && <input type="hidden" name="lookAheadDays" value={lookAhead} />}
        </section>

        <Switch label={labels.keepTitle} name="keepOn" checked={keepOn} onCheckedChange={setKeepOn}
          note={fill(keepOn ? labels.keepOn : labels.keepOff, { rate: rateShown })} />

        <p className="field-hint">{fill(labels.footnote, { email, stop: keepOn ? labels.stopOn : labels.stopOff })}</p>
      </form>
      <ConfirmDialog open={confirming} title={labels.confirmTitle} danger pending={deleting}
        confirmLabel={labels.confirmYes} cancelLabel={labels.confirmNo}
        onConfirm={remove} onCancel={() => setConfirming(false)}>
        {editing && fill(editing.direction === "above" ? labels.above : labels.below, { rate: rate2(editing.rate) })}. {labels.confirmBody}
      </ConfirmDialog>
    </Drawer>
  );
}

/** The last 12 months of FIX, the 30-day forecast with its 8-in-10 range, and the alert's rate as a line. */
function AlertChart({ data, rate, label, tag }: { data: RateAlerts; rate: number | null; label: string; tag: string | null }) {
  const W = 720, H = 240, split = 600;
  const geometry = useMemo(() => {
    const hist = data.history;
    if (hist.length < 2) return null;
    const step = Math.max(1, Math.floor(hist.length / 120));
    const pts = hist.filter((_, i) => i % step === 0 || i === hist.length - 1);
    const f = data.forecast;
    const horizons = (f?.horizons ?? []).filter((h) => h.days <= 30).sort((a, b) => a.days - b.days);
    const values = [...pts.map((p) => p.rate), ...horizons.flatMap((h) => [h.lower80, h.upper80])];
    return { pts, horizons, last: hist[hist.length - 1].rate, min: Math.min(...values), max: Math.max(...values) };
  }, [data]);
  if (!geometry) return null;

  const lo = Math.min(geometry.min, rate ?? geometry.min), hi = Math.max(geometry.max, rate ?? geometry.max);
  const pad = (hi - lo) * 0.12 || 0.5;
  const y = (v: number) => H - ((v - (lo - pad)) / (hi - lo + 2 * pad)) * H;
  const x = (i: number) => (i / (geometry.pts.length - 1)) * split;
  const line = geometry.pts.map((p, i) => `${i ? "L" : "M"}${x(i).toFixed(1)},${y(p.rate).toFixed(1)}`).join(" ");
  const fx = (days: number) => split + (days / 30) * (W - split);
  const hz = geometry.horizons;
  const band = hz.length
    ? `M${split},${y(geometry.last)} ` + hz.map((h) => `L${fx(h.days)},${y(h.upper80)}`).join(" ") + " "
      + [...hz].reverse().map((h) => `L${fx(h.days)},${y(h.lower80)}`).join(" ") + " Z"
    : "";
  const center = hz.length ? `M${split},${y(geometry.last)} ` + hz.map((h) => `L${fx(h.days)},${y(h.point)}`).join(" ") : "";
  const ly = rate !== null ? Math.max(14, Math.min(H - 14, y(rate))) : null;

  return (
    <svg viewBox={`0 0 ${W} ${H}`} width="100%" role="img" aria-label={label} className="vi-alert-chart">
      {band && <path d={band} className="vi-alert-chart__band" />}
      <path d={line} className="vi-alert-chart__line" />
      {center && <path d={center} className="vi-alert-chart__center" />}
      {ly !== null && (
        <>
          <path d={`M0 ${ly.toFixed(0)}H${W}`} className="vi-alert-chart__target" />
          {tag && (
            <>
              <rect x={W - 124} y={ly - 20} width={120} height={40} rx={20} className="vi-alert-chart__tag" />
              <text x={W - 64} y={ly + 8} textAnchor="middle" className="vi-alert-chart__tagText">{tag}</text>
            </>
          )}
        </>
      )}
    </svg>
  );
}

export type AlertsCardLabels = {
  kicker: string;
  emptyTitle: string;
  emptyBody: string;
  create: string;
  add: string; // accessible name of the "+" button
  maxReached: string; // "{max}"
};

/**
 * Exchange Forecast's bottom-right card: every rate alert of the person (each opens it in the drawer to edit or delete)
 * with a "+" for a new one, or an empty state that introduces alerts with "Create alert". Uses the header's drawer.
 */
export function RateAlertsCard({ data, labels, card, numbers, locale }: {
  data: RateAlerts; labels: AlertDrawerLabels; card: AlertsCardLabels; numbers: string; locale: string;
}) {
  const rate2 = (v: number) => new Intl.NumberFormat(numbers, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  const date = (iso: string) => new Intl.DateTimeFormat(locale, { day: "numeric", month: "short" }).format(new Date(iso));
  const full = data.alerts.length >= data.maxAlerts;

  if (data.alerts.length === 0) {
    return (
      <article className="card fx-alerts fx-alerts--empty">
        <span className="fx-kicker">{card.kicker}</span>
        <span className="fx-alerts__bell" aria-hidden="true">
          <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10 21h4" />
          </svg>
        </span>
        <strong className="fx-alerts__title">{card.emptyTitle}</strong>
        <p className="fx-alerts__body">{card.emptyBody}</p>
        <Button onClick={() => requestOpen({})}>{card.create}</Button>
      </article>
    );
  }

  return (
    <article className="card fx-alerts">
      <div className="fx-alerts__head">
        <span className="fx-kicker">{card.kicker}</span>
        <button type="button" className="fx-alerts__add" aria-label={full ? fill(card.maxReached, { max: data.maxAlerts }) : card.add}
          title={full ? fill(card.maxReached, { max: data.maxAlerts }) : card.add} disabled={full} onClick={() => requestOpen({})}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
            <path d="M12 5v14M5 12h14" />
          </svg>
        </button>
      </div>
      {!data.emails.reached && <Notice tone="warning" title={labels.emailsOff} />}
      <ul className="fx-alerts__list">
        {data.alerts.map((a) => (
          <li key={a.id}>
            <button type="button" className="fx-alerts__row" onClick={() => requestOpen({ id: a.id })}>
              <span className="fx-alerts__text">
                <strong>{fill(a.direction === "above" ? labels.above : labels.below, { rate: rate2(a.rate) })}</strong>
                <span className="fx-alerts__meta">
                  {[
                    a.isOn && a.chance30 !== null ? fill(labels.chance30, { chance: Math.round(a.chance30 * 100) }) : null,
                    a.earlyWarning ? fill(labels.earlyOn, { days: a.lookAheadDays }) : labels.earlyOff,
                  ].filter(Boolean).join(" · ")}
                </span>
              </span>
              {a.isOn
                ? <Badge tone="green">{labels.statusOn}</Badge>
                : <Badge tone="neutral">{a.reachedAt ? fill(labels.statusReached, { date: date(a.reachedAt) }) : labels.statusOff}</Badge>}
            </button>
          </li>
        ))}
      </ul>
    </article>
  );
}
