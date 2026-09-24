using System.Collections.Concurrent;

namespace Mh3gSim.Core;

/// <summary>
/// お守りの自動計算の候補: 欲しいスキルの系統ごとのポイント (0 以上。お守りのスキルは 2 つまでなので 0 でないのは 2 系統まで) とスロット数。
/// 実在するお守りでちょうど実現できる組み合わせだけを CharmCatalog が作る。
/// </summary>
public sealed class CharmRequirement
{
    /// <summary>SearchCondition.Requirements と同じ並びの、系統ごとのポイント。</summary>
    public required int[] Points { get; init; }
    public required int Slots { get; init; }

    public bool IsZero => Slots == 0 && Points.All(p => p == 0);

    /// <summary>ポイント・スロットがすべて points / slots 以上か。</summary>
    public bool IsAtLeast(int[] points, int slots)
    {
        if (Slots < slots) return false;
        for (var i = 0; i < Points.Length; i++)
            if (Points[i] < points[i]) return false;
        return true;
    }

    /// <summary>ポイント・スロットがすべて points / slots 以下か (そのお守りがこの条件を満たすか)。</summary>
    public bool IsAtMost(int[] points, int slots)
    {
        if (Slots > slots) return false;
        for (var i = 0; i < Points.Length; i++)
            if (Points[i] > points[i]) return false;
        return true;
    }

    /// <summary>検索に渡すお守り (欲しいスキルの系統だけを持つ)。</summary>
    public Charm ToCharm(IReadOnlyList<SkillRequirement> requirements) => MakeCharm(requirements, Points, Slots);

    internal static Charm MakeCharm(IReadOnlyList<SkillRequirement> requirements, int[] points, int slots)
    {
        var skills = points.Select((p, i) => (requirements[i].System, Points: p)).Where(s => s.Points > 0).ToList();
        return new Charm
        {
            Skill1 = skills.Count > 0 ? skills[0].System : "",
            Points1 = skills.Count > 0 ? skills[0].Points : 0,
            Skill2 = skills.Count > 1 ? skills[1].System : "",
            Points2 = skills.Count > 1 ? skills[1].Points : 0,
            Slots = slots,
        };
    }
}

/// <summary>自動計算の結果 1 件: 必要なお守りと、そのお守りで組める最上位の構成。</summary>
public sealed record CharmSuggestion(CharmRequirement Requirement, SearchResult Best)
{
    /// <summary>条件を満たす実在のお守り (出るテーブル・種類・例)。CharmCatalog.Describe で付ける。</summary>
    public CharmAvailability? Availability { get; init; }
}

/// <param name="TruncatedSolves">装飾品探索を上限で打ち切った構成の数 (0 でなければ見落としの可能性がある)</param>
public sealed record CharmFinderOutcome(List<CharmSuggestion> Suggestions, int TruncatedSolves);

/// <summary>
/// お守りの自動計算: 欲しいスキルが成立するお守りのうち、それより弱い実在のお守りでは成立しない「最小限のお守り」を探す。
/// お守りのポイント・スロットは多いほど成立しやすい (単調) ので、系統 2 つずつの組・スロット数ごとに
/// 「成立する最小のポイントの組」の境目 (階段状) を少ない検索回数でたどり、実在するお守りをその境目と比べて選ぶ。
/// </summary>
public sealed class CharmFinder
{
    private readonly SkillSearcher searcher;

    public CharmFinder(SkillSearcher searcher) => this.searcher = searcher;

    /// <summary>検証用: 境目をたどった後、推論を使わず候補を全部 1 つずつ検索する (打ち切りが起きた時と同じ道を通す)。</summary>
    internal bool ForceDirectProbing { get; init; }

    /// <param name="candidates">実在するお守りで実現できる候補 (重複なし)。</param>
    public CharmFinderOutcome Find(SearchCondition condition, IReadOnlyList<CharmRequirement> candidates,
        IProgress<double>? progress, CancellationToken cancellation)
    {
        if (candidates.Count == 0 || condition.Requirements.Count == 0) return new CharmFinderOutcome([], 0);
        return new FinderRun(searcher, condition, candidates, cancellation, ForceDirectProbing).Execute(progress);
    }

    private sealed class FinderRun
    {
        private readonly SkillSearcher searcher;
        private readonly SearchCondition probeCondition;
        private readonly IReadOnlyList<SkillRequirement> requirements;
        private readonly IReadOnlyList<CharmRequirement> candidates;
        private readonly CancellationToken cancellation;
        private readonly bool forceDirectProbing;
        /// <summary>検索済みの (ポイント, スロット) → 最上位の構成 (成立しなければ null)。</summary>
        private readonly ConcurrentDictionary<string, SearchResult?> probed = new();
        /// <summary>成立する最小の組み合わせ (境目の点)。</summary>
        private readonly ConcurrentBag<(int[] Points, int Slots)> boundary = [];
        private int truncatedSolves;
        /// <summary>装飾品探索の打ち切りで「見つからない」が出た (その結果から他の候補を推論してはいけない)。</summary>
        private volatile bool uncertain;

        public FinderRun(SkillSearcher searcher, SearchCondition condition, IReadOnlyList<CharmRequirement> candidates,
            CancellationToken cancellation, bool forceDirectProbing)
        {
            this.searcher = searcher;
            this.candidates = candidates;
            this.cancellation = cancellation;
            this.forceDirectProbing = forceDirectProbing;
            requirements = condition.Requirements;
            // 候補のお守りを全部渡しておく (マイナスになり得る系統・スロット上限の見積もりに使う)
            probeCondition = condition.WithCharms(candidates.Select(c => c.ToCharm(requirements)).ToList());
        }

        public CharmFinderOutcome Execute(IProgress<double>? progress)
        {
            var slices = BuildSlices();
            var walked = 0;
            RunParallel(slices, (probe, slice) =>
            {
                WalkBoundary(probe, slice);
                progress?.Report(0.6 * Interlocked.Increment(ref walked) / slices.Count);
            });

            // 打ち切りの「見つからない」は成立しない証拠ではないので、単調性を使った推論をやめて候補を 1 つずつ確かめる
            var minimal = uncertain || forceDirectProbing ? MinimalByDirectProbing(progress) : MinimalFeasibleCandidates();
            var suggestions = new ConcurrentBag<CharmSuggestion>();
            var finished = 0;
            RunParallel(minimal, (probe, requirement) =>
            {
                var best = Probe(probe, requirement.Points, requirement.Slots);
                if (best != null) suggestions.Add(new CharmSuggestion(requirement, best));
                progress?.Report(0.8 + 0.2 * Interlocked.Increment(ref finished) / minimal.Count);
            });
            var byResult = Comparer<SearchResult>.Create(SkillSearcher.CompareResults);
            return new CharmFinderOutcome([.. suggestions.OrderByDescending(s => s.Best, byResult)], truncatedSolves);
        }

        /// <summary>候補を全部 1 つずつ検索し、成立したものの中で最小のものを返す (打ち切りの影響はその候補だけに留まる)。</summary>
        private List<CharmRequirement> MinimalByDirectProbing(IProgress<double>? progress)
        {
            var feasible = new ConcurrentBag<CharmRequirement>();
            var probedCount = 0;
            RunParallel(candidates, (probe, candidate) =>
            {
                if (Probe(probe, candidate.Points, candidate.Slots) != null) feasible.Add(candidate);
                progress?.Report(0.6 + 0.2 * Interlocked.Increment(ref probedCount) / candidates.Count);
            });
            var list = feasible.ToList();
            return list.Where(c => !list.Any(other => other != c && IsStrictlyWeaker(other, c))).ToList();
        }

        /// <summary>系統 2 つの組 (系統が 1 つならその 1 つ) × スロット数ごとの探索範囲。各系統の上限は候補の最大ポイント。</summary>
        private List<Slice> BuildSlices()
        {
            var maxima = Enumerable.Range(0, requirements.Count).Select(i => candidates.Max(c => c.Points[i])).ToArray();
            var slotCounts = candidates.Select(c => c.Slots).Distinct().Order().ToList();
            var pairs = new List<(int First, int Second)>();
            if (requirements.Count == 1) pairs.Add((0, -1));
            for (var i = 0; i < requirements.Count; i++)
                for (var j = i + 1; j < requirements.Count; j++)
                    pairs.Add((i, j));
            return [.. from pair in pairs
                       from slots in slotCounts
                       select new Slice(pair.First, pair.Second, slots, requirements.Count,
                           maxima[pair.First], pair.Second < 0 ? 0 : maxima[pair.Second])];
        }

        /// <summary>
        /// 1 つの組・スロット数で、成立する最小の (第 1 系統, 第 2 系統) ポイントの組を階段状にたどる。
        /// 第 1 系統を 0 から増やしながら、成立する第 2 系統の最小値を上から下げていく (検索回数は両方の上限の和程度)。
        /// 成立は単調 (ポイントが多いほど成立しやすい) なので、前の第 1 系統で求めた最小値から下げ始めればよい。
        /// </summary>
        private void WalkBoundary(SkillSearcher.Probe probe, Slice slice)
        {
            if (!IsFeasible(probe, slice.Point(slice.FirstMax, slice.SecondMax), slice.Slots)) return;
            var second = slice.SecondMax;
            for (var first = 0; first <= slice.FirstMax; first++)
            {
                if (!IsFeasible(probe, slice.Point(first, second), slice.Slots)) continue;
                while (second > 0 && IsFeasible(probe, slice.Point(first, second - 1), slice.Slots)) second--;
                boundary.Add((slice.Point(first, second), slice.Slots));
                if (second == 0) break;
            }
        }

        private bool IsFeasible(SkillSearcher.Probe probe, int[] points, int slots) => Probe(probe, points, slots) != null;

        private SearchResult? Probe(SkillSearcher.Probe probe, int[] points, int slots)
        {
            var key = $"{string.Join(",", points)}|{slots}";
            if (probed.TryGetValue(key, out var cached)) return cached;
            var before = probe.TruncatedSolves;
            var best = probe.FindBest(CharmRequirement.MakeCharm(requirements, points, slots));
            var truncated = probe.TruncatedSolves - before;
            Interlocked.Add(ref truncatedSolves, truncated);
            if (best == null && truncated > 0) uncertain = true;
            probed[key] = best;
            return best;
        }

        /// <summary>実在する候補のうち、境目のどれか以上で成立し、成立する別の候補より全面的に強くはないもの。</summary>
        private List<CharmRequirement> MinimalFeasibleCandidates()
        {
            var points = boundary.ToList();
            var feasible = candidates.Where(c => points.Any(b => c.IsAtLeast(b.Points, b.Slots))).ToList();
            return feasible.Where(c => !feasible.Any(other => other != c && IsStrictlyWeaker(other, c))).ToList();
        }

        /// <summary>a のポイント・スロットがすべて b 以下で、どこかが b より小さい。</summary>
        private static bool IsStrictlyWeaker(CharmRequirement a, CharmRequirement b) =>
            b.IsAtLeast(a.Points, a.Slots) && !a.IsAtLeast(b.Points, b.Slots);

        private void RunParallel<T>(IReadOnlyList<T> items, Action<SkillSearcher.Probe, T> body)
        {
            var options = new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Environment.ProcessorCount };
            try
            {
                Parallel.ForEach(items, options,
                    () => searcher.CreateProbe(probeCondition, cancellation),
                    (item, _, probe) => { body(probe, item); return probe; },
                    _ => { });
            }
            catch (AggregateException exception) when (exception.InnerExceptions.All(e => e is OperationCanceledException))
            {
                // 中止は呼び出し側で「中止」として扱えるよう、まとめられた例外をほどいて投げ直す
                throw new OperationCanceledException(cancellation);
            }
        }

        /// <summary>第 1・第 2 系統 (Second = -1 なら系統 1 つ) とスロット数を決めた探索範囲。</summary>
        private sealed record Slice(int First, int Second, int Slots, int Count, int FirstMax, int SecondMax)
        {
            public int[] Point(int first, int second)
            {
                var points = new int[Count];
                points[First] = first;
                if (Second >= 0) points[Second] = second;
                return points;
            }
        }
    }
}
