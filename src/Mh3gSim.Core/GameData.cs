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
    public required CharmModel Charms { get; init; }

    public static GameData LoadEmbedded()
    {
        return new GameData
        {
            Armors = Load<List<ArmorRecord>>(ArmorFile).Select(r => r.ToArmor()).ToList(),
            Decorations = Load<List<Decoration>>(DecorationFile),
            SkillSystems = Load<List<SkillSystem>>(SkillFile),
            Charms = Load<CharmRecord>(CharmFile).ToModel(),
        };
    }

    public SkillSystem? FindSystem(string name) => SkillSystems.FirstOrDefault(s => s.System == name);

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

    private const string FirstSkillList = "skill1";
    private const string SecondSkillList = "skill2";

    private sealed class CharmRecord
    {
        public RngRecord Rng { get; init; } = new();
        public List<CharmTableSeed> Tables { get; init; } = [];
        public List<KindRecord> Kinds { get; init; } = [];
        /// <summary>1 行 = スキル表の 1 行: {"kind", "list": skill1 / skill2, "index": 表の中の番号 (0 から), "skill", "min", "max"}。</summary>
        public List<SkillRecord> Skills { get; init; } = [];

        public CharmModel ToModel()
        {
            var unknownKinds = Skills.Select(s => s.Kind).Distinct().Except(Kinds.Select(k => k.Kind)).ToList();
            if (unknownKinds.Count > 0) throw new DataLoadException($"{CharmFile}: skills の kind が kinds にありません ({string.Join(" ", unknownKinds)})");
            var badLists = Skills.Where(s => s.List is not (FirstSkillList or SecondSkillList)).Select(s => $"{s.Kind} {s.Skill}").ToList();
            if (badLists.Count > 0) throw new DataLoadException($"{CharmFile}: skills の list は skill1 か skill2 ({string.Join(", ", badLists)})");

            return new CharmModel
            {
                Multiplier = Rng.Multiplier,
                Modulus = Rng.Modulus,
                Tables = Tables,
                Kinds = Kinds.Select(k => new CharmKind
                {
                    Name = k.Kind,
                    SecondSkillThreshold = k.SecondSkillThreshold,
                    Names = k.Names.Select(n => new CharmNameBand(n.Name, n.MaxScore)).ToList(),
                    SlotRows = k.SlotRows,
                    FirstSkills = OrderedList(k.Kind, FirstSkillList),
                    SecondSkills = OrderedList(k.Kind, SecondSkillList),
                }).ToList(),
            };
        }

        /// <summary>1 つのスキル表を index 順に並べる (乱数で選ぶ番号なので 0 から抜けなく続いていること)。</summary>
        private List<CharmSkillRange> OrderedList(string kind, string list)
        {
            var rows = Skills.Where(s => s.Kind == kind && s.List == list).OrderBy(s => s.Index).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Index != i)
                    throw new DataLoadException($"{CharmFile}: {kind} の {list} の index が 0 から連番になっていません ({i} の所が {rows[i].Index} / {rows[i].Skill})");
            }
            return rows.Select(s => new CharmSkillRange(s.Skill, s.Min, s.Max)).ToList();
        }
    }

    private sealed class RngRecord
    {
        public int Multiplier { get; init; }
        public int Modulus { get; init; }
    }

    private sealed class KindRecord
    {
        public string Kind { get; init; } = "";
        public int SecondSkillThreshold { get; init; }
        public List<NameRecord> Names { get; init; } = [];
        public List<int[]> SlotRows { get; init; } = [];
    }

    private sealed class NameRecord
    {
        public string Name { get; init; } = "";
        public int? MaxScore { get; init; }
    }

    private sealed class SkillRecord
    {
        public string Kind { get; init; } = "";
        public string List { get; init; } = "";
        public int Index { get; init; }
        public string Skill { get; init; } = "";
        public int Min { get; init; }
        public int Max { get; init; }
    }
}
