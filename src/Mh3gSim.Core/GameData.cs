using System.Reflection;
using System.Text.Json;

namespace Mh3gSim.Core;

/// <summary>exe に埋め込んだ JSON (Data/*.json) を読み込む。</summary>
public sealed class GameData
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public required IReadOnlyList<Armor> Armors { get; init; }
    public required IReadOnlyList<Decoration> Decorations { get; init; }
    public required IReadOnlyList<SkillSystem> SkillSystems { get; init; }

    public static GameData LoadEmbedded()
    {
        return new GameData
        {
            Armors = Load<List<Armor>>("armors.json"),
            Decorations = Load<List<Decoration>>("decorations.json"),
            SkillSystems = Load<List<SkillSystem>>("skills.json"),
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
            ?? throw new InvalidOperationException($"埋め込みデータ {fileName} が見つかりません");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"{fileName} の読み込みに失敗しました");
    }
}
