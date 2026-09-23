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
/// お守りの系統 (鑑定前の名前) ごとの出現ルール。鑑定後の護石の種類はこのどれかに属する。
/// Skills は「付き得るスキル → 最大ポイント」。
/// </summary>
public sealed class CharmCategory
{
    public string Name { get; init; } = "";
    public List<string> Types { get; init; } = [];
    public int MaxSlots { get; init; }
    public bool SecondSkill { get; init; }
    /// <summary>第 2 スキルのマイナス側の下限 (第 2 スキルが無い系統は 0)。</summary>
    public int SecondSkillMin { get; init; }
    public Dictionary<string, int> Skills { get; init; } = [];
}

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
