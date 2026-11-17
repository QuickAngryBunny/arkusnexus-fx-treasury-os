"use client";

import Link from "next/link";
import { useActionState } from "react";
import { Button, Notice } from "@fxos/ui";
import { confirmEmail, type ConfirmEmailState } from "@/app/auth-actions";

/** One button rather than confirming on page load, so link scanners in mail clients can't use the link. */
export function ConfirmEmailForm({ token, labels }: {
  token: string;
  labels: { submit: string; pending: string; done: string; signIn: string; invalid: string; taken: string; unavailable: string };
}) {
  const [state, action, pending] = useActionState<ConfirmEmailState, FormData>(confirmEmail, {});
  if (state.email) {
    return (
      <>
        <Notice tone="success" title={labels.done.replace("{email}", state.email)} />
        <Button href="/sign-in" linkAs={Link} size="lg" full>{labels.signIn}</Button>
      </>
    );
  }

  return (
    <form action={action}>
      {state.error && (
        <Notice tone="danger" title={state.error === "email_taken" ? labels.taken : state.error === "invalid_link" ? labels.invalid : labels.unavailable} />
      )}
      <input type="hidden" name="token" value={token} />
      <Button type="submit" size="lg" full disabled={pending}>{pending ? labels.pending : labels.submit}</Button>
    </form>
  );
}
