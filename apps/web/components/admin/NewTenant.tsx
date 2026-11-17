"use client";

import { useActionState, useState } from "react";
import { Button, Checkbox, Drawer, Field, Notice } from "@fxos/ui";
import { createTenant, type CreateTenantState } from "@/app/admin/actions";

export type NewTenantLabels = {
  open: string;
  kicker: string;
  title: string;
  close: string;
  intro: string;
  name: string;
  nameHint: string;
  email: string;
  emailHint: string;
  realData: string;
  realDataHint: string;
  nextTitle: string;
  next1: string;
  next2: string;
  next3: string;
  cancel: string;
  submit: string;
  pending: string;
};

/** A-02: "New tenant" button and its drawer. */
export function NewTenant({ labels, size = "md" }: { labels: NewTenantLabels; size?: "md" | "lg" }) {
  const [open, setOpen] = useState(false);
  const [state, action, pending] = useActionState<CreateTenantState, FormData>(createTenant,
    { name: "", email: "", realData: false });

  return (
    <>
      <Button size={size} onClick={() => setOpen(true)}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
          <path d="M12 5v14M5 12h14" />
        </svg>
        {labels.open}
      </Button>
      <Drawer open={open} onClose={() => setOpen(false)} kicker={labels.kicker} title={labels.title} closeLabel={labels.close}
        footer={
          <>
            <Button variant="outline" size="lg" onClick={() => setOpen(false)}>{labels.cancel}</Button>
            <Button type="submit" size="lg" form="new-tenant" disabled={pending}>{pending ? labels.pending : labels.submit}</Button>
          </>
        }>
        <form id="new-tenant" action={action} className="stack-sm">
          <p className="drawer-intro">{labels.intro}</p>
          {state.error && <Notice tone="danger" title={state.error} />}
          <Field label={labels.name} name="name" required maxLength={200} emphasis defaultValue={state.name} hint={labels.nameHint} />
          <Field label={labels.email} name="email" type="email" required defaultValue={state.email} hint={labels.emailHint} />
          <Checkbox label={labels.realData} name="realData" defaultChecked={state.realData} />
          <p className="drawer-note">{labels.realDataHint}</p>
          <div className="drawer-next">
            <strong>{labels.nextTitle}</strong>
            <ol>
              <li>{labels.next1}</li>
              <li>{labels.next2}</li>
              <li>{labels.next3}</li>
            </ol>
          </div>
        </form>
      </Drawer>
    </>
  );
}
