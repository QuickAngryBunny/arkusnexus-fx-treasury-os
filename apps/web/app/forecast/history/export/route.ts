import { apiGet, type HistoryMonth, type HistoryYear } from "@/lib/api";
import { getSession } from "@/lib/session";

const BASE = "/api/tenant/rates/banxico_fix";

/** CSV of the Full history view being shown (a year by month, or a month by day). Stored precision, not display. */
export async function GET(request: Request): Promise<Response> {
  const { tenant } = await getSession();
  if (!tenant) return new Response("Sign in first.", { status: 401 });
  const url = new URL(request.url);
  const y = url.searchParams.get("y") ?? "";
  const m = url.searchParams.get("m") ?? "";

  let csv: string;
  let name: string;
  if (url.searchParams.get("view") === "year" && /^\d{4}$/.test(y)) {
    const data = await apiGet<HistoryYear>(`${BASE}/history/year/${y}`);
    if (!data.ok) return new Response(data.message, { status: data.status || 502 });
    csv = row(["month", "average_mxn_per_usd", "low", "high", "days", "gaps", "revised"])
      + [...data.data.months].reverse().map((r) => row([r.month, r.average, r.low, r.high, r.days, r.gaps, r.revised])).join("");
    name = `usdmxn-fix-${y}.csv`;
  } else if (/^\d{4}-\d{2}$/.test(m)) {
    const data = await apiGet<HistoryMonth>(`${BASE}/history/month/${m.slice(0, 4)}/${Number(m.slice(5, 7))}`);
    if (!data.ok) return new Response(data.message, { status: data.status || 502 });
    csv = row(["date", "fix_mxn_per_usd", "status", "revision", "source"])
      + [...data.data.days].reverse().map((r) => row([r.date, r.rate, r.status, r.revision || "", r.rate !== null ? "Banxico SIE SF43718" : ""])).join("");
    name = `usdmxn-fix-${m}.csv`;
  } else {
    return new Response("Choose a year (view=year&y=YYYY) or a month (m=YYYY-MM).", { status: 400 });
  }

  return new Response(csv, {
    headers: { "Content-Type": "text/csv; charset=utf-8", "Content-Disposition": `attachment; filename="${name}"`, "Cache-Control": "no-store" },
  });
}

function row(values: (string | number | null | undefined)[]): string {
  return values.map((v) => (v === null || v === undefined ? "" : /[",\n]/.test(String(v)) ? `"${String(v).replace(/"/g, '""')}"` : String(v))).join(",") + "\n";
}
