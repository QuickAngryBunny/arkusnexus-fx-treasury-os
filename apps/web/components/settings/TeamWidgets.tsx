"use client";

import { useActionState, useEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { Button, Drawer, Field, Notice, Segmented } from "@fxos/ui";
import { inviteTeammate, type InviteState } from "@/app/settings/actions";

export type InviteLabels = {
  open: string;
  kicker: string;
  title: string;
  close: string;
  name: string;
  email: string;
  role: string;
  standard: string;
  standardNote: string;
  approver: string;
  approverNote: string;
  note: string;
  noteHint: string;
  footer: string;
  cancel: string;
  submit: string;
  pending: string;
  /** Contains {email}. */
  sent: string;
  sentBody: string;
  /** T-S3. Contains {email}. */
  already: string;
  alreadyBody: string;
};

/** T-02 invite drawer → T-03 "Invitation sent" notice; T-S3 when the email is already in the company. */
export function InviteTeammate({ labels }: { labels: InviteLabels }) {
  const [open, setOpen] = useState(false);
  const [state, action, pending] = useActionState<InviteState, FormData>(inviteTeammate, {});
  const [role, setRole] = useState<"standard" | "approver">("standard");
  const lastOk = useRef<InviteState | null>(null);
  const [sentTo, setSentTo] = useState<string | null>(null);

  // A successful send closes the drawer and shows T-03 as a toast for a few seconds.
  useEffect(() => {
    if (state.ok && lastOk.current !== state) {
      lastOk.current = state;
      setOpen(false);
      setSentTo(state.sentTo ?? null);
      const timer = setTimeout(() => setSentTo(null), 8000);
      return () => clearTimeout(timer);
    }
  }, [state]);

  return (
    <>
      {sentTo && (
        <div className="toast" role="status">
          <Notice tone="success" title={labels.sent.replace("{email}", sentTo)}>{labels.sentBody}</Notice>
        </div>
      )}
      <Button onClick={() => setOpen(true)}>+ {labels.open}</Button>
      <Drawer open={open} onClose={() => setOpen(false)} kicker={labels.kicker} title={labels.title} closeLabel={labels.close}
        footer={
          <>
            <Button variant="outline" size="lg" onClick={() => setOpen(false)}>{labels.cancel}</Button>
            <Button type="submit" size="lg" form="invite-form" disabled={pending}>{pending ? labels.pending : labels.submit}</Button>
          </>
        }>
        <form id="invite-form" action={action} className="stack-sm">
          {state.alreadyMember && (
            <Notice tone="warning" title={labels.already.replace("{email}", state.values?.email ?? "")}>{labels.alreadyBody}</Notice>
          )}
          {state.error && !state.alreadyMember && <Notice tone="danger" title={state.error} />}
          <Field label={labels.name} name="displayName" required maxLength={200} defaultValue={state.values?.displayName} />
          <Field label={labels.email} name="email" type="email" required defaultValue={state.values?.email}
            error={state.alreadyMember ? labels.already.replace("{email}", state.values?.email ?? "") : undefined} />
          <div className="field-block">
            <span className="field-label">{labels.role}</span>
            <Segmented label={labels.role} name="role" value={role} onValueChange={(v) => setRole(v)}
              options={[{ value: "standard", label: labels.standard }, { value: "approver", label: labels.approver }]} />
            <span className="field-hint">{role === "approver" ? labels.approverNote : labels.standardNote}</span>
          </div>
          <label className="vi-field">
            {labels.note}
            <textarea name="note" maxLength={500} rows={3} className="vi-field__control textarea" defaultValue={state.values?.note} />
            <span className="vi-field__hint">{labels.noteHint}</span>
          </label>
          <p className="drawer-note">{labels.footer}</p>
        </form>
      </Drawer>
    </>
  );
}

/**
 * The ⋯ menu on a team row: a list of small forms (server actions). The panel is fixed to the viewport so the
 * scrolling table can't clip it; it opens upwards when there's no room below and closes on scroll or resize.
 */
export function RowMenu({ label, children }: { label: string; children: ReactNode }) {
  const [pos, setPos] = useState<CSSProperties | null>(null);
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const open = pos !== null;

  const toggle = () => {
    if (open || !trigger.current) return setPos(null);
    const r = trigger.current.getBoundingClientRect();
    const right = window.innerWidth - r.right;
    setPos(window.innerHeight - r.bottom < 240 ? { bottom: window.innerHeight - r.top + 4, right } : { top: r.bottom + 4, right });
  };

  useEffect(() => {
    if (!open) return;
    const close = () => setPos(null);
    const outside = (e: PointerEvent) => root.current && !root.current.contains(e.target as Node) && close();
    const key = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      close();
      trigger.current?.focus();
    };
    document.addEventListener("pointerdown", outside);
    document.addEventListener("keydown", key);
    window.addEventListener("scroll", close, true);
    window.addEventListener("resize", close);
    return () => {
      document.removeEventListener("pointerdown", outside);
      document.removeEventListener("keydown", key);
      window.removeEventListener("scroll", close, true);
      window.removeEventListener("resize", close);
    };
  }, [open]);

  return (
    <div ref={root} className="row-menu">
      <button ref={trigger} type="button" className="row-menu__trigger" aria-label={label} aria-haspopup="menu" aria-expanded={open} onClick={toggle}>⋯</button>
      {pos && (
        // Close after the item's form has been submitted (next tick), not before.
        <div className="row-menu__panel" role="menu" style={pos}
          onClick={(e) => (e.target as HTMLElement).closest('[role="menuitem"]') && setTimeout(() => setPos(null), 0)}>
          {children}
        </div>
      )}
    </div>
  );
}
