"use client";

import { useState, type KeyboardEvent, type ReactNode } from "react";
import { cx, type LinkComponent } from "./util";

export type StepPill = { label: ReactNode; state: "done" | "current" | "todo" };

/** The 1-2-3 pills above an onboarding step (O-02 to O-04). */
export function StepPills({ steps, label }: { steps: StepPill[]; label: string }) {
  return (
    <ol className="vi-pills" aria-label={label}>
      {steps.map((s, i) => (
        <li key={i} className={cx("vi-pills__item", `vi-pills__item--${s.state}`)} aria-current={s.state === "current" ? "step" : undefined}>
          <span className="vi-pills__num" aria-hidden="true">{s.state === "done" ? "✓" : i + 1}</span>
          {s.label}
        </li>
      ))}
    </ol>
  );
}

export type Tag = { key: string; name: string };

export type TagGroupProps = {
  currency: "MXN" | "USD";
  title: ReactNode;
  tags: Tag[];
  onAdd: (name: string) => void;
  onRemove: (key: string) => void;
  /** With onRename, clicking a name edits it in place (C-01). */
  onRename?: (key: string, name: string) => void;
  suggestions: string[];
  /** Shown in red under the tags when the group must not be empty (O-S2). */
  missing?: ReactNode;
  labels: { add: string; placeholder: string; inputLabel: string; suggestions: string; remove: (name: string) => string; rename: (name: string) => string; empty: string; save: string; cancel: string };
};

/** Bank names typed as tags for one currency (O-04, C-01). Duplicate names (ignoring case) are ignored. */
export function TagGroup({ currency, title, tags, onAdd, onRemove, onRename, suggestions, missing, labels }: TagGroupProps) {
  const [draft, setDraft] = useState("");
  const [editing, setEditing] = useState<{ key: string; value: string } | null>(null);
  const add = (value: string) => {
    const v = value.trim();
    if (v && !tags.some((t) => t.name.toLowerCase() === v.toLowerCase())) onAdd(v);
    setDraft("");
  };
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter") {
      e.preventDefault();
      add(draft);
    }
  };
  const left = suggestions.filter((s) => !tags.some((t) => t.name.toLowerCase() === s.toLowerCase())).slice(0, 4);

  return (
    <div className={cx("vi-tags", `vi-tags--${currency.toLowerCase()}`, missing ? "vi-tags--missing" : null)}>
      <span className="vi-tags__head"><span className="vi-tags__cur">{currency}</span><strong>{title}</strong></span>
      <div className="vi-tags__list">
        {tags.map((t) =>
          editing?.key === t.key ? (
            <span key={t.key} className="vi-tags__edit">
              <input aria-label={labels.rename(t.name)} value={editing.value} autoFocus maxLength={120}
                onChange={(e) => setEditing({ key: t.key, value: e.target.value })}
                onKeyDown={(e) => {
                  if (e.key === "Enter") { e.preventDefault(); if (editing.value.trim()) onRename?.(t.key, editing.value.trim()); setEditing(null); }
                  if (e.key === "Escape") setEditing(null);
                }} />
              <button type="button" onClick={() => { if (editing.value.trim()) onRename?.(t.key, editing.value.trim()); setEditing(null); }}>{labels.save}</button>
              <button type="button" onClick={() => setEditing(null)}>{labels.cancel}</button>
            </span>
          ) : (
            <span key={t.key} className="vi-tags__tag">
              {onRename ? (
                <button type="button" className="vi-tags__name" aria-label={labels.rename(t.name)} onClick={() => setEditing({ key: t.key, value: t.name })}>{t.name}</button>
              ) : (
                <span className="vi-tags__name">{t.name}</span>
              )}
              <button type="button" className="vi-tags__x" aria-label={labels.remove(t.name)} onClick={() => onRemove(t.key)}>×</button>
            </span>
          ),
        )}
        {tags.length === 0 && <span className={cx("vi-tags__empty", missing ? "vi-tags__empty--missing" : null)}>{missing ?? labels.empty}</span>}
      </div>
      <div className="vi-tags__add">
        <input type="text" aria-label={labels.inputLabel} placeholder={labels.placeholder} value={draft} maxLength={120}
          onChange={(e) => setDraft(e.target.value)} onKeyDown={onKey} />
        <button type="button" onClick={() => add(draft)}>{labels.add}</button>
      </div>
      {left.length > 0 && (
        <div className="vi-tags__suggest">
          <span>{labels.suggestions}</span>
          {left.map((s) => <button key={s} type="button" onClick={() => add(s)}>+ {s}</button>)}
        </div>
      )}
    </div>
  );
}

export type SettingsNavItem = { key: string; label: ReactNode; href: string };

/** Settings menu: Your account (Profile, Security, Notifications) and Company (Company info, Team for admins). */
export function SettingsNav({ groups, active, label, linkAs: Link = "a" }: {
  groups: { title: ReactNode; items: SettingsNavItem[] }[]; active: string; label: string; linkAs?: LinkComponent;
}) {
  return (
    <nav className="vi-settings-nav" aria-label={label}>
      {groups.filter((g) => g.items.length > 0).map((g, i) => (
        <div key={i} className="vi-settings-nav__group">
          <span className="vi-settings-nav__title">{g.title}</span>
          {g.items.map((it) => (
            <Link key={it.key} href={it.href} className="vi-settings-nav__item" aria-current={it.key === active ? "page" : undefined}>
              {it.label}
            </Link>
          ))}
        </div>
      ))}
    </nav>
  );
}
