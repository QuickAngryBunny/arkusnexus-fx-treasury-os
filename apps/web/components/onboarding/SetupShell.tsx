import type { ReactNode } from "react";
import { AuthShell } from "@fxos/ui";
import type { Translate } from "@/lib/i18n";

export function setupPills(t: Translate, current: 0 | 1 | 2) {
  return [t("onb.step.account"), t("onb.step.org"), t("onb.step.banks")].map((label, i) => ({
    label, state: (i < current ? "done" : i === current ? "current" : "todo") as "done" | "current" | "todo",
  }));
}

export function SetupShell({ t, company, body, children }: { t: Translate; company: string; body?: string; children: ReactNode }) {
  return (
    <AuthShell eyebrow={t("onb.eyebrow", { company })} title={t("onb.panelTitle")} body={body ?? t("onb.panelBody", { company })}
      footer={t("auth.panelFooter")} productName={t("product")} productTagline={t("productSubtitle")}>
      {children}
    </AuthShell>
  );
}

