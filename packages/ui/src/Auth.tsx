import type { InputHTMLAttributes, ReactNode } from "react";
import { BrandMark } from "./icons";
import { cx } from "./util";

export type AuthShellProps = {
  /** Small caps line above the title on the plum panel, e.g. the company name. */
  eyebrow: ReactNode;
  /** Big panel title ("Welcome back", "It happens"). */
  title: ReactNode;
  /** One or two sentences under the panel title. */
  body: ReactNode;
  /** Bottom line of the panel. */
  footer?: ReactNode;
  /** Product name next to the mark; defaults to the English name. */
  productName?: ReactNode;
  productTagline?: ReactNode;
  children: ReactNode;
};

/**
 * Signed-out pages (T-06 to T-11, T-S1, T-S2): a plum panel with the brand on the left and a 420px form column on the
 * right. The panel stacks above the form on narrow screens.
 */
export function AuthShell({
  eyebrow, title, body, footer, productName = "FX Intelligence", productTagline = "Treasury OS", children,
}: AuthShellProps) {
  return (
    <div className="vi-auth">
      <aside className="vi-auth__panel">
        <span className="vi-auth__blob vi-auth__blob--a" aria-hidden="true" />
        <span className="vi-auth__blob vi-auth__blob--b" aria-hidden="true" />
        <span className="vi-auth__brand">
          <BrandMark />
          <strong>
            {productName}
            <span>{productTagline}</span>
          </strong>
        </span>
        <div className="vi-auth__pitch">
          <span className="vi-auth__eyebrow">{eyebrow}</span>
          <span className="vi-auth__title">{title}</span>
          <span className="vi-auth__body">{body}</span>
        </div>
        {footer ? <span className="vi-auth__footer">{footer}</span> : <span />}
      </aside>
      <main className="vi-auth__main">
        <div className="vi-auth__form">{children}</div>
      </main>
    </div>
  );
}

/** Page heading of a signed-out form: the h1 and a line under it. */
export function AuthHeading({ title, children }: { title: ReactNode; children?: ReactNode }) {
  return (
    <div className="vi-auth__heading">
      <h1>{title}</h1>
      {children && <p>{children}</p>}
    </div>
  );
}

export type NoticeTone = "success" | "danger" | "warning";

/** A rounded inline message with a round mark: success (T-06 joined), danger (T-S2), warning. */
export function Notice({ tone, title, children, className }: { tone: NoticeTone; title: ReactNode; children?: ReactNode; className?: string }) {
  const mark = tone === "success" ? "✓" : "!";
  return (
    <div role={tone === "success" ? "status" : "alert"} className={cx("vi-notice", `vi-notice--${tone}`, className)}>
      <span className="vi-notice__mark" aria-hidden="true">{mark}</span>
      <span className="vi-notice__text">
        <strong>{title}</strong>
        {children && <span>{children}</span>}
      </span>
    </div>
  );
}

export type StatusTileKind = "mail" | "clock" | "check";

const tilePaths: Record<StatusTileKind, string> = {
  mail: "M3 6h18v12H3zM3 7l9 6 9-6",
  clock: "M12 7v5l3 2M12 21a9 9 0 1 0 0-18a9 9 0 0 0 0 18z",
  check: "M5 12.5l4.5 4.5L19 7.5",
};

/** The 64px icon tile at the top of T-08 (mail), T-S1 (clock) and T-11 (check). */
export function StatusTile({ kind }: { kind: StatusTileKind }) {
  return (
    <span className={cx("vi-tile", `vi-tile--${kind}`)} aria-hidden="true">
      <svg width="30" height="30" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={kind === "check" ? 3 : 2}
        strokeLinecap="round" strokeLinejoin="round">
        <path d={tilePaths[kind]} />
      </svg>
    </span>
  );
}

export type CheckboxProps = { label: ReactNode } & Omit<InputHTMLAttributes<HTMLInputElement>, "type" | "children">;

/** A labelled checkbox ("Keep me signed in on this computer for 7 days"). */
export function Checkbox({ label, className, ...input }: CheckboxProps) {
  return (
    <label className={cx("vi-check", className)}>
      <input type="checkbox" {...input} />
      {label}
    </label>
  );
}

export type PasswordRule = { id: string; label: ReactNode; met: boolean };

/** The live password checklist under a new-password field (T-10, O-02, T-05). */
export function PasswordRules({ rules, label }: { rules: PasswordRule[]; label: string }) {
  return (
    <ul className="vi-rules" aria-label={label}>
      {rules.map((rule) => (
        <li key={rule.id} className={cx("vi-rules__item", rule.met && "vi-rules__item--met")}>
          <span className="vi-rules__mark" aria-hidden="true">{rule.met ? "✓" : ""}</span>
          {rule.label}
        </li>
      ))}
    </ul>
  );
}
