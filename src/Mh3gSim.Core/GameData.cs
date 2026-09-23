using System.Reflection;
using System.Text.Json;

namespace Mh3gSim.Core;

/// <summary>データファイルの書き間違いなどで読み込めなかった時の例外。メッセージにファイル名と行を含める。</summary>
public sealed class DataLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// exe に埋め込んだ JSON (Data/*.json) を読み込む。
/// データファイルは手で直しやすいよう 1 件 = 1 行の書き方で、ここでシミュレーター内部の形に直す。
/// </summary>
public sealed class GameData
{
    public const string ArmorFile = "armors.json";
    public const string DecorationFile = "decorations.json";
    public const string SkillFile = "skills.json";
    public const string CharmFile = "charms.json";

    /// <summary>耐性の並び (Armor.Resist の添字順)。</summary>
    public static readonly string[] ResistNames = ["火", "水", "氷", "雷", "龍"];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public required IReadOnlyList<Armor> Armors { get; init; }
    public required IReadOnlyList<Decoration> Decorations { get; init; }
    public required IReadOnlyList<SkillSystem> SkillSystems { get; init; }
    public required IReadOnlyList<CharmCategory> CharmCategories { get; init; }

    public static GameData LoadEmbedded()
    {
        return new GameData
        {
            Armors = Load<List<ArmorRecord>>(ArmorFile).Select(r => r.ToArmor()).ToList(),
            Decorations = Load<List<Decoration>>(DecorationFile),
            SkillSystems = Load<List<SkillSystem>>(SkillFile),
            CharmCategories = Load<CharmTable>(CharmFile).ToCategories(),
        };
    }

    public SkillSystem? FindSystem(string name) => SkillSystems.FirstOrDefault(s => s.System == name);

    /// <summary>護石の種類名 (例: 天の護石) から系統を引く。見つからなければ null。</summary>
    public CharmCategory? FindCharmCategory(string type) =>
        CharmCategories.FirstOrDefault(c => c.Types.Contains(type));

    /// <summary>系統合計ポイントから発動スキルを求める (マイナススキル含む)。</summary>
    public List<ActiveSkill> ResolveActiveSkills(IReadOnlyDictionary<string, int> totals)
    {
        var active = new List<ActiveSkill>();
        foreach (var system in SkillSystems)
        {
            var total = totals.GetValueOrDefault(system.System);
            var activation = total >= 0
                ? system.Activations.Where(a => a.Points > 0 && total >= a.Points).MaxBy(a => a.Points)
                : system.Activations.Where(a => a.Points < 0 && total <= a.Points).MinBy(a => a.Points);
            if (activation != null) active.Add(new ActiveSkill(system.System, total, activation));
        }
        return active;
    }

    private static T Load<T>(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"Data.{fileName}")
            ?? throw new DataLoadException($"埋め込みデータ {fileName} が見つかりません");
        try
        {
            return JsonSerializer.Deserialize<T>(stream, JsonOptions)
                ?? throw new DataLoadException($"{fileName} が空です");
        }
        catch (JsonException exception)
        {
            var line = exception.LineNumber is { } number ? $" {number + 1} 行目付近" : "";
            throw new DataLoadException($"{fileName}{line}の書き方が正しくありません。({exception.Message})", exception);
        }
    }

    // ───────── armors.json の 1 行 ─────────

    private sealed class ArmorRecord
    {
        public int Id { get; init; }
        public string Part { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>両方 / 剣士 / ガンナー</summary>
        public string Class { get; init; } = "";
        /// <summary>両方 / 男 / 女</summary>
        public string Sex { get; init; } = "";
        public int Rarity { get; init; }
        public int Defense { get; init; }
        public int MaxDefense { get; init; }
        public int Slots { get; init; }
        public Dictionary<string, int> Skills { get; init; } = [];
        public Dictionary<string, int> Resist { get; init; } = [];

        public Armor ToArmor()
        {
            var part = Array.IndexOf(ArmorParts.Names, Part);
            if (part < 0) throw Invalid($"part は {string.Join(" / ", ArmorParts.Names)} のどれか (\"{Part}\")");
            if (Class is not ("両方" or "剣士" or "ガンナー")) throw Invalid($"class は 両方 / 剣士 / ガンナー のどれか (\"{Class}\")");
            if (Sex is not ("両方" or "男" or "女")) throw Invalid($"sex は 両方 / 男 / 女 のどれか (\"{Sex}\")");
            var unknownResist = Resist.Keys.Except(ResistNames).ToList();
            if (unknownResist.Count > 0) throw Invalid($"resist に使えるのは {string.Join(" ", ResistNames)} だけ ({string.Join(" ", unknownResist)})");

            return new Armor
            {
                Id = Id,
                Part = part,
                Blade = Class is "両方" or "剣士",
                Gunner = Class is "両方" or "ガンナー",
                Male = Sex is "両方" or "男",
                Female = Sex is "両方" or "女",
                Rarity = Rarity,
                Name = Name,
                Defense = Defense,
                MaxDefense = MaxDefense,
                Resist = ResistNames.Select(r => Resist.GetValueOrDefault(r)).ToArray(),
                Slots = Slots,
                Skills = Skills,
            };
        }

        private DataLoadException Invalid(string detail) => new($"{ArmorFile} の id {Id} ({Name}): {detail}");
    }

    // ───────── charms.json ─────────

    private sealed class CharmTable
    {
        public List<CharmCategory> Categories { get; init; } = [];
        /// <summary>1 行 = 1 スキル: {"skill": 系統名, "系統名": 最大ポイント, ...}。書いていない系統には付かない。</summary>
        public List<Dictionary<string, JsonElement>> MaxPoints { get; init; } = [];

        public List<CharmCategory> ToCategories()
        {
            var byName = Categories.ToDictionary(c => c.Name);
            foreach (var row in MaxPoints)
            {
                if (!row.TryGetValue("skill", out var skillElement) || skillElement.ValueKind != JsonValueKind.String)
                    throw new DataLoadException($"{CharmFile}: maxPoints の行に \"skill\" がありません");
                var skill = skillElement.GetString()!;
                foreach (var (column, value) in row.Where(kv => kv.Key != "skill"))
                {
                    if (!byName.TryGetValue(column, out var category))
                        throw new DataLoadException($"{CharmFile}: {skill} の行の \"{column}\" は categories にありません");
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var maximum))
                        throw new DataLoadException($"{CharmFile}: {skill} の行の \"{column}\" は整数で書いてください");
                    category.Skills[skill] = maximum;
                }
            }
            return Categories;
        }
    }
}
