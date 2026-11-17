"use client";

import { useState, type ReactNode } from "react";
import { cx } from "./util";

export type SwitchProps = {
  label: ReactNode;
  note?: ReactNode;
  /** Controlled value. Leave undefined and use defaultChecked for an uncontrolled switch. */
  checked?: boolean;
  defaultChecked?: boolean;
  onCheckedChange?: (checked: boolean) => void;
  /** With a name, the switch also submits "on"/"off" in a form. */
  name?: string;
  disabled?: boolean;
  className?: string;
};

/** Labelled on/off row (role="switch"). */
export function Switch({ label, note, checked, defaultChecked = false, onCheckedChange, name, disabled, className }: SwitchProps) {
  const [internal, setInternal] = useState(defaultChecked);
  const on = checked ?? internal;

  return (
    <>
      <button
        type="button"
        role="switch"
        aria-checked={on}
        disabled={disabled}
        className={cx("vi-switch", className)}
        onClick={() => {
          if (checked === undefined) setInternal(!on);
          onCheckedChange?.(!on);
        }}
      >
        <span className="vi-switch__track" aria-hidden="true">
          <span className="vi-switch__knob" />
        </span>
        <span className="vi-switch__text">
          <span className="vi-switch__label">{label}</span>
          {note && <span className="vi-switch__note">{note}</span>}
        </span>
      </button>
      {name && <input type="hidden" name={name} value={on ? "on" : "off"} />}
    </>
  );
}
