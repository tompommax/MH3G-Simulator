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
    /// <summary>テーブル数の上限 (出るテーブルを int のビットで持つため)。</summary>
    private const int MaxCharmTables = 31;

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
        var model = data.Charms;
        var file = GameData.CharmFile;
        // 乗数と法が互いに素でないと、乱数が初期値に戻らずテーブルが輪にならない
        if (model.Multiplier < 2 || model.Modulus < 2 || Gcd(model.Multiplier, model.Modulus) != 1)
            problems.Add($"{file}: rng の multiplier {model.Multiplier} と modulus {model.Modulus} は互いに素な 2 以上の整数");

        var numbers = model.Tables.Select(t => t.Table).Order().ToList();
        if (!numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)))
            problems.Add($"{file}: tables の table は 1 から抜けなく重複なく ({string.Join(" ", numbers)})");
        if (numbers.Count is 0 or > MaxCharmTables) problems.Add($"{file}: tables は 1〜{MaxCharmTables} 個");
        foreach (var table in model.Tables.Where(t => t.Seed < 1 || t.Seed >= model.Modulus))
            problems.Add($"{file}: テーブル {table.Table} の seed {table.Seed} (1〜{model.Modulus - 1})");
        foreach (var group in model.Tables.GroupBy(t => t.Seed).Where(g => g.Count() > 1))
            problems.Add($"{file}: seed {group.Key} が複数のテーブルにある");

        foreach (var group in model.Kinds.GroupBy(k => k.Name).Where(g => g.Count() > 1))
            problems.Add($"{file}: kinds の「{group.Key}」が重複");
        foreach (var kind in model.Kinds) ValidateCharmKind(kind, systems, problems);
    }

    private static void ValidateCharmKind(CharmKind kind, HashSet<string> systems, List<string> problems)
    {
        var label = $"{GameData.CharmFile}: {kind.Name}";
        if (kind.SecondSkillThreshold is < 0 or > 100) problems.Add($"{label}: secondSkillThreshold {kind.SecondSkillThreshold} (0〜100)");
        if (kind.FirstSkills.Count == 0) problems.Add($"{label}: skill1 の表が空");
        if (kind.SecondSkillThreshold < 100 && kind.SecondSkills.Count == 0) problems.Add($"{label}: 第 2 スキルが付く判定値なのに skill2 の表が空");

        if (kind.Names.Count == 0 || kind.Names[^1].MaxScore != null)
            problems.Add($"{label}: names の最後は maxScore なし (それ以上すべて)");
        var limits = kind.Names.Where(n => n.MaxScore != null).Select(n => n.MaxScore!.Value).ToList();
        if (limits.Zip(limits.Skip(1)).Any(p => p.First >= p.Second) || kind.Names.SkipLast(1).Any(n => n.MaxScore == null))
            problems.Add($"{label}: names の maxScore は小さい順に、最後以外すべて書く");

        if (kind.SlotRows.Count == 0) problems.Add($"{label}: slotRows が空");
        for (var i = 0; i < kind.SlotRows.Count; i++)
        {
            var row = kind.SlotRows[i];
            if (row.Length != 3 || row[0] < 0 || row[0] > row[1] || row[1] > row[2] || row[2] > 100)
                problems.Add($"{label}: slotRows の {i + 1} 行目 [{string.Join(", ", row)}] は 0 ≤ 1 スロット ≤ 2 スロット ≤ 3 スロット ≤ 100 の 3 つ");
        }

        foreach (var (list, ranges) in new[] { ("skill1", kind.FirstSkills), ("skill2", kind.SecondSkills) })
        {
            foreach (var group in ranges.GroupBy(r => r.Skill).Where(g => g.Count() > 1))
                problems.Add($"{label}: {list} に「{group.Key}」が重複");
            foreach (var range in ranges)
            {
                var where = $"{label} の {list} {range.Skill}";
                if (!systems.Contains(range.Skill)) problems.Add($"{where}: {GameData.SkillFile} に無いスキル");
                if (range.Max is < 1 or > MaxCharmPoints) problems.Add($"{where}: max {range.Max} (1〜{MaxCharmPoints})");
                if (list == "skill1" && (range.Min < 1 || range.Min > range.Max || range.Min * 10 < range.Max))
                    problems.Add($"{where}: min {range.Min} は 1 以上・max 以下で、min × 10 ≥ max (スロット値が 1 以上になる)");
                if (list == "skill2" && range.Min is > 0 or < -MaxCharmPoints)
                    problems.Add($"{where}: min {range.Min} (-{MaxCharmPoints}〜0)");
            }
        }
    }

    private static int Gcd(int a, int b) => b == 0 ? Math.Abs(a) : Gcd(b, a % b);
}
