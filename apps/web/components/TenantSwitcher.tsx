"use client";

import { useRef } from "react";
import { SelectField } from "@fxos/ui";
import { switchTenant } from "@/app/actions";
import type { DevTenant } from "@/lib/api";

type Props = { tenants: DevTenant[]; current?: string; label: string; placeholder: string };

/** Developer-only stand-in for sign-in. Removed in Phase 3. */
export function TenantSwitcher({ tenants, current, label, placeholder }: Props) {
  const form = useRef<HTMLFormElement>(null);

  return (
    // key: remount after a switch so the uncontrolled select shows the tenant the server now has.
    <form key={current ?? ""} ref={form} action={switchTenant} className="tenant-switcher">
      <SelectField
        label={label}
        name="tenant"
        defaultValue={current ?? ""}
        onChange={() => form.current?.requestSubmit()}
      >
        <option value="">{placeholder}</option>
        {tenants
          .filter((t) => t.status === "active")
          .map((t) => (
            <option key={t.slug} value={t.slug} title={t.slug}>
              {t.displayName}
            </option>
          ))}
      </SelectField>
    </form>
  );
}
