"use client";

import { useActionState, useEffect, useState } from "react";
import { AuthHeading, Button, Field, Notice, StatusTile } from "@fxos/ui";
import { requestReset, type ForgotState } from "@/app/auth-actions";

export type ForgotLabels = {
  title: string;
  subtitle: string;
  email: string;
  submit: string;
  pending: string;
  sentTitle: string;
  /** Contains {email}. */
  sentBody: string;
  /** Contains {time}. */
  resendIn: string;
  resend: string;
  otherEmail: string;
  help: string;
  unavailable: string;
};

const COOLDOWN = 60;

/** T-07 → T-08: the request form, then "Check your email" with a resend countdown. */
export function ForgotForm({ labels }: { labels: ForgotLabels }) {
  const [state, action, pending] = useActionState<ForgotState, FormData>(requestReset, { sent: false, email: "" });
  const [editing, setEditing] = useState(false);
  const [left, setLeft] = useState(0);
  const [sentAt, setSentAt] = useState<ForgotState | null>(null);

  // Each successful send restarts the countdown (the API also sends at most once a minute).
  if (state.sent && sentAt !== state) {
    setSentAt(state);
    setEditing(false);
    setLeft(COOLDOWN);
  }

  useEffect(() => {
    if (left <= 0) return;
    const timer = setTimeout(() => setLeft((s) => s - 1), 1000);
    return () => clearTimeout(timer);
  }, [left]);

  if (state.sent && !editing) {
    const mmss = `${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")}`;
    return (
      <>
        <StatusTile kind="mail" />
        <AuthHeading title={labels.sentTitle}>
          {labels.sentBody.split("{email}")[0]}
          <strong>{state.email}</strong>
          {labels.sentBody.split("{email}")[1]}
        </AuthHeading>
        <div className="vi-auth__links">
          <form action={action}>
            <input type="hidden" name="email" value={state.email} />
            <Button type="submit" variant="ghost" size="sm" disabled={left > 0 || pending}>
              {left > 0 ? labels.resendIn.replace("{time}", mmss) : labels.resend}
            </Button>
          </form>
          <Button variant="ghost" size="sm" onClick={() => setEditing(true)}>{labels.otherEmail}</Button>
        </div>
        <p className="vi-auth__fine" style={{ textAlign: "left" }}>{labels.help}</p>
      </>
    );
  }

  return (
    <>
      <AuthHeading title={labels.title}>{labels.subtitle}</AuthHeading>
      <form action={action}>
        {state.error && <Notice tone="warning" title={labels.unavailable} />}
        <Field label={labels.email} name="email" type="email" autoComplete="username" required autoFocus
          defaultValue={state.email} />
        <Button type="submit" size="lg" full disabled={pending}>{pending ? labels.pending : labels.submit}</Button>
      </form>
    </>
  );
}
