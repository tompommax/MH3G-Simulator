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

    /// <summary>ファイル保存用: 検索条件の見出し + 結果を順番に並べる。</summary>
    public static string FormatExport(SearchCondition condition, IReadOnlyList<SearchResult> results, string title)
    {
        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine();
        text.AppendLine("■ 検索条件");
        text.AppendLine("  スキル: " + string.Join("、", condition.Requirements.Select(r => $"{r.ActivationName}({r.System} {r.Points})")));
        text.AppendLine($"  職業: {(condition.IsGunner ? "ガンナー" : "剣士")} / 性別: {(condition.IsFemale ? "女" : "男")} / レア度上限: {condition.MaxRarity} / 武器スロット: {condition.WeaponSlots}");
        text.AppendLine($"  マイナススキル: {(condition.AvoidNegativeSkills ? "発動させない" : "発動を許す")}");
        var charms = condition.Charms.Where(c => !c.IsNone).ToList();
        text.AppendLine($"  お守り候補: {charms.Count} 個{(condition.Charms.Any(c => c.IsNone) ? " + お守りなし" : "")}");
        foreach (var charm in charms) text.AppendLine($"    {charm}");
        text.AppendLine($"  結果: {results.Count} 件");

        for (var i = 0; i < results.Count; i++)
        {
            text.AppendLine();
            text.AppendLine($"========== No.{i + 1} ==========");
            text.Append(FormatResult(results[i], condition.WeaponSlots));
        }
        return text.ToString();
    }

    private static string SlotMarks(int slots) => new string('○', slots) + new string('－', 3 - slots);
}
