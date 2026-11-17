import type { Metadata } from "next";
import Link from "next/link";
import { Badge, Notice, PageHeader, StatCard } from "@fxos/ui";
import { openCompany } from "@/app/admin/actions";
import { AdminShell } from "@/components/admin/AdminShell";
import { NewTenant } from "@/components/admin/NewTenant";
import { tenantState, newTenantLabels } from "@/components/admin/labels";
import { RefreshWhile } from "@/components/RefreshWhile";
import { apiGet, type AdminTenant } from "@/lib/api";
import { formatDate } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Tenants · Admin portal" };
export const dynamic = "force-dynamic";

/** A-01 Tenants (A-S1 when there are none). */
export default async function TenantsPage({ searchParams }: { searchParams: Promise<{ done?: string; error?: string }> }) {
  const { locale, t } = await getSession();
  const { done, error } = await searchParams;
  const result = await apiGet<AdminTenant[]>("/api/admin/tenants");
  const tenants = result.ok ? result.data : [];
  const labels = newTenantLabels(t);

  const real = tenants.filter((x) => !x.isDummy).length;
  const ready = tenants.filter((x) => x.status === "active").length;
  const busy = tenants.some((x) => x.setup && x.setup.status !== "done" && x.setup.status !== "failed");

  return (
    <AdminShell>
      <div className="stack">
        <RefreshWhile active={busy} />
        <PageHeader eyebrow={t("admin.eyebrow")} title={t("admin.tenants.title")}
          actions={tenants.length > 0 ? <NewTenant labels={labels} /> : undefined} />
        {done === "deleted" && <Notice tone="success" title={t("admin.done.deleted")} />}
        {error && <Notice tone="danger" title={error === "not_allowed" ? t("admin.error.notAllowed") : error} />}
        {!result.ok && <Notice tone="danger" title={t("apiDown")}>{result.message}</Notice>}

        {result.ok && tenants.length === 0 ? (
          <section className="card empty-state">
            <span className="empty-state__icon" aria-hidden="true">
              <svg width="34" height="34" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M4 7c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3zM4 7v10c0 1.7 3.6 3 8 3s8-1.3 8-3V7M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3" />
              </svg>
            </span>
            <h2>{t("admin.empty.title")}</h2>
            <p>{t("admin.empty.body")}</p>
            <NewTenant labels={{ ...labels, open: t("admin.empty.action") }} size="lg" />
          </section>
        ) : (
          <>
            <div className="grid">
              <StatCard label={t("admin.stats.tenants")} value={String(tenants.length)} note={t("admin.stats.real", { count: real })} />
              <StatCard label={t("admin.stats.ready")} value={t("admin.stats.readyValue", { ready, total: tenants.length })}
                note={ready === tenants.length ? t("admin.stats.allCurrent") : t("admin.stats.someBusy")} />
              <StatCard label={t("admin.stats.rates")} value={t("admin.stats.ratesValue")} note={t("admin.stats.ratesNote")} />
            </div>
            <section className="card stack-sm">
              <div className="section-row">
                <span className="kicker">{t("admin.tenants.all")}</span>
                <span className="subtitle">{t("admin.tenants.ownDb")}</span>
              </div>
              <div className="vi-table-wrap">
                <table className="vi-table">
                  <thead>
                    <tr>
                      <th>{t("admin.col.tenant")}</th>
                      <th>{t("admin.col.data")}</th>
                      <th>{t("admin.col.database")}</th>
                      <th>{t("admin.col.members")}</th>
                      <th>{t("admin.col.created")}</th>
                      <th><span className="sr-only">{t("admin.col.actions")}</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    {tenants.map((x) => {
                      const state = tenantState(t, x);
                      return (
                        <tr key={x.id}>
                          <td><Link href={`/admin/tenants/${x.id}`}>{x.displayName}</Link></td>
                          <td>
                            <Badge tone={x.isDummy ? "neutral" : "plum"} dot={false}>
                              {x.isDummy ? t("admin.data.dummy") : t("admin.data.real")}
                            </Badge>
                          </td>
                          <td><Badge tone={state.tone}>{state.label}</Badge></td>
                          <td>{x.members}</td>
                          <td>{formatDate(locale, x.createdAt.slice(0, 10))}</td>
                          <td className="vi-table__actions">
                            {x.status === "active" && x.isDummy && (
                              <form action={openCompany} className="inline-form">
                                <input type="hidden" name="id" value={x.id} />
                                <button type="submit">{t("admin.openInApp")}</button>
                              </form>
                            )}
                            {x.status === "active" && x.isDummy && " · "}
                            <Link href={`/admin/tenants/${x.id}`}>{t("admin.manage")}</Link>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </section>
          </>
        )}
        <p className="admin-footnote">{t("admin.footnote")}</p>
      </div>
    </AdminShell>
  );
}
