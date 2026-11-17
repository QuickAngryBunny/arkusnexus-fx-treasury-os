"use client";

import { useState } from "react";
import { Notice, TagGroup, type Tag } from "@fxos/ui";
import type { Bank } from "@/lib/api";

export type BankEditorLabels = {
  mxnTitle: string;
  usdTitle: string;
  mxnPlaceholder: string;
  usdPlaceholder: string;
  add: string;
  addLabel: string;
  suggestions: string;
  remove: string;
  rename: string;
  empty: string;
  missingOne: string;
  save: string;
  cancel: string;
  missingTitle: { both: string; mxn: string; usd: string };
  missingBody: string;
};

/** Common Mexican and US banks offered as one-tap suggestions; the team can type any name. */
const SUGGESTIONS = {
  MXN: ["BBVA México", "Banorte", "Santander México", "Banamex", "HSBC México", "Scotiabank", "Banregio"],
  USD: ["Bank of America", "Wells Fargo", "Chase", "Citi", "BBVA Dólares", "Mercury"],
} as const;

type Row = { key: string; id?: string; name: string; currency: "MXN" | "USD" };

/**
 * The MXN and USD bank tags (O-04, C-01), posted as JSON in a hidden "banks" field. With `rename`, clicking a name
 * edits it (the id stays, so plans that use the bank follow the new name).
 */
export function BankEditor({ initial, labels, rename = false, showMissing }: {
  initial: Bank[]; labels: BankEditorLabels; rename?: boolean;
  /** Set after a submit attempt: shows O-S2 for a missing currency. */
  showMissing?: boolean;
}) {
  const [rows, setRows] = useState<Row[]>(initial.map((b) => ({ key: b.id, id: b.id, name: b.name, currency: b.currency })));
  const of = (cur: "MXN" | "USD"): Tag[] => rows.filter((r) => r.currency === cur).map((r) => ({ key: r.key, name: r.name }));
  const missingMxn = showMissing && of("MXN").length === 0;
  const missingUsd = showMissing && of("USD").length === 0;

  const group = (cur: "MXN" | "USD") => (
    <TagGroup
      currency={cur}
      title={cur === "MXN" ? labels.mxnTitle : labels.usdTitle}
      tags={of(cur)}
      suggestions={[...SUGGESTIONS[cur]]}
      missing={(cur === "MXN" ? missingMxn : missingUsd) ? labels.missingOne.replace("{currency}", cur) : undefined}
      onAdd={(name) => setRows((r) => [...r, { key: `new-${cur}-${name}-${r.length}`, name, currency: cur }])}
      onRemove={(key) => setRows((r) => r.filter((x) => x.key !== key))}
      onRename={rename ? (key, name) => setRows((r) => r.map((x) => (x.key === key ? { ...x, name } : x))) : undefined}
      labels={{
        add: labels.add,
        placeholder: cur === "MXN" ? labels.mxnPlaceholder : labels.usdPlaceholder,
        inputLabel: labels.addLabel.replace("{currency}", cur),
        suggestions: labels.suggestions,
        remove: (name) => labels.remove.replace("{name}", name),
        rename: (name) => labels.rename.replace("{name}", name),
        empty: labels.empty,
        save: labels.save,
        cancel: labels.cancel,
      }}
    />
  );

  return (
    <>
      {(missingMxn || missingUsd) && (
        <Notice tone="danger" title={missingMxn && missingUsd ? labels.missingTitle.both : missingMxn ? labels.missingTitle.mxn : labels.missingTitle.usd}>
          {labels.missingBody}
        </Notice>
      )}
      <input type="hidden" name="banks" value={JSON.stringify(rows.map(({ id, name, currency }) => ({ id, name, currency })))} />
      <input type="hidden" name="bankCounts" value={`${of("MXN").length},${of("USD").length}`} />
      {group("MXN")}
      {group("USD")}
    </>
  );
}
