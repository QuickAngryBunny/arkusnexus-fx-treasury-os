import type { ReactNode } from "react";
import { cx, type LinkComponent } from "./util";

export type StatTone = "positive" | "negative" | "neutral";

export type StatCardProps = {
  label: ReactNode;
  /** Pre-formatted value. Money values include their currency code. */
  value: ReactNode;
  note?: ReactNode;
  tone?: StatTone;
  href?: string;
  linkAs?: LinkComponent;
  className?: string;
};

/** Flat white card with a label, a big value and an optional coloured note. Links when `href` is set. */
export function StatCard({ label, value, note, tone = "neutral", href, linkAs: Link = "a", className }: StatCardProps) {
  const body = (
    <>
      <span className="vi-stat__label">{label}</span>
      <span className="vi-stat__value">{value}</span>
      {note && <span className={cx("vi-stat__note", `vi-stat__note--${tone}`)}>{note}</span>}
    </>
  );

  return href ? (
    <Link href={href} className={cx("vi-stat", className)}>
      {body}
    </Link>
  ) : (
    <section className={cx("vi-stat", className)}>{body}</section>
  );
}
