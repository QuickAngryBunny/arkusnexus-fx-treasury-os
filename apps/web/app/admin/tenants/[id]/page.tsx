import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { Badge, Breadcrumb, Button, Field, Notice, PageHeader, StepList, type Step } from "@fxos/ui";
import {
  archiveTenant, openCompany, renameTenant, requestDummyData, restoreTenant, retrySetup,
} from "@/app/admin/actions";
import { AdminShell } from "@/components/admin/AdminShell";
import { DeleteTenant } from "@/components/admin/DeleteTenant";
import { tenantState } from "@/components/admin/labels";
import { RefreshWhile } from "@/components/RefreshWhile";
import { apiGet, type AdminTenant, type TenantJob } from "@/lib/api";
import { formatDate, type Translate } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Tenant · Admin portal" };
export const dynamic = "force-dynamic";

const ORDER = ["database", "rate_history", "invitation"] as const;

/** A-03 / A-S2 setup steps from the setup job. */
function setupSteps(t: Translate, job: TenantJob, time: (iso: string) => string): Step[] {
  const at = Math.max(0, ORDER.indexOf(job.step as (typeof ORDER)[number]));
  const doneAll = job.step === "done";
  const event = (step: string) => job.events.find((e) => e.step === step);
  return ORDER.map((step, i) => {
    const state: Step["state"] = doneAll || i < at ? "done" : i > at ? "todo" : job.status === "failed" ? "failed" : "running";
    const e = event(step);
    const note =
      state === "done" && e ? `${t(`admin.step.${step}.done`)} · ${time(e.at)}`
      : state === "failed" ? `${job.error ?? ""}`
      : state === "running" ? t(`admin.step.${step}.running`)
      : t(`admin.step.${step}.todo`);
    return { id: step, state, title: t(`admin.step.${step}.title`), note, progress: step === "rate_history" ? job.progress : undefined };
  });
}

export default async function TenantPage({ params, searchParams }: {
  params: Promise<{ id: string }>; searchParams: Promise<{ done?: string; error?: string }>;
}) {
  const { locale, t } = await getSession();
  const { id } = await params;
  const { done, error } = await searchParams;
  const result = await apiGet<AdminTenant>(`/api/admin/tenants/${encodeURIComponent(id)}`);
  if (!result.ok) {
    if (result.status === 404) notFound();
    return <AdminShell><Notice tone="danger" title={t("apiDown")}>{result.message}</Notice></AdminShell>;
  }

  const x = result.data;
  const state = tenantState(t, x);
  const time = (iso: string) => new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" }).format(new Date(iso));
  const setup = x.setup;
  const settingUp = setup !== null && setup.step !== "done";
  const working = (setup && settingUp && setup.status !== "failed")
    || (x.dummyJob !== null && (x.dummyJob.status === "pending" || x.dummyJob.status === "running"));
  const hidden = (name: string, value: string) => <input type="hidden" name={name} value={value} />;

  return (
    <AdminShell>
      <div className="stack">
        <RefreshWhile active={Boolean(working)} everyMs={2500} />
        <PageHeader
          breadcrumb={<Breadcrumb label={t("breadcrumb.label")} linkAs={Link}
            items={[{ label: t("admin.tenants.title"), href: "/admin" }, { label: x.displayName }]} />}
          eyebrow={t("admin.eyebrow")}
          title={<>{x.displayName} <Badge tone={state.tone}>{state.label}</Badge></>}
          actions={x.status === "active" && x.isDummy ? (
            <form action={openCompany}>{hidden("id", x.id)}<Button type="submit">{t("admin.openInApp")} ↗</Button></form>
          ) : undefined}
        />
        {done && <Notice tone="success" title={t(`admin.done.${done}` as Parameters<Translate>[0])} />}
        {error && <Notice tone="danger" title={error} />}

        {setup && settingUp && (
          <section className="card stack-sm">
            <span className="kicker">{setup.status === "failed" ? t("admin.setup.failedKicker") : t("admin.setup.kicker")}</span>
            {setup.status === "failed" && (
              <Notice tone="danger" title={t("admin.setup.failedTitle")}>{t("admin.setup.failedBody")}</Notice>
            )}
            <StepList label={t("admin.setup.kicker")} steps={[
              ...setupSteps(t, setup, time),
              { id: "ready", state: "todo", title: t("admin.step.ready.title"), note: t("admin.step.ready.todo") },
            ]} />
            {setup.status === "failed" ? (
              <div className="row-actions">
                <form action={retrySetup}>{hidden("id", x.id)}<Button type="submit">{t("admin.setup.retry")}</Button></form>
                <span className="subtitle">{t("admin.setup.retryNote")}</span>
              </div>
            ) : (
              <p className="subtitle">{t("admin.setup.leave")}</p>
            )}
          </section>
        )}

        {!settingUp && (
          <div className="admin-grid">
            <section className="card stack-sm">
              <span className="kicker">{t("admin.tenant.kicker")}</span>
              <form action={renameTenant} className="rename-row">
                {hidden("id", x.id)}
                <Field label={t("admin.tenant.name")} name="name" defaultValue={x.displayName} required maxLength={200}
                  hint={t("admin.tenant.nameHint")} />
                <Button type="submit" variant="outline">{t("admin.save")}</Button>
              </form>
              <div className="detail-row">
                <span><strong>{t("admin.tenant.created")}</strong><span>{formatDate(locale, x.createdAt.slice(0, 10))}
                  {setup?.firstAdminEmail ? ` · ${t("admin.tenant.firstAdmin", { email: setup.firstAdminEmail })}` : ""}</span></span>
              </div>
              <div className="detail-row">
                <span><strong>{t("admin.tenant.members")}</strong><span>{t("admin.tenant.membersValue", { count: x.members })}</span></span>
              </div>
              {x.isDummy && x.status === "active" && (
                <div className="detail-row">
                  <span>
                    <strong>{t("admin.tenant.dummy")}</strong>
                    <span>
                      {x.dummyJob?.status === "failed" ? `${t("admin.dummy.failed")}: ${x.dummyJob.error}`
                        : x.dummyJob && x.dummyJob.status !== "done" ? t("admin.dummy.working")
                        : x.dummyJob?.kind === "load_dummy" ? t("admin.dummy.loaded")
                        : t("admin.dummy.none")}
                    </span>
                  </span>
                  <span className="row-actions">
                    {x.dummyJob?.status === "failed" ? (
                      <form action={retrySetup}>{hidden("id", x.id)}<Button type="submit" variant="outline" size="sm">{t("admin.setup.retry")}</Button></form>
                    ) : (
                      <>
                        <form action={requestDummyData}>{hidden("id", x.id)}{hidden("load", "true")}
                          <Button type="submit" variant="outline" size="sm" disabled={Boolean(working)}>{t("admin.dummy.load")}</Button></form>
                        <form action={requestDummyData}>{hidden("id", x.id)}{hidden("load", "false")}
                          <Button type="submit" variant="ghost" size="sm" disabled={Boolean(working)}>{t("admin.dummy.clear")}</Button></form>
                      </>
                    )}
                  </span>
                </div>
              )}
            </section>

            <section className="card stack-sm">
              <span className="kicker">{t("admin.db.kicker")}</span>
              <div className="detail-row">
                <span><strong>{t("admin.db.status")}</strong>
                  <span>{x.schemaVersion ? t("admin.db.schema", { version: x.schemaVersion }) : t("admin.db.none")}</span></span>
                <Badge tone={state.tone}>{state.label}</Badge>
              </div>
              <div className="detail-row">
                <span><strong>{t("admin.db.rates")}</strong><span>{t("admin.db.ratesValue")}</span></span>
              </div>
              <div className="detail-row">
                <span><strong>{t("admin.db.isolation")}</strong><span>{t("admin.db.isolationValue")}</span></span>
              </div>
              <div className="detail-row">
                <span><strong>{t("admin.db.data")}</strong>
                  <span>{x.isDummy ? t("admin.data.dummy") : t("admin.data.real")}</span></span>
              </div>
            </section>
          </div>
        )}

        <section className="card stack-sm">
          <span className="kicker kicker--danger">{t("admin.danger.kicker")}</span>
          {!settingUp && (
            <div className="detail-row">
              {x.status === "archived" ? (
                <>
                  <span><strong>{t("admin.danger.restore")}</strong><span>{t("admin.danger.restoreBody")}</span></span>
                  <form action={restoreTenant}>{hidden("id", x.id)}<Button type="submit" variant="outline" size="sm">{t("admin.danger.restore")}</Button></form>
                </>
              ) : (
                <>
                  <span><strong>{t("admin.danger.archive")}</strong><span>{t("admin.danger.archiveBody")}</span></span>
                  <form action={archiveTenant}>{hidden("id", x.id)}<Button type="submit" variant="outline" size="sm">{t("admin.danger.archive")}</Button></form>
                </>
              )}
            </div>
          )}
          <div className="detail-row detail-row--stack">
            <span><strong>{t("admin.danger.delete")}</strong>
              <span>{x.isDummy ? t("admin.danger.deleteBody") : t("admin.danger.realOnlyArchive")}</span></span>
            {x.isDummy && (
              <DeleteTenant id={x.id} name={x.displayName} labels={{
                open: t("admin.danger.delete"),
                title: t("admin.danger.delete"),
                body: t("admin.danger.confirmBody"),
                typeName: t("admin.danger.typeName"),
                cancel: t("admin.cancel"),
                submit: t("admin.danger.deleteForever"),
                pending: t("admin.danger.deleting"),
              }} />
            )}
          </div>
        </section>
      </div>
    </AdminShell>
  );
}
