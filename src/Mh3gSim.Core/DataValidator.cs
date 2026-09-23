namespace Mh3gSim.Core;

/// <summary>
/// データファイルを手で直した後の整合チェック (読み込めても意味がおかしい値を見つける)。
/// 問題が無ければ空のリストを返す。
/// </summary>
public static class DataValidator
{
    private const int MaxSlots = 3;
    private const int MaxRarity = 10;
    private const int MaxCharmPoints = 20;

    public static List<string> Validate(GameData data)
    {
        var problems = new List<string>();
        var systems = data.SkillSystems.Select(s => s.System).ToHashSet();
        ValidateSkills(data, problems);
        ValidateArmors(data, systems, problems);
        ValidateDecorations(data, systems, problems);
        ValidateCharms(data, systems, problems);
        return problems;
    }

    private static void ValidateSkills(GameData data, List<string> problems)
    {
        foreach (var group in data.SkillSystems.GroupBy(s => s.System).Where(g => g.Count() > 1))
            problems.Add($"{GameData.SkillFile}: 系統「{group.Key}」が重複");
        foreach (var system in data.SkillSystems)
        {
            if (system.Activations.Count == 0) problems.Add($"{GameData.SkillFile}: 「{system.System}」に発動スキルが無い");
            foreach (var activation in system.Activations.Where(a => a.Points == 0 || string.IsNullOrWhiteSpace(a.Name)))
                problems.Add($"{GameData.SkillFile}: 「{system.System}」の発動スキルに points 0 か名前なしがある");
        }
    }

    private static void ValidateArmors(GameData data, HashSet<string> systems, List<string> problems)
    {
        foreach (var group in data.Armors.GroupBy(a => a.Id).Where(g => g.Count() > 1))
            problems.Add($"{GameData.ArmorFile}: id {group.Key} が重複");
        foreach (var armor in data.Armors)
        {
            var label = $"{GameData.ArmorFile}: id {armor.Id} ({armor.Name})";
            if (string.IsNullOrWhiteSpace(armor.Name)) problems.Add($"{label}: 名前が空");
            if (armor.Slots is < 0 or > MaxSlots) problems.Add($"{label}: slots {armor.Slots} (0〜{MaxSlots})");
            if (armor.Rarity is < 1 or > MaxRarity) problems.Add($"{label}: rarity {armor.Rarity} (1〜{MaxRarity})");
            if (armor.Defense > armor.MaxDefense) problems.Add($"{label}: defense {armor.Defense} > maxDefense {armor.MaxDefense}");
            foreach (var skill in armor.Skills.Keys.Where(k => k != Armor.TorsoUpSkill && !systems.Contains(k)))
                problems.Add($"{label}: スキル「{skill}」が {GameData.SkillFile} に無い");
        }
        for (var part = 0; part < ArmorParts.Count; part++)
            if (data.Armors.All(a => a.Part != part)) problems.Add($"{GameData.ArmorFile}: 部位「{ArmorParts.Names[part]}」の防具が 1 つも無い");
    }

    private static void ValidateDecorations(GameData data, HashSet<string> systems, List<string> problems)
    {
        foreach (var group in data.Decorations.GroupBy(d => d.Name).Where(g => g.Count() > 1))
            problems.Add($"{GameData.DecorationFile}: 「{group.Key}」が重複");
        foreach (var decoration in data.Decorations)
        {
            var label = $"{GameData.DecorationFile}: {decoration.Name}";
            if (decoration.Slots is < 1 or > MaxSlots) problems.Add($"{label}: slots {decoration.Slots} (1〜{MaxSlots})");
            if (decoration.Skills.Count(kv => kv.Value > 0) != 1) problems.Add($"{label}: プラスのスキルがちょうど 1 つではない");
            foreach (var skill in decoration.Skills.Keys.Where(k => !systems.Contains(k)))
                problems.Add($"{label}: スキル「{skill}」が {GameData.SkillFile} に無い");
        }
    }

    private static void ValidateCharms(GameData data, HashSet<string> systems, List<string> problems)
    {
        foreach (var group in data.CharmCategories.SelectMany(c => c.Types).GroupBy(t => t).Where(g => g.Count() > 1))
            problems.Add($"{GameData.CharmFile}: 種類「{group.Key}」が複数の系統にある");
        foreach (var category in data.CharmCategories)
        {
            var label = $"{GameData.CharmFile}: {category.Name}";
            if (category.Types.Count == 0) problems.Add($"{label}: types が空");
            if (category.MaxSlots is < 0 or > MaxSlots) problems.Add($"{label}: maxSlots {category.MaxSlots} (0〜{MaxSlots})");
            if (category.SecondSkill ? category.SecondSkillMin >= 0 : category.SecondSkillMin != 0)
                problems.Add($"{label}: secondSkillMin {category.SecondSkillMin} (第 2 スキルありなら負、なしなら 0)");
            foreach (var (skill, maximum) in category.Skills)
            {
                if (!systems.Contains(skill)) problems.Add($"{label}: スキル「{skill}」が {GameData.SkillFile} に無い");
                if (maximum is < 1 or > MaxCharmPoints) problems.Add($"{label}: {skill} の最大ポイント {maximum} (1〜{MaxCharmPoints})");
            }
        }
    }
}
