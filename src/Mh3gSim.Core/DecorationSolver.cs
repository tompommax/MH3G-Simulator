namespace Mh3gSim.Core;

/// <summary>
/// 防具の合計ポイントと空きスロットから、要求スキルを満たす装飾品の組み合わせを探す。
/// スロット穴は 1〜3 の大きさで、1 つの穴に複数の珠を入れられる (3 穴 = 3 / 2+1 / 1+1+1)。
/// </summary>
internal sealed class DecorationSolver
{
    /// <summary>1 回の Solve で訪問する探索ノードの上限。超えたら「見つからない」扱いで打ち切る。</summary>
    private const int MaxNodeVisits = 20000;

    private readonly int[] targets;
    private readonly List<DecorationOption>[] optionsBySkill;
    private readonly Dictionary<(int Skill, int Deficit), List<int[]>> comboCache = [];
    private int nodeVisits;

    public DecorationSolver(IReadOnlyList<string> systems, int[] targets, IEnumerable<Decoration> decorations)
    {
        this.targets = targets;
        optionsBySkill = new List<DecorationOption>[systems.Count];
        for (var k = 0; k < systems.Count; k++)
        {
            var system = systems[k];
            optionsBySkill[k] = decorations
                .Where(d => d.Skills.GetValueOrDefault(system) > 0)
                .Select(d => new DecorationOption(d, systems.Select(s => d.Skills.GetValueOrDefault(s)).ToArray()))
                .OrderByDescending(o => o.Points[k] / (double)o.Decoration.Slots)
                .ToList();
        }
        PointsPerSlot = optionsBySkill
            .Select((options, k) => options.Count == 0 ? 0 : options.Max(o => o.Points[k] / (double)o.Decoration.Slots))
            .ToArray();
    }

    /// <summary>直前の Solve がノード上限で打ち切られたか (true なら「見つからない」は不確定)。</summary>
    public bool WasTruncated => nodeVisits > MaxNodeVisits;

    /// <summary>系統ごとの 1 スロットあたり最大ポイント (珠が無い系統は 0)。探索の上限見積もりに使う。</summary>
    public double[] PointsPerSlot { get; }

    /// <param name="holes">胴以外の穴の数 [0, 1穴, 2穴, 3穴]</param>
    /// <param name="bodyMultiplier">胴に入れた珠の倍率 (1 + 胴系統倍化の部位数)</param>
    /// <param name="accept">見つかった組み合わせを最終確認する (マイナススキル判定など)</param>
    public Solution? Solve(int[] basePoints, int[] holes, int bodySlots, int bodyMultiplier,
        Func<Solution, bool> accept)
    {
        nodeVisits = 0;
        if (bodyMultiplier == 1)
        {
            var merged = (int[])holes.Clone();
            merged[bodySlots]++;
            return SolveGeneral(basePoints, merged, [], accept);
        }

        var candidates = optionsBySkill
            .Where((_, k) => basePoints[k] < targets[k])
            .SelectMany(o => o).GroupBy(o => o.Decoration).Select(g => g.First()).ToList();
        foreach (var filling in EnumerateBodyFillings(candidates, bodySlots))
        {
            var points = (int[])basePoints.Clone();
            foreach (var option in filling) Add(points, option.Points, bodyMultiplier);
            var solution = SolveGeneral(points, holes, filling, accept);
            if (solution != null) return solution;
            if (nodeVisits > MaxNodeVisits) return null;
        }
        return null;
    }

    private Solution? SolveGeneral(int[] points, int[] holes, List<DecorationOption> bodyDecorations,
        Func<Solution, bool> accept)
    {
        return Recurse(points, holes, [0, 0, 0, 0], [], bodyDecorations, accept);
    }

    /// <summary>
    /// 未達の系統を先頭から 1 つ選び、その不足を埋める珠の組み合わせを試す。
    /// 珠の副スキル (マイナス) で満たしていた系統が下回っても、その系統が再び「未達」として選ばれ
    /// 追加の珠で埋め直される (匠珠と斬鉄珠のように互いに打ち消し合う珠の組み合わせに対応)。
    /// 珠を 1 個以上足すたびに空き容量が減るので、探索は必ず終わる。
    /// </summary>
    private Solution? Recurse(int[] points, int[] holes, int[] used, List<DecorationOption> chosen,
        List<DecorationOption> bodyDecorations, Func<Solution, bool> accept)
    {
        if (++nodeVisits > MaxNodeVisits) return null;
        var skill = FirstUnmetSkill(points);
        if (skill < 0)
        {
            var solution = new Solution([.. bodyDecorations.Select(o => o.Decoration)], [.. chosen.Select(o => o.Decoration)]);
            return accept(solution) ? solution : null;
        }
        if (!CanCoverWithRemaining(points, holes, used)) return null;

        var options = optionsBySkill[skill];
        foreach (var counts in Combos(skill, targets[skill] - points[skill]))
        {
            var nextUsed = (int[])used.Clone();
            for (var i = 0; i < counts.Length; i++) nextUsed[options[i].Decoration.Slots] += counts[i];
            if (!Fits(holes, nextUsed)) continue;

            var nextPoints = (int[])points.Clone();
            var added = 0;
            for (var i = 0; i < counts.Length; i++)
            {
                Add(nextPoints, options[i].Points, counts[i]);
                for (var c = 0; c < counts[i]; c++) { chosen.Add(options[i]); added++; }
            }
            var result = Recurse(nextPoints, holes, nextUsed, chosen, bodyDecorations, accept);
            chosen.RemoveRange(chosen.Count - added, added);
            if (result != null) return result;
            if (nodeVisits > MaxNodeVisits) return null;
        }
        return null;
    }

    private int FirstUnmetSkill(int[] points)
    {
        for (var k = 0; k < targets.Length; k++)
            if (points[k] < targets[k]) return k;
        return -1;
    }

    /// <summary>残りの空き容量で全系統の不足を埋められる見込みがあるか (1 スロットあたり最大ポイントで換算した下限で判定)。</summary>
    private bool CanCoverWithRemaining(int[] points, int[] holes, int[] used)
    {
        var remaining = holes[1] + 2 * holes[2] + 3 * holes[3] - (used[1] + 2 * used[2] + 3 * used[3]);
        double needed = 0;
        for (var k = 0; k < targets.Length; k++)
        {
            var deficit = targets[k] - points[k];
            if (deficit <= 0) continue;
            if (PointsPerSlot[k] == 0) return false;
            needed += deficit / PointsPerSlot[k];
        }
        return needed <= remaining + 1e-9;
    }

    /// <summary>穴 (1/2/3) に珠 (1/2/3) が全て収まるか。3 穴には 2+1 や 1+1+1 も入る。</summary>
    public static bool Fits(int[] holes, int[] used)
    {
        if (used[3] > holes[3]) return false;
        if (used[2] > holes[2] + holes[3] - used[3]) return false;
        var capacity = holes[1] + 2 * holes[2] + 3 * holes[3];
        return used[1] + 2 * used[2] + 3 * used[3] <= capacity;
    }

    /// <summary>その系統の不足ポイントを埋める珠の個数ベクトル (無駄な珠を含まない最小構成) を少ないスロット順に返す。</summary>
    private List<int[]> Combos(int skill, int deficit)
    {
        if (comboCache.TryGetValue((skill, deficit), out var cached)) return cached;
        var options = optionsBySkill[skill];
        var combos = new List<int[]>();
        var counts = new int[options.Count];

        void Build(int index, int sum)
        {
            if (sum >= deficit)
            {
                var smallestUsed = options.Where((_, i) => counts[i] > 0).Min(o => o.Points[skill]);
                if (sum - smallestUsed < deficit) combos.Add((int[])counts.Clone());
                return;
            }
            if (index == options.Count) return;
            var points = options[index].Points[skill];
            var maxCount = (deficit - sum + points - 1) / points;
            for (var c = maxCount; c >= 0; c--)
            {
                counts[index] = c;
                Build(index + 1, sum + c * points);
            }
            counts[index] = 0;
        }

        Build(0, 0);
        combos.Sort((a, b) => SlotSum(a).CompareTo(SlotSum(b)));
        comboCache[(skill, deficit)] = combos;
        return combos;

        int SlotSum(int[] c) => c.Select((n, i) => n * options[i].Decoration.Slots).Sum();
    }

    private static IEnumerable<List<DecorationOption>> EnumerateBodyFillings(List<DecorationOption> candidates, int bodySlots)
    {
        var results = new List<List<DecorationOption>>();
        var current = new List<DecorationOption>();

        void Build(int start, int remaining)
        {
            results.Add([.. current]);
            for (var i = start; i < candidates.Count; i++)
            {
                if (candidates[i].Decoration.Slots > remaining) continue;
                current.Add(candidates[i]);
                Build(i, remaining - candidates[i].Decoration.Slots);
                current.RemoveAt(current.Count - 1);
            }
        }

        Build(0, bodySlots);
        // 胴に多く詰める (倍化の恩恵が大きい) 順に試す
        return results.OrderByDescending(f => f.Sum(o => o.Decoration.Slots));
    }

    private static void Add(int[] points, int[] delta, int times)
    {
        for (var k = 0; k < points.Length; k++) points[k] += delta[k] * times;
    }

    private sealed record DecorationOption(Decoration Decoration, int[] Points);

    public sealed record Solution(List<Decoration> BodyDecorations, List<Decoration> OtherDecorations);
}
