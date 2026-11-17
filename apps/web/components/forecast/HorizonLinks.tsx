"use client";

import { useRouter } from "next/navigation";
import { Segmented } from "@fxos/ui";

/** Horizon pills that switch the page by URL (?h=30), so a horizon can be linked and reloaded. */
export function HorizonLinks({ label, options, value }: { label: string; options: { value: string; label: string }[]; value: string }) {
  const router = useRouter();
  return <Segmented label={label} options={options} value={value} onValueChange={(v) => router.push(`/forecast/models?h=${v}`)} />;
}
