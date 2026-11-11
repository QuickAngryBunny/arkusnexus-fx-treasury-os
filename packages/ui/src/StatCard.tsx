import type { ReactNode } from "react";
import { cx, type LinkComponent } from "./util";

export type StatTone = "positive" | "negative" | "neutral";

export type StatCardProps = {
  label: ReactNode;
  /** Pre-formatted value. Money values include their currency code. */
  value: ReactNode;
  note?: ReactNode;
  tone?: StatTone;
  /** Shown before the value (e.g. a trend arrow); inherits the value colour. */
  icon?: ReactNode;
  /** Colour of the value itself (the note keeps `tone`). */
  valueTone?: "default" | "positive" | "negative" | "brand";
  /** Small uppercase label, as on the Forecast Intelligence cards. */
  kicker?: boolean;
  /** Extra content under the note (e.g. a level bar). */
  children?: ReactNode;
  href?: string;
  linkAs?: LinkComponent;
  className?: string;
};

/** Flat white card with a label, a big value and an optional coloured note. Links when `href` is set. */
export function StatCard({
  label, value, note, tone = "neutral", icon, valueTone = "default", kicker, children, href, linkAs: Link = "a", className,
}: StatCardProps) {
  const body = (
    <>
      <span className={cx("vi-stat__label", kicker && "vi-stat__label--kicker")}>{label}</span>
      <span className={cx("vi-stat__value", valueTone !== "default" && `vi-stat__value--${valueTone}`, icon ? "vi-stat__value--icon" : null)}>
        {icon}
        {value}
      </span>
      {note && <span className={cx("vi-stat__note", `vi-stat__note--${tone}`)}>{note}</span>}
      {children}
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
