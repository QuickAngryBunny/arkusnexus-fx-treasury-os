import type { ReactNode } from "react";
import { cx, type LinkComponent } from "./util";

export type AlertTone = "info" | "danger" | "brand";

export type AlertBannerProps = {
  tone?: AlertTone;
  kicker?: ReactNode;
  title: ReactNode;
  children?: ReactNode;
  action?: { label: ReactNode; href: string };
  linkAs?: LinkComponent;
  /** Heading level for the title; defaults to h2. */
  headingLevel?: 2 | 3;
  className?: string;
};

/** Wide rounded banner (plum info, danger, violet brand) with an optional single action. */
export function AlertBanner({
  tone = "info", kicker, title, children, action, linkAs: Link = "a", headingLevel = 2, className,
}: AlertBannerProps) {
  const Heading = headingLevel === 3 ? "h3" : "h2";
  return (
    <section role={tone === "danger" ? "alert" : "status"} className={cx("vi-banner", `vi-banner--${tone}`, className)}>
      <span className="vi-banner__blob" aria-hidden="true" />
      <div className="vi-banner__text">
        {kicker && <span className="vi-banner__kicker">{kicker}</span>}
        <Heading className="vi-banner__title">{title}</Heading>
        {children && <p className="vi-banner__body">{children}</p>}
      </div>
      {action && (
        <Link href={action.href} className="vi-banner__action">
          {action.label}
        </Link>
      )}
    </section>
  );
}
