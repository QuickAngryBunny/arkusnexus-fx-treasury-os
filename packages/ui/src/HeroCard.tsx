import type { ReactNode } from "react";
import { cx } from "./util";

export type HeroCardProps = {
  /** violet = today's headline number (with blobs); plum = a dark summary card. */
  tone?: "violet" | "plum";
  kicker: ReactNode;
  value: ReactNode;
  note?: ReactNode;
  /** Extra content under the note (e.g. a link). */
  children?: ReactNode;
  /** Accessible name for the card region. */
  label?: string;
  className?: string;
};

/** "Blob card · hero metric" from the design system: a big number on a rounded violet or plum card. */
export function HeroCard({ tone = "violet", kicker, value, note, children, label, className }: HeroCardProps) {
  return (
    <section aria-label={label} className={cx("vi-hero", `vi-hero--${tone}`, className)}>
      {tone === "violet" && (
        <>
          <span className="vi-hero__blob vi-hero__blob--a" aria-hidden="true" />
          <span className="vi-hero__blob vi-hero__blob--b" aria-hidden="true" />
        </>
      )}
      <div className="vi-hero__body">
        <span className="vi-hero__kicker">{kicker}</span>
        <span className="vi-hero__value">{value}</span>
        {note && <span className="vi-hero__note">{note}</span>}
        {children}
      </div>
    </section>
  );
}
