using System.Text;

namespace Mh3gSim.Core;

/// <summary>検索結果をテキストにする (画面の詳細表示・コピー・ファイル保存で共通)。</summary>
public static class ResultTextFormatter
{
    private const int EquivalentsShown = 6;
    private const int PointsPerLine = 6;

    /// <summary>1 件分の詳細 (防具・お守り・装飾品・発動スキル・耐性・スキルポイント)。</summary>
    public static string FormatResult(SearchResult result, int weaponSlots)
    {
        var text = new StringBuilder();
        text.AppendLine($"■ 防具   防御力 {result.Defense} → 最終 {result.MaxDefense}");
        for (var part = 0; part < ArmorParts.Count; part++)
        {
            var armor = result.Armors[part];
            var skills = string.Join(" ", armor.Skills.Select(s => $"{s.Key}{s.Value:+0;-0}"));
            text.AppendLine($"  {ArmorParts.Names[part]}: {armor.Name}  [{SlotMarks(armor.Slots)}] レア{armor.Rarity} 防{armor.Defense}→{armor.MaxDefense}  {skills}");
            var equivalents = result.Equivalents[part];
            if (equivalents.Count > 0)
            {
                var names = string.Join(", ", equivalents.Take(EquivalentsShown).Select(a => a.Name));
                text.AppendLine($"        同等品(要求スキル・スロット同じ): {names}{(equivalents.Count > EquivalentsShown ? " …" : "")}");
            }
        }
        text.AppendLine($"  お守り: {result.Charm}");
        text.AppendLine($"  武器スロット: {weaponSlots}");
        text.AppendLine();

        text.AppendLine("■ 装飾品");
        foreach (var group in result.Decorations.GroupBy(d => d.Location))
            text.AppendLine($"  {group.Key}: {string.Join(", ", group.Select(d => d.Decoration.Name))}");
        if (result.Decorations.Count == 0) text.AppendLine("  (なし)");
        if (result.FreeSlots.Count > 0) text.AppendLine($"  空きスロット: {string.Join(" ", result.FreeSlots)}");
        text.AppendLine();

        text.AppendLine("■ 発動スキル");
        foreach (var skill in result.ActiveSkills.OrderBy(s => s.IsNegative))
            text.AppendLine($"  {(skill.IsNegative ? "▼" : "●")} {skill.Activation.Name}  ({skill.System} {skill.Total})");
        text.AppendLine();

        text.AppendLine("■ 耐性");
        var resist = result.Resist;
        text.AppendLine("  " + string.Join("  ", GameData.ResistNames.Select((n, i) => $"{n}{resist[i]:+0;-0;0}")));
        text.AppendLine();

        text.AppendLine("■ スキルポイント合計");
        var points = result.SkillTotals.Where(p => p.Value != 0).OrderByDescending(p => p.Value)
            .Select(p => $"{p.Key}{p.Value:+0;-0}");
        foreach (var chunk in points.Chunk(PointsPerLine)) text.AppendLine("  " + string.Join("  ", chunk));
        return text.ToString();
    }

    /// <summary>
    /// ファイル保存用: 検索条件の見出し + 結果を順番に並べる。
    /// charmSearch があればお守りの自動計算の結果として出す (suggestions は results と同じ並び)。
    /// </summary>
    public static string FormatExport(SearchCondition condition, IReadOnlyList<SearchResult> results, string title,
        CharmSearchContext? charmSearch = null)
    {
        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine();
        text.AppendLine("■ 検索条件");
        text.AppendLine("  スキル: " + string.Join("、", condition.Requirements.Select(r => $"{r.ActivationName}({r.System} {r.Points})")));
        text.AppendLine($"  職業: {(condition.IsGunner ? "ガンナー" : "剣士")} / 性別: {(condition.IsFemale ? "女" : "男")} / レア度上限: {condition.MaxRarity} / 武器スロット: {condition.WeaponSlots}");
        text.AppendLine($"  マイナススキル: {(condition.AvoidNegativeSkills ? "発動させない" : "発動を許す")}");
        text.AppendLine(charmSearch == null
            ? $"  お守り: {string.Join(" / ", condition.Charms)}"
            : $"  お守り: 自動計算 (成立に必要なお守りを探す、{(charmSearch.Table is { } table ? $"テーブル {table} で出るお守り" : "全テーブルのお守り")})");
        text.AppendLine($"  結果: {results.Count} 件");

        for (var i = 0; i < results.Count; i++)
        {
            text.AppendLine();
            text.AppendLine($"========== No.{i + 1} ==========");
            text.Append(charmSearch == null
                ? FormatResult(results[i], condition.WeaponSlots)
                : FormatSuggestion(charmSearch.Suggestions[i], condition.Requirements, condition.WeaponSlots, charmSearch.AllTables));
        }
        return text.ToString();
    }

    // ───────── お守りの自動計算 ─────────

    /// <summary>自動計算の結果を文字にするのに要る情報 (結果ごとの必要なお守り・絞ったテーブル・テーブル番号の一覧)。</summary>
    public sealed record CharmSearchContext(IReadOnlyList<CharmSuggestion> Suggestions, int? Table, IReadOnlyList<int> AllTables);

    /// <summary>自動計算の 1 件: 必要なお守りの説明 + 条件を満たす実在のお守り + そのお守りで組める構成。</summary>
    public static string FormatSuggestion(CharmSuggestion suggestion, IReadOnlyList<SkillRequirement> requirements, int weaponSlots,
        IReadOnlyList<int> allTables)
    {
        var requirement = suggestion.Requirement;
        var text = new StringBuilder();
        text.AppendLine($"■ 必要なお守り: {DescribeRequirement(requirement, requirements)}");
        if (!requirement.IsZero && suggestion.Availability is { } availability)
        {
            text.AppendLine($"  出るお守り: {string.Join("・", availability.Kinds)}");
            text.AppendLine($"  出るテーブル: {FormatTables(availability.Tables, allTables)} (条件を満たすお守り {availability.Count} 種類)");
            foreach (var example in availability.Examples)
                text.AppendLine($"  例: {example}  (テーブル {FormatTables(allTables.Where(example.AppearsOn).ToList(), allTables)})");
            text.AppendLine("  (欲しいスキル以外のスキルは問わない。マイナスのスキルが付いたお守りは、入力して通常の検索で確かめてください)");
        }
        text.AppendLine();
        text.Append(FormatResult(suggestion.Best, weaponSlots));
        return text.ToString();
    }

    /// <summary>「攻撃+6 以上・スロット 1 以上」のような必要条件の説明。</summary>
    public static string DescribeRequirement(CharmRequirement requirement, IReadOnlyList<SkillRequirement> requirements)
    {
        if (requirement.IsZero) return "不要 (お守りなしで成立)";
        var parts = requirement.Points.Select((p, i) => (requirements[i].System, Points: p)).Where(s => s.Points > 0)
            .Select(s => $"{s.System}{s.Points:+0} 以上").ToList();
        if (requirement.Slots > 0) parts.Add($"スロット {requirement.Slots} 以上");
        var text = string.Join("・", parts);
        return requirement.Points.All(p => p == 0) ? $"{text} (スキルは問わない)" : text;
    }

    /// <summary>テーブル番号の並びを「1〜10, 13, 14」のように縮める (全部なら「全テーブル」)。</summary>
    public static string FormatTables(IReadOnlyList<int> tables, IReadOnlyList<int> allTables)
    {
        if (tables.Count == 0) return "なし";
        if (tables.Count == allTables.Count) return "全テーブル";
        var parts = new List<string>();
        for (var i = 0; i < tables.Count; i++)
        {
            var start = tables[i];
            while (i + 1 < tables.Count && tables[i + 1] == tables[i] + 1) i++;
            parts.Add(tables[i] - start >= 2 ? $"{start}〜{tables[i]}" : tables[i] == start ? $"{start}" : $"{start}, {tables[i]}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>一覧表示用の短い形 (「攻撃+6 [○－－]」「スキル問わず [○○○]」「不要」)。</summary>
    public static string ShortRequirement(CharmRequirement requirement, IReadOnlyList<SkillRequirement> requirements)
    {
        if (requirement.IsZero) return "不要";
        var skills = requirement.Points.Select((p, i) => (requirements[i].System, Points: p)).Where(s => s.Points > 0)
            .Select(s => $"{s.System}{s.Points:+0}").ToList();
        return $"{(skills.Count == 0 ? "スキル問わず" : string.Join(" ", skills))} [{SlotMarks(requirement.Slots)}]";
    }

    private static string SlotMarks(int slots) => new string('○', slots) + new string('－', 3 - slots);
}
