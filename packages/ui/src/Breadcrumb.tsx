import type { ReactNode } from "react";
import { cx, type LinkComponent } from "./util";

export type Crumb = { label: ReactNode; href?: string };

export type BreadcrumbProps = {
  /** From the section down to the current page. The last item is the current page. */
  items: ReadonlyArray<Crumb>;
  /** Accessible name of the nav landmark ("Breadcrumb" / "Ruta de navegación"). */
  label?: string;
  linkAs?: LinkComponent;
  className?: string;
};

/** Inner pages show this above the title instead of a back button. */
export function Breadcrumb({ items, label = "Breadcrumb", linkAs: Link = "a", className }: BreadcrumbProps) {
  return (
    <nav aria-label={label} className={cx("vi-crumbs", className)}>
      <ol>
        {items.map((c, i) => {
          const last = i === items.length - 1;
          return (
            <li key={i}>
              {i > 0 && (
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5"
                  strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                  <path d="M9 18l6-6-6-6" />
                </svg>
              )}
              {!last && c.href ? <Link href={c.href}>{c.label}</Link> : <span aria-current={last ? "page" : undefined}>{c.label}</span>}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
