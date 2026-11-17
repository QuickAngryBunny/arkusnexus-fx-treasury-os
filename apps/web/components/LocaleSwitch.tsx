"use client";

import { useTransition } from "react";
import { Segmented } from "@fxos/ui";
import { setLocale } from "@/app/actions";
import type { Locale } from "@/lib/i18n";

type Props = { label: string; current: Locale; options: ReadonlyArray<{ value: Locale; label: string }> };

export function LocaleSwitch({ label, current, options }: Props) {
  const [pending, start] = useTransition();
  return (
    <div className="locale-switch" aria-busy={pending}>
      <Segmented label={label} options={options} value={current} onWhite onValueChange={(v) => start(() => setLocale(v))} />
    </div>
  );
}
