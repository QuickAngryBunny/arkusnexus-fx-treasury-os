import type { Metadata } from "next";
import { Field, Notice, Segmented } from "@fxos/ui";
import { saveCompany } from "@/app/settings/actions";
import { ActionForm } from "@/components/ActionForm";
import { BankEditor } from "@/components/BankEditor";
import { bankLabels } from "@/components/onboarding/labels";
import { SettingsShell } from "@/components/settings/SettingsShell";
import { apiGet, type CompanyInfo } from "@/lib/api";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Company info · Settings" };
export const dynamic = "force-dynamic";

/** C-01 Company info: name, base and reporting currency, and the bank tags. Admins edit; others read. */
export default async function CompanyPage() {
  const { role, profile, t } = await getSession();
  const result = await apiGet<CompanyInfo>("/api/tenant/company");
  if (!result.ok) return <SettingsShell page="company"><Notice tone="danger" title={result.message} /></SettingsShell>;
  const c = result.data;
  const admin = role === "admin";
  const users = profile?.ok ? profile.data.counts.users : 0;

  if (!admin) {
    return (
      <SettingsShell page="company" eyebrow={t("company.eyebrow", { name: c.displayName, count: users })}>
        <section className="card stack-sm">
          <span className="kicker">{t("company.kicker")}</span>
          <div className="detail-row"><span><strong>{t("company.name")}</strong><span>{c.displayName}</span></span></div>
          <div className="detail-row"><span><strong>{t("company.base")}</strong><span>{c.baseCurrency}</span></span></div>
          <div className="detail-row"><span><strong>{t("company.reporting")}</strong><span>{c.reportingCurrency}</span></span></div>
          <div className="detail-row"><span><strong>{t("company.banks")}</strong>
            <span>{c.banks.map((b) => `${b.name} · ${b.currency}`).join(", ") || t("banks.empty")}</span></span></div>
          <p className="subtitle">{t("company.adminsEdit")}</p>
        </section>
      </SettingsShell>
    );
  }

  return (
    <SettingsShell page="company" eyebrow={t("company.eyebrow", { name: c.displayName, count: users })}>
      <ActionForm action={saveCompany} submit={t("settings.save")} pending={t("onb.saving")} saved={t("settings.saved")} className="stack"
        messages={{ last_bank: t("company.lastBank"), duplicate: t("company.duplicate") }}>
        <section className="card stack-sm">
          <span className="kicker">{t("company.kicker")}</span>
          <Field label={t("company.name")} name="displayName" required maxLength={200} defaultValue={c.displayName} />
          <div className="form-grid">
            <div className="field-block">
              <span className="field-label">{t("company.base")}</span>
              <Segmented label={t("company.base")} name="baseCurrency" defaultValue={c.baseCurrency} onWhite
                options={[{ value: "USD", label: "USD" }, { value: "MXN", label: "MXN" }]} />
              <span className="field-hint">{t("company.baseHint")}</span>
            </div>
            <div className="field-block">
              <span className="field-label">{t("company.reporting")}</span>
              <Segmented label={t("company.reporting")} name="reportingCurrency" defaultValue={c.reportingCurrency} onWhite
                options={[{ value: "MXN", label: "MXN" }, { value: "USD", label: "USD" }]} />
              <span className="field-hint">{t("company.reportingHint")}</span>
            </div>
          </div>
          {/* Fields set in onboarding, kept as they are. */}
          <input type="hidden" name="legalName" value={c.legalName ?? ""} />
          <input type="hidden" name="country" value={c.country} />
          <input type="hidden" name="timeZone" value={c.timeZone} />
          <input type="hidden" name="conversionDirection" value={c.conversionDirection} />
          <input type="hidden" name="locale" value={c.locale} />
        </section>
        <section className="card stack-sm">
          <span className="kicker">{t("company.banks")}</span>
          <p className="subtitle">{t("company.banksIntro")}</p>
          <BankEditor initial={c.banks} labels={bankLabels(t)} rename />
          <p className="subtitle">{t("company.banksNote")}</p>
        </section>
      </ActionForm>
    </SettingsShell>
  );
}
