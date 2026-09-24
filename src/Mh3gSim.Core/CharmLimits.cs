namespace Mh3gSim.Core;

/// <summary>
/// お守り入力の選択肢の範囲。お守りの種類 (charms.json の kinds) を区別せず、
/// どれかの種類で付き得るスキルと、その中で一番大きい最大ポイントを使う。
/// </summary>
public sealed class CharmLimits
{
    /// <summary>第 1 スキルに付き得る系統 → 最大ポイント。</summary>
    public required IReadOnlyDictionary<string, int> FirstSkillMax { get; init; }
    /// <summary>第 2 スキルに付き得る系統 → 最大ポイント。</summary>
    public required IReadOnlyDictionary<string, int> SecondSkillMax { get; init; }
    /// <summary>第 2 スキルのマイナス側の下限。</summary>
    public required int SecondSkillMin { get; init; }
    public required int MaxSlots { get; init; }

    public static CharmLimits From(CharmModel model) => new()
    {
        FirstSkillMax = MergeMax(model.Kinds.SelectMany(k => k.FirstSkills)),
        SecondSkillMax = MergeMax(model.Kinds.SelectMany(k => k.SecondSkills)),
        SecondSkillMin = model.Kinds.SelectMany(k => k.SecondSkills).Select(s => s.Min).DefaultIfEmpty(0).Min(),
        MaxSlots = model.Kinds.Select(MaxSlotsOf).DefaultIfEmpty(0).Max(),
    };

    /// <summary>スロット判定表から、その種類で付き得る最大のスロット数 (判定値が 100 未満の列がある最大の数)。</summary>
    public static int MaxSlotsOf(CharmKind kind) =>
        kind.SlotRows.Select(row => row[2] < 100 ? 3 : row[1] < 100 ? 2 : row[0] < 100 ? 1 : 0).DefaultIfEmpty(0).Max();

    private static Dictionary<string, int> MergeMax(IEnumerable<CharmSkillRange> ranges)
    {
        var merged = new Dictionary<string, int>();
        foreach (var range in ranges)
            merged[range.Skill] = Math.Max(merged.GetValueOrDefault(range.Skill), range.Max);
        return merged;
    }
}
