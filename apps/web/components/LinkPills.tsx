"use client";

import { useRouter } from "next/navigation";
import { Segmented } from "@fxos/ui";

/** Segmented pills that navigate: each option carries the URL it opens (views and periods live in the URL). */
export function LinkPills({ label, options, value }: { label: string; options: { value: string; label: string; href: string }[]; value: string }) {
  const router = useRouter();
  return (
    <Segmented
      label={label}
      options={options.map(({ value: v, label: l }) => ({ value: v, label: l }))}
      value={value}
      onValueChange={(v) => {
        const target = options.find((o) => o.value === v);
        if (target) router.push(target.href);
      }}
      onWhite
    />
  );
}
