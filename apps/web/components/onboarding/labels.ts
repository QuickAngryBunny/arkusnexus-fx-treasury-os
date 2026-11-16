import type { Translate } from "@/lib/i18n";
import type { BankEditorLabels } from "@/components/BankEditor";
import type { AcceptLabels } from "@/components/onboarding/AcceptForm";

export function acceptLabels(t: Translate, submit: string): AcceptLabels {
  return {
    name: t("onb.name"),
    email: t("auth.email"),
    emailHint: t("onb.emailLocked"),
    newPassword: t("auth.newPassword"),
    confirm: t("auth.confirmPassword"),
    show: t("auth.show"),
    hide: t("auth.hide"),
    rulesLabel: t("auth.rules.label"),
    rules: {
      min_length: t("auth.rules.min_length"),
      number: t("auth.rules.number"),
      symbol: t("auth.rules.symbol"),
      personal_info: t("auth.rules.personal_info"),
      max_length: t("auth.rules.max_length"),
    },
    matchEmpty: t("auth.match.empty"),
    matchYes: t("auth.match.yes"),
    matchNo: t("auth.match.no"),
    submit,
    pending: t("onb.saving"),
    weak: t("auth.reset.weak"),
    unavailable: t("auth.error.unavailable"),
    tenantUnavailable: t("onb.tenantUnavailable"),
  };
}

export function bankLabels(t: Translate): BankEditorLabels {
  return {
    mxnTitle: t("banks.mxnTitle"),
    usdTitle: t("banks.usdTitle"),
    mxnPlaceholder: t("banks.mxnPlaceholder"),
    usdPlaceholder: t("banks.usdPlaceholder"),
    add: t("banks.add"),
    addLabel: t("banks.addLabel"),
    suggestions: t("banks.suggestions"),
    remove: t("banks.remove"),
    rename: t("banks.rename"),
    empty: t("banks.empty"),
    missingOne: t("banks.missingOne"),
    save: t("admin.save"),
    cancel: t("admin.cancel"),
    missingTitle: { both: t("banks.missing.both"), mxn: t("banks.missing.mxn"), usd: t("banks.missing.usd") },
    missingBody: t("banks.missing.body"),
  };
}
