"use client";

import { useEffect, useRef, useState } from "react";

export type FiltersDrawerProps = {
  buttonLabel: string;
  title: string;
  closeLabel: string;
  values: { period: string; currencies: string[]; customer: string | null; entity: string | null };
  options: { customers: string[]; entities: { id: string; name: string }[]; currencies: string[] };
  text: {
    dateRange: string;
    periods: { value: string; label: string }[];
    currency: string;
    customer: string;
    allCustomers: string;
    entity: string;
    allEntities: string;
    cancel: string;
    apply: string;
    clear: string;
  };
};

/** P8-02 filters drawer: a GET form, so the applied filters live in the URL (shareable, reload-safe). */
export function FiltersDrawer({ buttonLabel, title, closeLabel, values, options, text }: FiltersDrawerProps) {
  const [open, setOpen] = useState(false);
  const panel = useRef<HTMLElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    panel.current?.querySelector<HTMLElement>("input, select, button")?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setOpen(false);
        trigger.current?.focus();
      }
    };
    document.addEventListener("keydown", onKey);
    document.body.style.overflow = "hidden";
    return () => {
      document.removeEventListener("keydown", onKey);
      document.body.style.overflow = "";
    };
  }, [open]);

  return (
    <>
      <button ref={trigger} type="button" className="dash-filter-btn" aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(true)}>
        {buttonLabel}
      </button>
      {open && (
        <div className="drawer">
          <div className="drawer__scrim" onClick={() => setOpen(false)} />
          <aside ref={panel} role="dialog" aria-modal="true" aria-labelledby="filters-title" className="drawer__panel">
            <div className="drawer__head">
              <h2 id="filters-title">{title}</h2>
              <button type="button" className="drawer__close" aria-label={closeLabel} onClick={() => setOpen(false)}>✕</button>
            </div>
            <form method="get" action="/" className="drawer__form">
              <div className="drawer__body">
                <fieldset>
                  <legend>{text.dateRange}</legend>
                  <div className="drawer__pills">
                    {text.periods.map((p) => (
                      <label key={p.value} className="drawer__pill">
                        <input type="radio" name="period" value={p.value} defaultChecked={values.period === p.value} />
                        <span>{p.label}</span>
                      </label>
                    ))}
                  </div>
                </fieldset>
                <fieldset>
                  <legend>{text.currency}</legend>
                  <div className="drawer__pills">
                    {options.currencies.map((c) => (
                      <label key={c} className="drawer__check">
                        <input type="checkbox" name="cur" value={c} defaultChecked={values.currencies.includes(c)} />
                        {c}
                      </label>
                    ))}
                  </div>
                </fieldset>
                <label className="drawer__field">
                  {text.customer}
                  <select name="customer" defaultValue={values.customer ?? ""}>
                    <option value="">{text.allCustomers}</option>
                    {options.customers.map((c) => <option key={c} value={c}>{c}</option>)}
                  </select>
                </label>
                <label className="drawer__field">
                  {text.entity}
                  <select name="entity" defaultValue={values.entity ?? ""}>
                    <option value="">{text.allEntities}</option>
                    {options.entities.map((e) => <option key={e.id} value={e.id}>{e.name}</option>)}
                  </select>
                </label>
                <a href="/" className="drawer__clear">{text.clear}</a>
              </div>
              <div className="drawer__foot">
                <button type="button" className="drawer__cancel" onClick={() => setOpen(false)}>{text.cancel}</button>
                <button type="submit" className="drawer__apply">{text.apply}</button>
              </div>
            </form>
          </aside>
        </div>
      )}
    </>
  );
}
