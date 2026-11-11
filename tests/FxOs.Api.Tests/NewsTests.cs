using System.Net.Http.Json;
using System.Text.Json;
using FxOs.Platform.Data;
using FxOs.Platform.Secrets;
using FxOs.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FxOs.Api.Tests;

/// <summary>Phase 2 exit: news for a past date loads from the tenant database (never GNews), tenant by tenant.</summary>
[Collection(PlatformCollection.Name)]
public sealed class NewsTests(PlatformFixture fx)
{
    [Fact]
    public async Task Moves_and_headlines_come_from_the_tenants_own_database()
    {
        var a = await fx.ProvisionAsync("news");
        var b = await fx.ProvisionAsync("news");
        await using (var conn = await OpenAsync(a))
        {
            await using var cmd = new NpgsqlCommand(
                """
                insert into rate_move (source_code, rate_date, rate, change_pct, sigma, vol30_pct)
                values ('banxico_fix', '2026-10-09', 18.4163, 1.6756, 2.51, 10.51);
                insert into news_window (window_key, kind, rate_date, window_from, window_to, raw_refs, article_count)
                values ('move:2026-10-09', 'move', '2026-10-09', '2026-10-09 00:00Z', '2026-10-10 18:00Z', '{gnews/x.json}', 2);
                insert into news_article (window_key, rank, lang, title, url, source_name, published_at) values
                    ('move:2026-10-09', 1, 'es', 'El peso se deprecia', 'https://a.mx/1', 'Diario A', '2026-10-09 15:00Z'),
                    ('move:2026-10-09', 2, 'en', 'Peso weakens', 'https://b.com/1', 'Wire B', '2026-10-09 16:00Z');
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var news = await GetAsync(a.Slug, "/api/tenant/news?from=2026-01-01&to=2026-12-31");
        var move = Assert.Single(news.GetProperty("moves").EnumerateArray());
        Assert.Equal("2026-10-09", move.GetProperty("date").GetString());
        Assert.Equal(2.51m, move.GetProperty("sigma").GetDecimal());
        var window = Assert.Single(news.GetProperty("windows").EnumerateArray());
        Assert.Equal(new[] { "El peso se deprecia", "Peso weakens" },
            window.GetProperty("articles").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        Assert.Equal("es", window.GetProperty("articles")[0].GetProperty("lang").GetString());

        var other = await GetAsync(b.Slug, "/api/tenant/news?from=2026-01-01&to=2026-12-31");
        Assert.Equal(0, other.GetProperty("moves").GetArrayLength());
        Assert.Equal(0, other.GetProperty("windows").GetArrayLength());
    }

    private async Task<JsonElement> GetAsync(string tenant, string url)
    {
        var response = await fx.Client().SendAsync(PlatformFixture.Request(HttpMethod.Get, url, tenant));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<NpgsqlConnection> OpenAsync(TenantRecord tenant)
    {
        var password = await fx.Services.GetRequiredService<ISecretStore>().GetAsync(tenant.SecretRef!);
        var conn = new NpgsqlConnection(fx.Services.GetRequiredService<TenantServerRegistry>()
            .TenantConnectionString(TenantServerRegistry.DefaultKey, tenant.DatabaseName!, tenant.LoginRole!, password!));
        await conn.OpenAsync();
        return conn;
    }
}
