using System.Text.Json.Serialization;

namespace Mh3gSim.Core;

public static class ArmorParts
{
    public const int Count = 5;
    public const int Body = 1;
    public static readonly string[] Names = ["頭", "胴", "腕", "腰", "脚"];
}

public sealed class Armor
{
    public const string TorsoUpSkill = "胴系統倍化";

    public int Id { get; init; }
    public int Part { get; init; }
    public bool Blade { get; init; }
    public bool Gunner { get; init; }
    public int Rarity { get; init; }
    public bool Male { get; init; }
    public bool Female { get; init; }
    public string Name { get; init; } = "";
    public int Defense { get; init; }
    public int MaxDefense { get; init; }
    public int[] Resist { get; init; } = new int[5];
    public int Slots { get; init; }
    public Dictionary<string, int> Skills { get; init; } = [];

    public bool IsTorsoUp => Skills.ContainsKey(TorsoUpSkill);

    public int PointsOf(string system) => Skills.GetValueOrDefault(system);
}

public sealed class Decoration
{
    public string Name { get; init; } = "";
    public int Slots { get; init; }
    public Dictionary<string, int> Skills { get; init; } = [];
}

public sealed class SkillActivation
{
    public int Points { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class SkillSystem
{
    public string System { get; init; } = "";
    public List<SkillActivation> Activations { get; init; } = [];
}

/// <summary>お守り。スキルは最大 2 系統。</summary>
public sealed class Charm
{
    public string Skill1 { get; set; } = "";
    public int Points1 { get; set; }
    public string Skill2 { get; set; } = "";
    public int Points2 { get; set; }
    public int Slots { get; set; }

    public static readonly Charm None = new();

    [JsonIgnore]
    public bool IsNone => string.IsNullOrEmpty(Skill1) && string.IsNullOrEmpty(Skill2) && Slots == 0;

    public IEnumerable<KeyValuePair<string, int>> Skills()
    {
        if (!string.IsNullOrEmpty(Skill1)) yield return new(Skill1, Points1);
        if (!string.IsNullOrEmpty(Skill2)) yield return new(Skill2, Points2);
    }

    public override string ToString()
    {
        if (IsNone) return "なし";
        var parts = Skills().Select(s => $"{s.Key}{s.Value:+0;-0}");
        return $"{string.Join(" ", parts)} [{new string('○', Slots)}{new string('－', 3 - Slots)}]";
    }

    /// <summary>スキル・ポイント・スロットが同じか。</summary>
    public bool SameContent(Charm other) =>
        Skill1 == other.Skill1 && Points1 == other.Points1 && Skill2 == other.Skill2
        && Points2 == other.Points2 && Slots == other.Slots;
}

/// <summary>
/// お守りの作られ方 (charms.json)。お守りの乱数は x ← Multiplier·x mod Modulus で進み、
/// 乱数の初期値がテーブルごとに決まっている。お守りの種類 (なぞ / 光る / 古びた / 風化した) ごとに
/// スキル表・第 2 スキルが付く判定値・スロットの判定表を持つ。
/// </summary>
public sealed class CharmModel
{
    public required int Multiplier { get; init; }
    public required int Modulus { get; init; }
    public required IReadOnlyList<CharmTableSeed> Tables { get; init; }
    public required IReadOnlyList<CharmKind> Kinds { get; init; }
}

/// <summary>テーブル番号と、その乱数の初期値。</summary>
public sealed record CharmTableSeed(int Table, int Seed);

public sealed class CharmKind
{
    /// <summary>拾った時のお守りの名前 (なぞのお守り 等)。</summary>
    public required string Name { get; init; }
    /// <summary>乱数 % 100 がこの値以上なら第 2 スキルが付く (100 なら付かない)。</summary>
    public required int SecondSkillThreshold { get; init; }
    /// <summary>護石の名前: 評価値 (スロット値 + 2 × スロット数) が MaxScore 以下の最初のもの。最後は MaxScore なし。</summary>
    public required IReadOnlyList<CharmNameBand> Names { get; init; }
    /// <summary>スロット値 1, 2, … ごとの [1 スロット, 2 スロット, 3 スロット] になる乱数 % 100 の下限。最後の行はそれより大きいスロット値にも使う。</summary>
    public required IReadOnlyList<int[]> SlotRows { get; init; }
    /// <summary>第 1 スキルの表。並び順 = 乱数で選ぶ番号。</summary>
    public required IReadOnlyList<CharmSkillRange> FirstSkills { get; init; }
    /// <summary>第 2 スキルの表。並び順 = 乱数で選ぶ番号。</summary>
    public required IReadOnlyList<CharmSkillRange> SecondSkills { get; init; }
}

public sealed record CharmNameBand(string Name, int? MaxScore);

/// <summary>スキル表の 1 行: スキルとポイントの範囲 (第 2 スキルは Min がマイナス)。</summary>
public sealed record CharmSkillRange(string Skill, int Min, int Max);

public sealed record SkillRequirement(string System, int Points, string ActivationName);

public sealed class SearchCondition
{
    public List<SkillRequirement> Requirements { get; init; } = [];
    public bool IsGunner { get; init; }
    public bool IsFemale { get; init; }
    public int MaxRarity { get; init; } = 10;
    public int WeaponSlots { get; init; }
    public List<Charm> Charms { get; init; } = [];
    public bool AvoidNegativeSkills { get; init; } = true;
    public HashSet<int> ExcludedArmorIds { get; init; } = [];
    public int MaxResults { get; init; } = 200;

    /// <summary>お守りの候補だけを差し替えた複製。</summary>
    public SearchCondition WithCharms(List<Charm> charms) => Copy(charms, MaxResults);

    /// <summary>結果の保持件数だけを差し替えた複製。</summary>
    public SearchCondition WithMaxResults(int maxResults) => Copy(Charms, maxResults);

    private SearchCondition Copy(List<Charm> charms, int maxResults) => new()
    {
        Requirements = Requirements,
        IsGunner = IsGunner,
        IsFemale = IsFemale,
        MaxRarity = MaxRarity,
        WeaponSlots = WeaponSlots,
        Charms = charms,
        AvoidNegativeSkills = AvoidNegativeSkills,
        ExcludedArmorIds = ExcludedArmorIds,
        MaxResults = maxResults,
    };
}

public sealed record PlacedDecoration(Decoration Decoration, string Location);

public sealed record ActiveSkill(string System, int Total, SkillActivation Activation)
{
    public bool IsNegative => Activation.Points < 0;
}

/// <param name="TruncatedSolves">装飾品探索を上限で打ち切った構成の数 (0 でなければ見落としの可能性がある)</param>
public sealed record SearchOutcome(List<SearchResult> Results, int TruncatedSolves);

public sealed class SearchResult
{
    public required Armor[] Armors { get; init; }
    /// <summary>部位ごとの同等品 (スキル・スロットが同じで防御力が低いもの)。</summary>
    public required IReadOnlyList<Armor>[] Equivalents { get; init; }
    public required Charm Charm { get; init; }
    public required List<PlacedDecoration> Decorations { get; init; }
    public required Dictionary<string, int> SkillTotals { get; init; }
    public required List<ActiveSkill> ActiveSkills { get; init; }
    public required List<string> FreeSlots { get; init; }
    public required int FreeSlotTotal { get; init; }

    public int Defense => Armors.Sum(a => a.Defense);
    public int MaxDefense => Armors.Sum(a => a.MaxDefense);
    public int[] Resist => Enumerable.Range(0, 5).Select(i => Armors.Sum(a => a.Resist[i])).ToArray();
}
