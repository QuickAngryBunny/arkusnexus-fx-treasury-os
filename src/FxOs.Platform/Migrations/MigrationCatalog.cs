using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FxOs.Platform.Migrations;

public sealed record Migration(int Version, string Name, string Sql, string Checksum);

/// <summary>
/// Loads the SQL migrations embedded from <c>/db/control</c> and <c>/db/tenant</c>.
/// Files are named <c>V0001__snake_case_name.sql</c> and must be numbered without gaps.
/// </summary>
public static partial class MigrationCatalog
{
    public const string ControlPlane = "control";
    public const string Tenant = "tenant";

    [GeneratedRegex(@"^fxos/migrations/(?<set>[a-z]+)/V(?<version>\d{4})__(?<name>[A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ResourceName();

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<Migration>>> All = new(LoadAll);

    public static IReadOnlyList<Migration> Load(string set) =>
        All.Value.TryGetValue(set, out var list)
            ? list
            : throw new InvalidOperationException($"No migrations embedded for set '{set}'.");

    public static int LatestVersion(string set) => Load(set)[^1].Version;

    private static IReadOnlyDictionary<string, IReadOnlyList<Migration>> LoadAll()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        var bySet = new Dictionary<string, List<Migration>>(StringComparer.Ordinal);

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourceName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Cannot read embedded migration {resource}.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // Checksums are taken on LF text so a Windows checkout cannot fake a change.
            var sql = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

            var set = match.Groups["set"].Value;
            if (!bySet.TryGetValue(set, out var list))
            {
                bySet[set] = list = [];
            }

            list.Add(new Migration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                sql,
                checksum));
        }

        var result = new Dictionary<string, IReadOnlyList<Migration>>(StringComparer.Ordinal);
        foreach (var (set, list) in bySet)
        {
            list.Sort((a, b) => a.Version.CompareTo(b.Version));
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Version != i + 1)
                {
                    throw new InvalidOperationException(
                        $"Migrations in '{set}' must run V0001, V0002, … without gaps; found V{list[i].Version:D4} at position {i + 1}.");
                }
            }

            result[set] = list;
        }

        return result;
    }
}
