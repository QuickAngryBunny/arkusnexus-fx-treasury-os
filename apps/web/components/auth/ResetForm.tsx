"use client";

import { useActionState, useState, type ReactNode } from "react";
import { Button, Notice, PasswordField, PasswordRules } from "@fxos/ui";
import { resetPassword, type ResetState } from "@/app/auth-actions";
import { brokenRules, SHOWN_RULES, type PasswordRuleCode } from "@/lib/password";

export type ResetLabels = {
  newPassword: string;
  confirm: string;
  show: string;
  hide: string;
  rulesLabel: string;
  rules: Record<PasswordRuleCode, string>;
  matchEmpty: string;
  matchYes: string;
  matchNo: string;
  submit: string;
  pending: string;
  weak: string;
  unavailable: string;
};

/** T-10: new password with the live checklist; an invalid link swaps in the expired view (T-S1). */
export function ResetForm({ token, email, name, labels, expired }: {
  token: string; email: string; name: string; labels: ResetLabels;
  /** Rendered when the API says the link can't be used (another tab used it, or it expired meanwhile). */
  expired: Record<"expired" | "used", ReactNode>;
}) {
  const [state, action, pending] = useActionState<ResetState, FormData>(resetPassword, {});
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");

  if (state.error === "invalid_link") {
    return expired[state.linkState === "used" ? "used" : "expired"];
  }

  const broken = brokenRules(password, email, name);
  const shown = [...SHOWN_RULES, ...(broken.includes("max_length") ? (["max_length"] as const) : [])];
  const match = confirm.length > 0 && confirm === password;
  const ready = broken.length === 0 && match;

  return (
    <form action={action}>
      {state.error === "weak" && <Notice tone="danger" title={labels.weak} />}
      {state.error === "unavailable" && <Notice tone="warning" title={labels.unavailable} />}
      <input type="hidden" name="token" value={token} />
      {/* Lets password managers save the new password against the right account. */}
      <input type="hidden" name="username" autoComplete="username" value={email} />
      <PasswordField id="new-password" label={labels.newPassword} name="password" autoComplete="new-password" required autoFocus
        value={password} onChange={(e) => setPassword(e.target.value)} showLabel={labels.show} hideLabel={labels.hide} />
      <PasswordRules label={labels.rulesLabel}
        rules={shown.map((code) => ({ id: code, label: labels.rules[code], met: password.length > 0 && !broken.includes(code) }))} />
      <PasswordField id="confirm-password" label={labels.confirm} name="confirm" autoComplete="new-password" required
        value={confirm} onChange={(e) => setConfirm(e.target.value)} showLabel={labels.show} hideLabel={labels.hide}
        hint={!confirm ? labels.matchEmpty : match ? labels.matchYes : labels.matchNo} />
      <Button type="submit" size="lg" full disabled={!ready || pending}>{pending ? labels.pending : labels.submit}</Button>
    </form>
  );
}
