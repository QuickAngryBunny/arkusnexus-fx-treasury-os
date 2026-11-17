import type { InputHTMLAttributes, ReactNode, SelectHTMLAttributes } from "react";
import { cx } from "./util";

type FieldFrame = {
  label: ReactNode;
  hint?: ReactNode;
  /** Shown instead of the hint and marks the control invalid. */
  error?: ReactNode;
  /** Large, violet-bordered value: for the one key number on a form (a rate, an amount). */
  emphasis?: boolean;
};

function Frame({ label, hint, error, emphasis, className, children }: FieldFrame & { className?: string; children: ReactNode }) {
  return (
    <label className={cx("vi-field", emphasis && "vi-field--emphasis", error ? "vi-field--invalid" : null, className)}>
      {label}
      {children}
      {error ? (
        <span className="vi-field__error" role="alert">
          {error}
        </span>
      ) : (
        hint && <span className="vi-field__hint">{hint}</span>
      )}
    </label>
  );
}

export type FieldProps = FieldFrame & Omit<InputHTMLAttributes<HTMLInputElement>, "children">;

/** Labelled text input. The label wraps the control, so no id wiring is needed. */
export function Field({ label, hint, error, emphasis, className, type = "text", ...input }: FieldProps) {
  return (
    <Frame label={label} hint={hint} error={error} emphasis={emphasis} className={className}>
      <input {...input} type={type} aria-invalid={error ? true : undefined} className="vi-field__control" />
    </Frame>
  );
}

export type SelectFieldProps = FieldFrame & SelectHTMLAttributes<HTMLSelectElement> & { children: ReactNode };

/** Labelled native select. Pass <option> elements as children. */
export function SelectField({ label, hint, error, emphasis, className, children, ...select }: SelectFieldProps) {
  return (
    <Frame label={label} hint={hint} error={error} emphasis={emphasis} className={className}>
      <select {...select} aria-invalid={error ? true : undefined} className="vi-field__control">
        {children}
      </select>
    </Frame>
  );
}
