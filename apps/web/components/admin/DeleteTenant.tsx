"use client";

import { useActionState, useState } from "react";
import { Button, Field, Notice } from "@fxos/ui";
import { deleteTenant, type DeleteState } from "@/app/admin/actions";

export type DeleteLabels = {
  open: string;
  title: string;
  /** Contains {name}. */
  body: string;
  /** Contains {name}. */
  typeName: string;
  cancel: string;
  submit: string;
  pending: string;
};

/** A-S3: delete with type-to-confirm. Only rendered for dummy tenants; the API refuses real-data ones anyway. */
export function DeleteTenant({ id, name, labels }: { id: string; name: string; labels: DeleteLabels }) {
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState("");
  const [state, action, pending] = useActionState<DeleteState, FormData>(deleteTenant, {});

  if (!open) {
    return <Button variant="outline" size="sm" className="btn-danger-outline" onClick={() => setOpen(true)}>{labels.open}</Button>;
  }

  return (
    <form action={action} className="danger-confirm" role="group" aria-label={labels.title}>
      <strong>{labels.title}</strong>
      <p>{labels.body.replace("{name}", name)}</p>
      {state.error && <Notice tone="danger" title={state.error} />}
      <input type="hidden" name="id" value={id} />
      <Field label={labels.typeName.replace("{name}", name)} name="confirmName" autoComplete="off" autoFocus
        value={typed} onChange={(e) => setTyped(e.target.value)} />
      <div className="danger-confirm__actions">
        <Button variant="outline" onClick={() => { setOpen(false); setTyped(""); }}>{labels.cancel}</Button>
        <Button type="submit" variant="danger" disabled={typed.trim() !== name || pending}>{pending ? labels.pending : labels.submit}</Button>
      </div>
    </form>
  );
}
