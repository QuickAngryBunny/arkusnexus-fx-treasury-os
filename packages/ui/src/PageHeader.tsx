import type { ReactNode } from "react";
import { cx } from "./util";

export type PageHeaderProps = {
  title: ReactNode;
  eyebrow?: ReactNode;
  /** A <Breadcrumb/>; it renders above the eyebrow and title. */
  breadcrumb?: ReactNode;
  /** Buttons on the right (one primary at most). */
  actions?: ReactNode;
  className?: string;
};

/** Page title block: breadcrumb, eyebrow (context line) and the plum h1. */
export function PageHeader({ title, eyebrow, breadcrumb, actions, className }: PageHeaderProps) {
  return (
    <header className={cx("vi-page-header", className)}>
      {breadcrumb && <div className="vi-page-header__crumbs">{breadcrumb}</div>}
      <div className="vi-page-header__text">
        {eyebrow && <span className="vi-page-header__eyebrow">{eyebrow}</span>}
        <h1 className="vi-page-header__title">{title}</h1>
      </div>
      {actions && <div className="vi-page-header__actions">{actions}</div>}
    </header>
  );
}
