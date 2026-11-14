"use client";

import { useEffect, useId, useRef, useState } from "react";
import { openCompany } from "@/app/admin/actions";
import type { Company } from "@/lib/api";

export type TenantMenuLabels = {
  title: string;
  switchTo: string;
  real: string;
  dummy: string;
  manage: string;
};

function mono(name: string): string {
  const words = name.replace(/[^\p{L}\p{N} ]/gu, " ").trim().split(/\s+/).filter(Boolean);
  return ((words[0]?.[0] ?? "") + (words[1]?.[0] ?? words[0]?.[1] ?? "")).toUpperCase() || "?";
}

/** The list of companies; choosing one moves the session there (server action) and opens its dashboard. */
export function TenantList({ companies, labels, adminHref }: { companies: Company[]; labels: TenantMenuLabels; adminHref?: string }) {
  return (
    <div className="vi-tenants">
      <span className="vi-tenants__title">{labels.title}</span>
      <div role="menu" aria-label={labels.switchTo}>
        {companies.map((c) => (
          <form key={c.id} action={openCompany}>
            <input type="hidden" name="id" value={c.id} />
            <button type="submit" role="menuitemradio" aria-checked={c.current} className="vi-tenants__item">
              <span className={`vi-tenants__mono${c.isDummy ? "" : " vi-tenants__mono--real"}`} aria-hidden="true">{mono(c.displayName)}</span>
              <span className="vi-tenants__text">
                <strong>{c.displayName}</strong>
                <span>{c.isDummy ? labels.dummy : labels.real}</span>
              </span>
              {c.current && (
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" style={{ color: "var(--vi-violet)" }}>
                  <path d="M5 12l5 5L20 7" />
                </svg>
              )}
            </button>
          </form>
        ))}
      </div>
      {adminHref && <a href={adminHref} className="vi-tenants__link">{labels.manage} ↗</a>}
    </div>
  );
}

/** A-05: the current company above the user card; opens the list upward. */
export function TenantMenu({ companies, labels, adminHref }: { companies: Company[]; labels: TenantMenuLabels; adminHref?: string }) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const panelId = useId();
  const current = companies.find((c) => c.current);

  useEffect(() => {
    if (!open) return;
    const onPointer = (e: PointerEvent) => root.current && !root.current.contains(e.target as Node) && setOpen(false);
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("pointerdown", onPointer);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onPointer);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  if (!current) return null;
  return (
    <div ref={root} className="tenant-menu">
      {open && <div id={panelId} className="tenant-menu__panel"><TenantList companies={companies} labels={labels} adminHref={adminHref} /></div>}
      <button type="button" className="tenant-menu__trigger" aria-expanded={open} aria-controls={panelId}
        aria-label={`${labels.switchTo}: ${current.displayName}`} onClick={() => setOpen((v) => !v)}>
        <span className={`vi-tenants__mono${current.isDummy ? "" : " vi-tenants__mono--real"}`} aria-hidden="true">{mono(current.displayName)}</span>
        <span className="vi-tenants__text">
          <strong>{current.displayName}</strong>
          <span>{current.isDummy ? labels.dummy : labels.real}</span>
        </span>
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
          <path d="M7 15l5-5 5 5" />
        </svg>
      </button>
    </div>
  );
}
