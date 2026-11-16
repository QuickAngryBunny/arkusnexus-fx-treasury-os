"use client";

import { useActionState, useState, type FormEvent } from "react";
import { Button, Notice } from "@fxos/ui";
import { finishOnboarding, type FormState } from "@/app/settings/actions";
import { BankEditor, type BankEditorLabels } from "@/components/BankEditor";
import type { Bank } from "@/lib/api";

/** O-04 Your banks (step 3 of 3); a missing currency shows O-S2 instead of submitting. */
export function BanksStep({ initial, labels, submit, pending, note }: {
  initial: Bank[]; labels: BankEditorLabels; submit: string; pending: string; note: string;
}) {
  const [state, action, busy] = useActionState<FormState, FormData>(finishOnboarding, {});
  const [tried, setTried] = useState(false);

  const check = (e: FormEvent<HTMLFormElement>) => {
    const counts = String(new FormData(e.currentTarget).get("bankCounts") ?? "0,0").split(",").map(Number);
    if (!(counts[0]! > 0 && counts[1]! > 0)) {
      e.preventDefault();
      setTried(true);
    }
  };

  return (
    <form action={action} onSubmit={check} className="stack-sm">
      {state.error && <Notice tone="danger" title={state.error} />}
      <BankEditor initial={initial} labels={labels} showMissing={tried} />
      <Button type="submit" size="lg" full disabled={busy}>{busy ? pending : submit}</Button>
      <p className="vi-auth__fine">{note}</p>
    </form>
  );
}
