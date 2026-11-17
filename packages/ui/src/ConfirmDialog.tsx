"use client";

import { useEffect, useId, useRef, type ReactNode } from "react";
import { Button } from "./Button";

export type ConfirmDialogProps = {
  open: boolean;
  title: ReactNode;
  children?: ReactNode;
  confirmLabel: string;
  cancelLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
  /** Danger styling for a destructive action (delete). */
  danger?: boolean;
  pending?: boolean;
};

/**
 * Small centred confirmation (alertdialog) above everything else, drawers included. Escape and the scrim cancel;
 * focus starts on Cancel so Enter never confirms a destructive action by accident.
 */
export function ConfirmDialog({ open, title, children, confirmLabel, cancelLabel, onConfirm, onCancel, danger, pending }: ConfirmDialogProps) {
  const titleId = useId();
  const panel = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    panel.current?.querySelector<HTMLButtonElement>("[data-cancel]")?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        onCancel();
      }
    };
    document.addEventListener("keydown", onKey, true);
    return () => document.removeEventListener("keydown", onKey, true);
  }, [open, onCancel]);

  if (!open) return null;
  return (
    <div className="vi-confirm">
      <button type="button" className="vi-confirm__scrim" aria-label={cancelLabel} onClick={onCancel} tabIndex={-1} />
      <div ref={panel} role="alertdialog" aria-modal="true" aria-labelledby={titleId} className="vi-confirm__panel">
        <h2 id={titleId}>{title}</h2>
        {children && <div className="vi-confirm__body">{children}</div>}
        <div className="vi-confirm__actions">
          <Button data-cancel variant="outline" onClick={onCancel} disabled={pending}>{cancelLabel}</Button>
          <Button variant={danger ? "danger" : "primary"} onClick={onConfirm} disabled={pending}>{confirmLabel}</Button>
        </div>
      </div>
    </div>
  );
}
