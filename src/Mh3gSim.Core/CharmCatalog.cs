namespace Mh3gSim.Core;

/// <summary>必要なお守りの条件を満たす、実在するお守りの情報。</summary>
/// <param name="Tables">条件を満たすお守りが出るテーブル (番号順)</param>
/// <param name="Count">条件を満たすお守りの種類数 (お守りの種類・スキル・スロットが違えば別に数える)</param>
/// <param name="Kinds">条件を満たすお守りが出るお守りの種類 (なぞのお守り 等)</param>
/// <param name="Examples">条件に近い順の例</param>
public sealed record CharmAvailability(IReadOnlyList<int> Tables, int Count, IReadOnlyList<string> Kinds, IReadOnlyList<CharmEntry> Examples);

/// <summary>
/// 実在するお守りの一覧 (charms.json の作り方で全テーブル分を作ったもの)。初めて使う時に作る。
/// お守りの自動計算の候補づくりと、入力したお守りが出るテーブルの確認に使う。
/// </summary>
public sealed class CharmCatalog
{
    private readonly Lazy<List<CharmEntry>> entries;
    private readonly List<string> kindOrder;

    public CharmCatalog(CharmModel model)
    {
        TableNumbers = model.Tables.Select(t => t.Table).Order().ToList();
        kindOrder = model.Kinds.Select(k => k.Name).ToList();
        entries = new Lazy<List<CharmEntry>>(() => CharmGenerator.GenerateAll(model));
    }

    public IReadOnlyList<int> TableNumbers { get; }

    public IReadOnlyList<CharmEntry> Entries => entries.Value;

    /// <summary>
    /// 自動計算の候補: 実在するお守りを「欲しいスキルの系統ごとのポイント + スロット数」に直した組み合わせ (重複なし)。
    /// 欲しいスキルがマイナスのお守りは目標にしない (それより弱いので、成立してもマイナスの無いお守りでも成立する)。
    /// </summary>
    /// <param name="table">このテーブルで出るお守りだけにする (null なら全テーブル)</param>
    public List<CharmRequirement> RequirementsFor(IReadOnlyList<SkillRequirement> requirements, int? table)
    {
        var projector = new Projector(requirements);
        var found = new Dictionary<string, CharmRequirement>();
        foreach (var entry in Entries)
        {
            if (table is { } only && !entry.AppearsOn(only)) continue;
            var points = projector.Project(entry);
            if (points.Any(p => p < 0)) continue;
            found.TryAdd($"{string.Join(",", points)}|{entry.Slots}", new CharmRequirement { Points = points, Slots = entry.Slots });
        }
        return [.. found.Values];
    }

    /// <summary>必要なお守りの条件 (これ以上のポイント・スロット) を満たす実在のお守り。</summary>
    public CharmAvailability Describe(CharmRequirement requirement, IReadOnlyList<SkillRequirement> requirements, int? table, int exampleCount)
    {
        var projector = new Projector(requirements);
        var matches = Entries
            .Where(e => table is not { } only || e.AppearsOn(only))
            .Select(e => (Entry: e, Points: projector.Project(e)))
            .Where(m => requirement.IsAtMost(m.Points, m.Entry.Slots))
            .ToList();
        var mask = matches.Aggregate(0, (bits, m) => bits | m.Entry.TableMask);
        var tables = TableNumbers.Where(t => (mask & CharmEntry.TableBit(t)) != 0 && (table is not { } only || only == t)).ToList();
        var kinds = kindOrder.Where(kind => matches.Any(m => m.Entry.Kind == kind)).ToList();
        // 条件にぴったりのもの (余分なポイント・スロットが少ない) から、出るテーブルが多い順
        var examples = matches
            .OrderBy(m => m.Entry.Slots - requirement.Slots + m.Points.Select((p, i) => p - requirement.Points[i]).Sum())
            .ThenByDescending(m => System.Numerics.BitOperations.PopCount((uint)m.Entry.TableMask))
            .Take(exampleCount).Select(m => m.Entry).ToList();
        return new CharmAvailability(tables, matches.Count, kinds, examples);
    }

    /// <summary>入力したお守りと同じスキル・ポイント・スロットの、実在するお守り (種類ごと)。</summary>
    public List<CharmEntry> FindSame(Charm charm) => Entries.Where(e => e.SameContent(charm)).ToList();

    /// <summary>お守りのスキルを、欲しいスキルの系統ごとのポイントに直す。</summary>
    private sealed class Projector(IReadOnlyList<SkillRequirement> requirements)
    {
        private readonly Dictionary<string, int> indexBySystem =
            requirements.Select((r, i) => (r.System, Index: i)).ToDictionary(x => x.System, x => x.Index);

        public int[] Project(CharmEntry entry)
        {
            var points = new int[requirements.Count];
            if (indexBySystem.TryGetValue(entry.Skill1, out var first)) points[first] += entry.Points1;
            if (entry.Skill2.Length > 0 && indexBySystem.TryGetValue(entry.Skill2, out var second)) points[second] += entry.Points2;
            return points;
        }
    }
}
