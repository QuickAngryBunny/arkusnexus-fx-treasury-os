"use client";

import { useState, type InputHTMLAttributes, type ReactNode } from "react";
import { cx } from "./util";

export type PasswordFieldProps = {
  label: ReactNode;
  /** Shown to the right of the label, e.g. the "Forgot password?" link. */
  aside?: ReactNode;
  hint?: ReactNode;
  error?: ReactNode;
  showLabel: string;
  hideLabel: string;
} & Omit<InputHTMLAttributes<HTMLInputElement>, "type" | "children">;

/** Password input with a Show/Hide toggle inside the field (T-10). */
export function PasswordField({ label, aside, hint, error, showLabel, hideLabel, className, ...input }: PasswordFieldProps) {
  const [visible, setVisible] = useState(false);
  return (
    <div className={cx("vi-field", error ? "vi-field--invalid" : null, className)}>
      <span className="vi-field__label-row">
        <label htmlFor={input.id}>{label}</label>
        {aside}
      </span>
      <span className="vi-password">
        <input {...input} type={visible ? "text" : "password"} aria-invalid={error ? true : undefined} className="vi-field__control" />
        <button type="button" className="vi-password__toggle" aria-pressed={visible} aria-controls={input.id}
          onClick={() => setVisible((v) => !v)}>
          {visible ? hideLabel : showLabel}
        </button>
      </span>
      {error ? (
        <span className="vi-field__error" role="alert">{error}</span>
      ) : (
        hint && <span className="vi-field__hint">{hint}</span>
      )}
    </div>
  );
}
