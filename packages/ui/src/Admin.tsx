"use client";

import { useEffect, useId, useRef, type ReactNode } from "react";
import { cx } from "./util";

export type DrawerProps = {
  open: boolean;
  onClose: () => void;
  kicker?: ReactNode;
  title: ReactNode;
  closeLabel: string;
  children: ReactNode;
  /** Sticky footer, usually Cancel + the primary action. */
  footer?: ReactNode;
};

/** Right-side drawer (A-02 New tenant). Modal: Escape and the scrim close it; focus moves into it. */
export function Drawer({ open, onClose, kicker, title, closeLabel, children, footer }: DrawerProps) {
  const titleId = useId();
  const panel = useRef<HTMLElement>(null);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    panel.current?.querySelector<HTMLElement>("input, select, textarea, button")?.focus();
    return () => document.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;
  return (
    <div className="vi-drawer">
      <button type="button" className="vi-drawer__scrim" aria-label={closeLabel} onClick={onClose} />
      <aside ref={panel} role="dialog" aria-modal="true" aria-labelledby={titleId} className="vi-drawer__panel">
        <div className="vi-drawer__head">
          <div>
            {kicker && <span className="vi-drawer__kicker">{kicker}</span>}
            <h2 id={titleId}>{title}</h2>
          </div>
          <button type="button" className="vi-drawer__close" aria-label={closeLabel} onClick={onClose}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>
        <div className="vi-drawer__body">{children}</div>
        {footer && <div className="vi-drawer__foot">{footer}</div>}
      </aside>
    </div>
  );
}

export type StepState = "done" | "running" | "failed" | "todo";

export type Step = { id: string; state: StepState; title: ReactNode; note?: ReactNode; progress?: number };

/** Setup steps with their state (A-03 running, A-S2 failed). Progress 0–100 shows a bar under a running step. */
export function StepList({ steps, label }: { steps: Step[]; label: string }) {
  return (
    <ol className="vi-steps" aria-label={label}>
      {steps.map((s) => (
        <li key={s.id} className={cx("vi-steps__item", `vi-steps__item--${s.state}`)}>
          <span className="vi-steps__mark" aria-hidden="true">{s.state === "done" ? "✓" : s.state === "failed" ? "!" : ""}</span>
          <span className="vi-steps__text">
            <strong>{s.title}</strong>
            {s.note && <span>{s.note}</span>}
            {s.state === "running" && s.progress !== undefined && (
              <span className="vi-steps__bar" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={s.progress}>
                <span style={{ width: `${Math.max(0, Math.min(100, s.progress))}%` }} />
              </span>
            )}
          </span>
        </li>
      ))}
    </ol>
  );
}
