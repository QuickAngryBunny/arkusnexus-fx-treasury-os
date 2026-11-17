import type { HTMLAttributes, ReactNode } from "react";
import { cx } from "./util";

export type BadgeTone = "violet" | "green" | "orange" | "neutral" | "plum";

export type BadgeProps = HTMLAttributes<HTMLSpanElement> & { tone?: BadgeTone; dot?: boolean; children: ReactNode };

/** Status pill. Tone is never the only signal: the label always says the status in words. */
export function Badge({ tone = "violet", dot = true, className, children, ...rest }: BadgeProps) {
  return (
    <span {...rest} className={cx("vi-badge", `vi-badge--${tone}`, className)}>
      {dot && <span className="vi-badge__dot" aria-hidden="true" />}
      {children}
    </span>
  );
}
