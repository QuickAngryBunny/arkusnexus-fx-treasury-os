"use client";

import { useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { cx } from "./util";

export type SegmentedOption<T extends string> = { value: T; label: ReactNode };

export type SegmentedProps<T extends string> = {
  /** Accessible name for the group, e.g. "Range". */
  label: string;
  options: ReadonlyArray<SegmentedOption<T>>;
  value?: T;
  defaultValue?: T;
  onValueChange?: (value: T) => void;
  /** Mist track, for use on a white card. The default white track sits on the canvas background. */
  onWhite?: boolean;
  /** With a name, the selected value also submits in a form. */
  name?: string;
  className?: string;
};

/** Single-choice pill group (radiogroup semantics, arrow keys move the choice). */
export function Segmented<T extends string>({
  label, options, value, defaultValue, onValueChange, onWhite, name, className,
}: SegmentedProps<T>) {
  const [internal, setInternal] = useState<T | undefined>(defaultValue ?? options[0]?.value);
  const selected = value ?? internal;
  const refs = useRef<Array<HTMLButtonElement | null>>([]);

  const choose = (next: T) => {
    if (value === undefined) setInternal(next);
    if (next !== selected) onValueChange?.(next);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const step = e.key === "ArrowRight" || e.key === "ArrowDown" ? 1 : e.key === "ArrowLeft" || e.key === "ArrowUp" ? -1 : 0;
    if (!step) return;
    e.preventDefault();
    const nextIndex = (index + step + options.length) % options.length;
    const next = options[nextIndex];
    if (!next) return;
    choose(next.value);
    refs.current[nextIndex]?.focus();
  };

  return (
    <div role="radiogroup" aria-label={label} className={cx("vi-seg", onWhite && "vi-seg--on-white", className)}>
      {options.map((o, i) => {
        const isOn = o.value === selected;
        return (
          <button
            key={o.value}
            ref={(el) => {
              refs.current[i] = el;
            }}
            type="button"
            role="radio"
            aria-checked={isOn}
            tabIndex={isOn ? 0 : -1}
            className="vi-seg__opt"
            onClick={() => choose(o.value)}
            onKeyDown={(e) => onKeyDown(e, i)}
          >
            {o.label}
          </button>
        );
      })}
      {name && selected !== undefined && <input type="hidden" name={name} value={selected} />}
    </div>
  );
}
