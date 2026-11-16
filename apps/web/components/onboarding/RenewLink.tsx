"use client";

import { useActionState } from "react";
import { Button, Notice } from "@fxos/ui";
import { renewInvitation, type RenewState } from "@/app/auth-actions";

/** O-S1 "Send me a new link" / T-S1 "Send a new invitation link": emails a fresh link to the same address. */
export function RenewLink({ token, labels }: { token: string; labels: { submit: string; pending: string; sent: string; notSent: string } }) {
  const [state, action, pending] = useActionState<RenewState, FormData>(renewInvitation, {});
  if (state.sent === true) return <Notice tone="success" title={labels.sent} />;
  return (
    <form action={action}>
      {state.sent === false && <Notice tone="warning" title={labels.notSent} />}
      <input type="hidden" name="token" value={token} />
      <Button type="submit" size="lg" full disabled={pending}>{pending ? labels.pending : labels.submit}</Button>
    </form>
  );
}
