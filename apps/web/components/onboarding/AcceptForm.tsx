"use client";

import { useActionState, useState, type ReactNode } from "react";
import { Button, Field, Notice, PasswordField, PasswordRules } from "@fxos/ui";
import { acceptInvitation, type AcceptState } from "@/app/auth-actions";
import { brokenRules, SHOWN_RULES, type PasswordRuleCode } from "@/lib/password";

export type AcceptLabels = {
  name: string;
  email: string;
  emailHint?: string;
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
  tenantUnavailable: string;
};

/**
 * O-02 (setup: name editable, email locked) and T-05 (join: who is joining shown as a card). Sets the password with
 * the live rules, then signs in. A link that stopped working meanwhile swaps in the expired view.
 */
export function AcceptForm({ token, email, name, editableName, person, labels, expired }: {
  token: string; email: string; name: string; editableName: boolean; person?: ReactNode; labels: AcceptLabels; expired: ReactNode;
}) {
  const [state, action, pending] = useActionState<AcceptState, FormData>(acceptInvitation, {});
  const [displayName, setDisplayName] = useState(name);
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");

  if (state.error === "invalid_link") return <>{expired}</>;

  const broken = brokenRules(password, email, displayName);
  const shown = [...SHOWN_RULES, ...(broken.includes("max_length") ? (["max_length"] as const) : [])];
  const match = confirm.length > 0 && confirm === password;
  const ready = broken.length === 0 && match && displayName.trim().length > 0;

  return (
    <form action={action}>
      {state.error === "weak" && <Notice tone="danger" title={labels.weak} />}
      {state.error === "unavailable" && <Notice tone="warning" title={labels.unavailable} />}
      {state.error === "tenant_unavailable" && <Notice tone="warning" title={labels.tenantUnavailable} />}
      <input type="hidden" name="token" value={token} />
      {person}
      {editableName ? (
        <Field label={labels.name} name="displayName" autoComplete="name" required maxLength={200}
          value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
      ) : (
        <input type="hidden" name="displayName" value={displayName} />
      )}
      {editableName && <Field label={labels.email} type="email" value={email} readOnly aria-readonly hint={labels.emailHint} />}
      <input type="hidden" name="username" autoComplete="username" value={email} />
      <PasswordField id="new-password" label={labels.newPassword} name="password" autoComplete="new-password" required autoFocus={!editableName}
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
