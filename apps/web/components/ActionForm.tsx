"use client";

import { useActionState, type ReactNode } from "react";
import { Button, Notice } from "@fxos/ui";
import type { FormState } from "@/app/settings/actions";

/**
 * A form posting to a server action, with its error / success notice and submit button. Fields are passed as children,
 * so they can be server-rendered. `messages` turns API codes (e.g. "wrong_password") into words.
 */
export function ActionForm({ action, children, submit, pending, saved, savedEmail, messages = {}, className, extraActions, full }: {
  action: (prev: FormState, form: FormData) => Promise<FormState>;
  children: ReactNode;
  submit: string;
  pending: string;
  saved?: string;
  /** Shown instead of `saved` when the action sent a confirmation email; "{email}" is replaced with the address. */
  savedEmail?: string;
  messages?: Record<string, string>;
  className?: string;
  extraActions?: ReactNode;
  full?: boolean;
}) {
  const [state, run, busy] = useActionState<FormState, FormData>(action, {});
  const error = state.error ? (messages[state.error] ?? state.error) : null;
  const ok = !state.ok ? null : state.emailSent && savedEmail ? savedEmail.replace("{email}", state.emailSent) : (saved ?? null);
  return (
    <form action={run} className={className ?? "stack-sm"}>
      {error && <Notice tone="danger" title={error} />}
      {ok && <Notice tone="success" title={ok} />}
      {children}
      <div className="form-actions">
        {extraActions}
        <Button type="submit" size="lg" full={full} disabled={busy}>{busy ? pending : submit}</Button>
      </div>
    </form>
  );
}
