"use client";

import Link from "next/link";
import { useActionState } from "react";
import { Button, Checkbox, Field, Notice, PasswordField } from "@fxos/ui";
import { signIn, type SignInState } from "@/app/auth-actions";

export type SignInLabels = {
  email: string;
  password: string;
  forgot: string;
  remember: string;
  submit: string;
  pending: string;
  show: string;
  hide: string;
  invalidTitle: string;
  invalidBody: string;
  lockedTitle: string;
  /** Contains {time}. */
  lockedBody: string;
  noAccessTitle: string;
  noAccessBody: string;
  unavailable: string;
};

/** T-06 / T-S2. */
export function SignInForm({ labels, locale, next, notice }: {
  labels: SignInLabels; locale: string; next: string; notice?: string;
}) {
  const [state, action, pending] = useActionState<SignInState, FormData>(signIn, { email: "" });

  const lockedAt = state.lockedUntil
    ? new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" }).format(new Date(state.lockedUntil))
    : "";

  return (
    <form action={action} noValidate>
      {notice && !state.error && <Notice tone="warning" title={notice} />}
      {state.error === "invalid" && <Notice tone="danger" title={labels.invalidTitle}>{labels.invalidBody}</Notice>}
      {state.error === "locked" && (
        <Notice tone="danger" title={labels.lockedTitle}>{labels.lockedBody.replace("{time}", lockedAt)}</Notice>
      )}
      {state.error === "no_access" && <Notice tone="danger" title={labels.noAccessTitle}>{labels.noAccessBody}</Notice>}
      {state.error === "unavailable" && <Notice tone="warning" title={labels.unavailable} />}

      <input type="hidden" name="next" value={next} />
      <Field label={labels.email} name="email" type="email" autoComplete="username" required
        defaultValue={state.email} autoFocus={!state.email} />
      <PasswordField id="sign-in-password" label={labels.password} name="password" autoComplete="current-password" required
        showLabel={labels.show} hideLabel={labels.hide} autoFocus={Boolean(state.email)}
        aside={<Link href="/forgot-password">{labels.forgot}</Link>} />
      <Checkbox label={labels.remember} name="remember" defaultChecked />
      <Button type="submit" size="lg" full disabled={pending}>{pending ? labels.pending : labels.submit}</Button>
    </form>
  );
}
