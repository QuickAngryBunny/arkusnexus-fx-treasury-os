"use client";

import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { cx, initials } from "./util";

export type UserMenuProps = {
  name: string;
  email?: string;
  /** Accessible label for the trigger, e.g. "Ana Ruiz — open account menu". */
  triggerLabel: string;
  /** Menu content: MenuItem, MenuSection, MenuDivider or any element (forms are fine). */
  children: ReactNode;
  className?: string;
};

/** Account card at the bottom of AppNav that opens a menu. Escape or a click outside closes it. */
export function UserMenu({ name, email, triggerLabel, children, className }: UserMenuProps) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const panelId = useId();

  useEffect(() => {
    if (!open) return;
    const onPointer = (e: PointerEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setOpen(false);
        trigger.current?.focus();
      }
    };
    document.addEventListener("pointerdown", onPointer);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onPointer);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <div ref={root} className={cx("vi-user", className)}>
      {open && (
        <div id={panelId} className="vi-user__panel">
          {children}
        </div>
      )}
      <button
        ref={trigger}
        type="button"
        className="vi-user__trigger"
        aria-expanded={open}
        aria-controls={panelId}
        aria-label={triggerLabel}
        onClick={() => setOpen((v) => !v)}
      >
        <span className="vi-avatar" aria-hidden="true">
          {initials(name)}
        </span>
        <span className="vi-user__id">
          <strong>{name}</strong>
          {email && <span>{email}</span>}
        </span>
        <svg className="vi-user__chev" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor"
          strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M9 18l6-6-6-6" />
        </svg>
      </button>
    </div>
  );
}

export function MenuSection({ children }: { children: ReactNode }) {
  return <div className="vi-menu__section">{children}</div>;
}

export function MenuDivider() {
  return <hr className="vi-menu__divider" />;
}

/** A menu row: a link when `href` is set, otherwise a button (type="submit" works inside a form). */
export function MenuItem({
  children, href, type = "button", disabled, onClick,
}: {
  children: ReactNode;
  href?: string;
  type?: "button" | "submit";
  disabled?: boolean;
  onClick?: () => void;
}) {
  if (href && !disabled) {
    return (
      <a href={href} className="vi-menu__item">
        {children}
      </a>
    );
  }
  return (
    <button type={type} className="vi-menu__item" disabled={disabled} onClick={onClick}>
      {children}
    </button>
  );
}
