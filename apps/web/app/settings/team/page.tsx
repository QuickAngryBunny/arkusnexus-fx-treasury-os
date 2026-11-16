import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { Badge, Notice, StatCard, initials, type BadgeTone } from "@fxos/ui";
import {
  cancelInvitation, changeRole, deactivateMember, reactivateMember, resendInvitation, sendReset,
} from "@/app/settings/actions";
import { SettingsShell } from "@/components/settings/SettingsShell";
import { InviteTeammate, RowMenu } from "@/components/settings/TeamWidgets";
import { apiGet, type Team } from "@/lib/api";
import type { MessageKey, Translate } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Team · Settings" };
export const dynamic = "force-dynamic";

type Filter = "all" | "active" | "invited" | "deactivated";
type Row = {
  key: string; id: string; kind: "member" | "invitation"; name: string; email: string; role: string;
  status: "active" | "invited" | "expired" | "deactivated"; last: string | null;
};

const statusTone: Record<Row["status"], BadgeTone> = { active: "green", invited: "violet", expired: "orange", deactivated: "neutral" };

function menuItem(action: (form: FormData) => Promise<void>, id: string, label: string, extra?: Record<string, string>) {
  return (
    <form action={action}>
      <input type="hidden" name="id" value={id} />
      {extra && Object.entries(extra).map(([k, v]) => <input key={k} type="hidden" name={k} value={v} />)}
      <button type="submit" role="menuitem" className="vi-menu__item">{label}</button>
    </form>
  );
}

function rowActions(t: Translate, r: Row, me: string) {
  if (r.kind === "invitation") {
    return (
      <RowMenu label={t("team.menu", { name: r.name })}>
        {menuItem(resendInvitation, r.id, t("team.resend"))}
        {menuItem(cancelInvitation, r.id, t("team.cancelInvite"))}
      </RowMenu>
    );
  }
  if (r.status === "deactivated") {
    return <RowMenu label={t("team.menu", { name: r.name })}>{menuItem(reactivateMember, r.id, t("team.reactivate"))}</RowMenu>;
  }
  return (
    <RowMenu label={t("team.menu", { name: r.name })}>
      {(["standard", "approver", "admin"] as const).filter((role) => role !== r.role).map((role) => (
        <div key={role}>{menuItem(changeRole, r.id, t("team.makeRole", { role: t(`role.${role}`) }), { role })}</div>
      ))}
      {menuItem(sendReset, r.id, t("team.sendReset"))}
      {r.id !== me && menuItem(deactivateMember, r.id, t("team.deactivate"))}
    </RowMenu>
  );
}

/** T-01 Team: people and invitations with filters and the row menu; T-02 invite drawer; T-03 / T-S3 notices. Admins only. */
export default async function TeamPage({ searchParams }: { searchParams: Promise<{ filter?: string; done?: string; error?: string }> }) {
  const { session, role, locale, t } = await getSession();
  if (role !== "admin") notFound();
  const { filter: raw = "all", done, error } = await searchParams;
  const filter: Filter = (["all", "active", "invited", "deactivated"] as const).includes(raw as Filter) ? (raw as Filter) : "all";
  const result = await apiGet<Team>("/api/tenant/team");
  const team = result.ok ? result.data : { members: [], invitations: [] };
  const date = (iso: string | null) => (iso ? new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(iso)) : "—");

  const rows: Row[] = [
    ...team.members.map((m) => ({
      key: `m-${m.userId}`, id: m.userId, kind: "member" as const, name: m.displayName, email: m.email, role: m.role,
      status: m.status, last: m.lastSignInAt,
    })),
    ...team.invitations.map((i) => ({
      key: `i-${i.id}`, id: i.id, kind: "invitation" as const, name: i.displayName, email: i.email, role: i.role,
      status: i.state === "expired" ? ("expired" as const) : ("invited" as const), last: null,
    })),
  ];
  const shown = rows.filter((r) => filter === "all" || (filter === "invited" ? r.kind === "invitation" : r.status === filter));
  const active = team.members.filter((m) => m.status === "active");

  return (
    <SettingsShell page="team" actions={
      <InviteTeammate labels={{
        open: t("team.invite"), kicker: session?.tenant?.displayName ?? "", title: t("team.inviteTitle"), close: t("admin.close"),
        name: t("onb.name"), email: t("auth.email"), role: t("team.role"),
        standard: t("role.standard"), standardNote: t("team.standardNote"), approver: t("role.approver"), approverNote: t("team.approverNote"),
        note: t("team.note"), noteHint: t("team.noteHint"), footer: t("team.inviteFooter"), cancel: t("admin.cancel"),
        submit: t("team.send"), pending: t("onb.saving"), sent: t("team.sent"), sentBody: t("team.sentBody"),
        already: t("team.already"), alreadyBody: t("team.alreadyBody"),
      }} />
    }>
      <div className="stack-sm">
        {done && <Notice tone="success" title={t(`team.done.${done}` as MessageKey)} />}
        {error && <Notice tone="danger" title={error === "last_admin" ? t("team.lastAdmin") : error} />}
        <div className="grid team-stats">
          <StatCard label={t("team.stats.people")} value={String(team.members.length)} note={t("team.stats.active", { count: active.length })} />
          <StatCard label={t("team.stats.approvers")} value={String(active.filter((m) => m.role === "approver").length)} note={t("team.stats.approversNote")} />
          <StatCard label={t("team.stats.pending")} value={String(team.invitations.length)} note={t("team.stats.pendingNote")} />
        </div>
        <section className="card stack-sm">
          <div className="section-row">
            <span className="kicker">{t("team.people", { company: session?.tenant?.displayName ?? "" })}</span>
            <nav className="filter-pills" aria-label={t("team.filter")}>
              {(["all", "active", "invited", "deactivated"] as const).map((f) => (
                <Link key={f} href={f === "all" ? "/settings/team" : `/settings/team?filter=${f}`} aria-current={f === filter ? "page" : undefined}>
                  {t(`team.filter.${f}`)}
                </Link>
              ))}
            </nav>
          </div>
          <div className="vi-table-wrap">
            <table className="vi-table">
              <thead>
                <tr><th>{t("team.col.name")}</th><th>{t("team.role")}</th><th>{t("team.col.status")}</th><th>{t("team.col.last")}</th>
                  <th><span className="sr-only">{t("admin.col.actions")}</span></th></tr>
              </thead>
              <tbody>
                {shown.map((r) => (
                  <tr key={r.key}>
                    <td>
                      <span className="person-cell">
                        <span className="person-cell__avatar" aria-hidden="true">{initials(r.name)}</span>
                        <span><strong>{r.name}</strong><span>{r.email}</span></span>
                      </span>
                    </td>
                    <td>{t(`role.${r.role}` as MessageKey)}</td>
                    <td><Badge tone={statusTone[r.status]}>{t(`team.status.${r.status}` as MessageKey)}</Badge></td>
                    <td>{r.kind === "member" ? date(r.last) : t("team.expires", { date: date(team.invitations.find((i) => i.id === r.id)?.expiresAt ?? null) })}</td>
                    <td className="vi-table__actions">{rowActions(t, r, session?.user.id ?? "")}</td>
                  </tr>
                ))}
                {shown.length === 0 && <tr><td colSpan={5}>{t("team.none")}</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </SettingsShell>
  );
}
