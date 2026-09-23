namespace Mh3gSim.Core;

/// <summary>
/// お守り入力の選択肢の範囲。系統 (charms.json の categories) を区別せず、
/// どれかの系統で付き得るスキルと、その中で一番大きい最大ポイントを使う。
/// </summary>
public sealed class CharmLimits
{
    /// <summary>第 1 スキルに付き得る系統 → 最大ポイント。</summary>
    public required IReadOnlyDictionary<string, int> FirstSkillMax { get; init; }
    /// <summary>第 2 スキルに付き得る系統 → 最大ポイント (第 2 スキルのある系統だけから集める)。</summary>
    public required IReadOnlyDictionary<string, int> SecondSkillMax { get; init; }
    /// <summary>第 2 スキルのマイナス側の下限。</summary>
    public required int SecondSkillMin { get; init; }
    public required int MaxSlots { get; init; }

    public static CharmLimits From(IEnumerable<CharmCategory> categories)
    {
        var list = categories.ToList();
        return new CharmLimits
        {
            FirstSkillMax = MergeMax(list),
            SecondSkillMax = MergeMax(list.Where(c => c.SecondSkill)),
            SecondSkillMin = list.Where(c => c.SecondSkill).Select(c => c.SecondSkillMin).DefaultIfEmpty(0).Min(),
            MaxSlots = list.Select(c => c.MaxSlots).DefaultIfEmpty(0).Max(),
        };
    }

    private static Dictionary<string, int> MergeMax(IEnumerable<CharmCategory> categories)
    {
        var merged = new Dictionary<string, int>();
        foreach (var (skill, maximum) in categories.SelectMany(c => c.Skills))
            merged[skill] = Math.Max(merged.GetValueOrDefault(skill), maximum);
        return merged;
    }
}
