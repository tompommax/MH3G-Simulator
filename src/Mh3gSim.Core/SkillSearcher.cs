namespace Mh3gSim.Core;

/// <summary>
/// 要求スキルを発動できる防具 5 部位 + お守り + 装飾品の組み合わせを、最終防御力の高い順に探す。
/// 部位ごとに「要求系統ポイント + スロット数」が同じ防具を 1 つの候補 (クラス) にまとめて分枝限定法で探索し、
/// 装飾品で要求を満たせた組み合わせについてだけ、クラス内からマイナススキルを避けられる最良の防具を選ぶ。
/// </summary>
public sealed class SkillSearcher
{
    private readonly GameData data;

    public SkillSearcher(GameData data) => this.data = data;

    public SearchOutcome Search(SearchCondition condition, IProgress<double>? progress, CancellationToken cancellation)
    {
        var run = new SearchRun(data, condition, cancellation);
        return run.Execute(progress);
    }

    /// <summary>結果の並び順: 最終防御力 → 空きスロット合計 → 初期防御力 (いずれも大きい方が上)。</summary>
    public static int CompareResults(SearchResult a, SearchResult b) =>
        CompareRanks(a.MaxDefense, a.FreeSlotTotal, a.Defense, b.MaxDefense, b.FreeSlotTotal, b.Defense);

    private static int CompareRanks(int maxDefenseA, int freeSlotsA, int defenseA, int maxDefenseB, int freeSlotsB, int defenseB)
    {
        var byMax = maxDefenseA.CompareTo(maxDefenseB);
        if (byMax != 0) return byMax;
        var byFree = freeSlotsA.CompareTo(freeSlotsB);
        return byFree != 0 ? byFree : defenseA.CompareTo(defenseB);
    }

    /// <summary>要求系統ポイントとスロット数が同じ防具のまとまり。探索はこの単位で行う。</summary>
    private sealed class Candidate
    {
        /// <summary>互いに劣らない防具 (防御力・マイナスになり得る系統ポイントのパレート集合)。防御力の高い順。</summary>
        public required List<Armor> Members { get; init; }
        /// <summary>Members のどれかに全面的に劣る防具 (結果の「同等品」表示用)。</summary>
        public required List<Armor> Covered { get; init; }
        public required int[] Points { get; init; }
        public required int Slots { get; init; }
        public required bool IsTorsoUp { get; init; }
        public int MaxDefense => Members[0].MaxDefense;
    }

    private sealed class SearchRun
    {
        private readonly GameData data;
        private readonly SearchCondition condition;
        private readonly CancellationToken cancellation;
        private readonly string[] systems;
        private readonly int[] targets;
        private readonly DecorationSolver solver;
        private readonly List<Candidate>[] candidates = new List<Candidate>[ArmorParts.Count];
        private readonly int[] partOrder;
        private readonly List<SearchResult> results = [];

        // DFS の状態
        private readonly Candidate[] chosen = new Candidate[ArmorParts.Count];
        private readonly Armor[] chosenMembers = new Armor[ArmorParts.Count];
        private Charm currentCharm = Charm.None;
        private double[] suffixValue = [];
        private int[][] suffixPoints = [];
        private int[] suffixDefense = [];
        private SearchResult? worstResult;
        private int truncatedSolves;

        // マイナススキル判定用 (負のスキルを持つ系統だけを配列で持ち、葉ごとの判定を軽くする)
        private readonly string[] negativeSystems;
        private readonly int[] negativeThresholds;
        private readonly Dictionary<Armor, int[]> armorNegativePoints = [];
        private readonly Dictionary<Armor, int[]> guardPoints = [];
        private readonly Dictionary<Decoration, int[]> decorationNegativePoints = [];
        private int[] charmNegativePoints = [];

        public SearchRun(GameData data, SearchCondition condition, CancellationToken cancellation)
        {
            this.data = data;
            this.condition = condition;
            this.cancellation = cancellation;
            systems = condition.Requirements.Select(r => r.System).ToArray();
            targets = condition.Requirements.Select(r => r.Points).ToArray();
            solver = new DecorationSolver(systems, targets, data.Decorations);

            var negative = data.SkillSystems.Where(sys => sys.Activations.Any(a => a.Points < 0)).ToArray();
            negativeSystems = negative.Select(sys => sys.System).ToArray();
            negativeThresholds = negative.Select(sys => sys.Activations.Where(a => a.Points < 0).Max(a => a.Points)).ToArray();
            foreach (var decoration in data.Decorations)
                decorationNegativePoints[decoration] = negativeSystems.Select(n => decoration.Skills.GetValueOrDefault(n)).ToArray();

            var pools = Enumerable.Range(0, ArmorParts.Count).Select(BuildPool).ToArray();
            foreach (var armor in pools.SelectMany(p => p))
                armorNegativePoints[armor] = negativeSystems.Select(armor.PointsOf).ToArray();
            var guardSystems = condition.AvoidNegativeSkills ? FindGuardSystems(pools) : [];
            for (var part = 0; part < ArmorParts.Count; part++) candidates[part] = BuildCandidates(pools[part], guardSystems);
            // 胴を最初に決める (胴系統倍化の値が胴に依存するため)。残りは候補の少ない順。
            partOrder = [ArmorParts.Body, .. Enumerable.Range(0, ArmorParts.Count)
                .Where(p => p != ArmorParts.Body).OrderBy(p => candidates[p].Count)];
        }

        public SearchOutcome Execute(IProgress<double>? progress)
        {
            var charms = condition.Charms.Count == 0 ? [Charm.None] : condition.Charms;
            var bodyCandidates = candidates[ArmorParts.Body];
            var totalSteps = Math.Max(1, charms.Count * bodyCandidates.Count);
            var step = 0;

            foreach (var charm in charms)
            {
                currentCharm = charm;
                charmNegativePoints = negativeSystems.Select(n => CharmPoints(charm, n)).ToArray();
                foreach (var body in bodyCandidates)
                {
                    cancellation.ThrowIfCancellationRequested();
                    chosen[ArmorParts.Body] = body;
                    PrepareSuffixBounds(body);
                    var points = new int[systems.Length];
                    for (var k = 0; k < systems.Length; k++)
                        points[k] = body.Points[k] + CharmPoints(charm, systems[k]);
                    Dfs(1, points, body.Slots, 1, body.MaxDefense);
                    progress?.Report(++step / (double)totalSteps);
                }
            }
            results.Sort((a, b) => CompareResults(b, a));
            return new SearchOutcome(results, truncatedSolves);
        }

        private List<Armor> BuildPool(int part) =>
            data.Armors.Where(a => a.Part == part
                && (condition.IsGunner ? a.Gunner : a.Blade)
                && (condition.IsFemale ? a.Female : a.Male)
                && a.Rarity <= condition.MaxRarity
                && !condition.ExcludedArmorIds.Contains(a.Id)).ToList();

        /// <summary>
        /// マイナススキル回避時に、要求外でも候補の同一視・支配判定で比べる必要がある系統。
        /// 防具・お守り・珠の最悪値を全部足しても負のスキルの発動値に届かない系統は、どの構成でも発動しないので除く
        /// (全系統で比べると候補がほとんど圧縮できず遅くなる)。
        /// </summary>
        private string[] FindGuardSystems(List<Armor>[] pools)
        {
            var charms = condition.Charms.Count == 0 ? [Charm.None] : condition.Charms;
            var relevantDecorations = data.Decorations
                .Where(d => systems.Any(s => d.Skills.GetValueOrDefault(s) > 0)).ToList();
            var bodyPool = pools[ArmorParts.Body];
            var torsoUpParts = pools.Count(p => p.Any(a => a.IsTorsoUp));
            var maxSlots = pools.Sum(p => p.Count == 0 ? 0 : p.Max(a => a.Slots))
                + (bodyPool.Count == 0 ? 0 : bodyPool.Max(a => a.Slots)) * torsoUpParts
                + condition.WeaponSlots + charms.Max(c => c.Slots);

            return data.SkillSystems
                .Where(system => !systems.Contains(system.System) && system.Activations.Any(a => a.Points < 0))
                .Where(system =>
                {
                    var name = system.System;
                    var bodyWorst = Math.Min(0, bodyPool.Count == 0 ? 0 : bodyPool.Min(a => a.PointsOf(name)));
                    var armorWorst = pools.Select((pool, part) => part == ArmorParts.Body
                        ? bodyWorst
                        : Math.Min(0, pool.Count == 0 ? 0 : pool.Min(a => a.IsTorsoUp ? bodyWorst : a.PointsOf(name)))).Sum();
                    var charmWorst = Math.Min(0, charms.Min(c => c.Skills().Where(s => s.Key == name).Sum(s => s.Value)));
                    var worstPerSlot = relevantDecorations
                        .Select(d => Math.Min(0, d.Skills.GetValueOrDefault(name)) / (double)d.Slots).DefaultIfEmpty(0).Min();
                    var lowerBound = armorWorst + charmWorst + worstPerSlot * maxSlots;
                    var activation = system.Activations.Where(a => a.Points < 0).Max(a => a.Points);
                    return lowerBound <= activation;
                })
                .Select(system => system.System).ToArray();
        }

        private List<Candidate> BuildCandidates(List<Armor> pool, string[] guardSystems)
        {
            foreach (var armor in pool) guardPoints[armor] = guardSystems.Select(armor.PointsOf).ToArray();

            var classes = pool.Where(a => !a.IsTorsoUp)
                .GroupBy(a => (string.Join(",", systems.Select(a.PointsOf)), a.Slots))
                .Select(g => BuildClass(g.ToList(), systems.Select(g.First().PointsOf).ToArray(), g.Key.Slots, isTorsoUp: false))
                .ToList();
            var survivors = classes.Where(c => !classes.Any(other => other != c && Dominates(other, c))).ToList();

            var torsoUps = pool.Where(a => a.IsTorsoUp).ToList();
            if (torsoUps.Count > 0) survivors.Add(BuildClass(torsoUps, new int[systems.Length], 0, isTorsoUp: true));
            return survivors.OrderByDescending(c => c.MaxDefense).ToList();
        }

        private Candidate BuildClass(List<Armor> armors, int[] points, int slots, bool isTorsoUp)
        {
            var members = new List<Armor>();
            var covered = new List<Armor>();
            foreach (var armor in armors.OrderByDescending(a => a.MaxDefense).ThenByDescending(a => a.Defense))
            {
                if (members.Any(m => Covers(m, armor))) covered.Add(armor);
                else members.Add(armor);
            }
            return new Candidate { Members = members, Covered = covered, Points = points, Slots = slots, IsTorsoUp = isTorsoUp };
        }

        /// <summary>a が b の代わりに使えて損をしない (防御力が同等以上で、マイナスになり得る系統のポイントも同等以上)。</summary>
        private bool Covers(Armor a, Armor b)
        {
            if (a.MaxDefense < b.MaxDefense || a.Defense < b.Defense) return false;
            var guardA = guardPoints[a];
            var guardB = guardPoints[b];
            for (var i = 0; i < guardA.Length; i++)
                if (guardA[i] < guardB[i]) return false;
            return true;
        }

        /// <summary>
        /// クラス a がクラス b を不要にする: 要求ポイント・スロットが同等以上で、b のどの防具にも a の中に代わりがある。
        /// (要求ポイントとスロットが同等以上なら同じ装飾品がそのまま入るので、b を使う構成は a で置き換えられる)
        /// </summary>
        private bool Dominates(Candidate a, Candidate b)
        {
            if (a.Slots < b.Slots) return false;
            for (var k = 0; k < a.Points.Length; k++)
                if (a.Points[k] < b.Points[k]) return false;
            return b.Members.All(memberB => a.Members.Any(memberA => Covers(memberA, memberB)));
        }

        /// <summary>胴決定後、残り部位で得られるポイント・スロット換算値・防御力の上限を後ろから累積する。</summary>
        private void PrepareSuffixBounds(Candidate body)
        {
            var perSlot = solver.PointsPerSlot;
            double Value(int[] points, int slots) =>
                slots + points.Select((p, k) => perSlot[k] > 0 ? Math.Max(0, p) / perSlot[k] : 0).Sum();

            var depthCount = ArmorParts.Count;
            suffixValue = new double[depthCount + 1];
            suffixPoints = new int[depthCount + 1][];
            suffixDefense = new int[depthCount + 1];
            suffixPoints[depthCount] = new int[systems.Length];

            for (var depth = depthCount - 1; depth >= 1; depth--)
            {
                var part = partOrder[depth];
                double maxValue = 0;
                var maxPoints = new int[systems.Length];
                var maxDefense = 0;
                foreach (var candidate in candidates[part])
                {
                    var points = candidate.IsTorsoUp ? body.Points : candidate.Points;
                    var value = candidate.IsTorsoUp ? Value(body.Points, body.Slots) : Value(points, candidate.Slots);
                    maxValue = Math.Max(maxValue, value);
                    for (var k = 0; k < systems.Length; k++) maxPoints[k] = Math.Max(maxPoints[k], points[k]);
                    maxDefense = Math.Max(maxDefense, candidate.MaxDefense);
                }
                suffixValue[depth] = suffixValue[depth + 1] + maxValue;
                suffixPoints[depth] = maxPoints.Select((p, k) => p + suffixPoints[depth + 1][k]).ToArray();
                suffixDefense[depth] = suffixDefense[depth + 1] + maxDefense;
            }
        }

        private void Dfs(int depth, int[] points, int slotTotal, int bodyMultiplier, int defense)
        {
            if (IsFullAndWorse(defense + suffixDefense[depth])) return;
            if (!CanStillReach(depth, points, slotTotal)) return;

            if (depth == ArmorParts.Count)
            {
                TryComplete(points, bodyMultiplier);
                return;
            }

            var part = partOrder[depth];
            var body = chosen[ArmorParts.Body];
            foreach (var candidate in candidates[part])
            {
                if (cancellation.IsCancellationRequested) return;
                chosen[part] = candidate;
                var next = (int[])points.Clone();
                var delta = candidate.IsTorsoUp ? body.Points : candidate.Points;
                for (var k = 0; k < next.Length; k++) next[k] += delta[k];
                var slots = slotTotal + (candidate.IsTorsoUp ? body.Slots : candidate.Slots);
                Dfs(depth + 1, next, slots, bodyMultiplier + (candidate.IsTorsoUp ? 1 : 0), defense + candidate.MaxDefense);
            }
        }

        /// <summary>残り部位を最大限に使っても要求に届かない枝を切る。</summary>
        private bool CanStillReach(int depth, int[] points, int slotTotal)
        {
            var perSlot = solver.PointsPerSlot;
            double slotsNeeded = 0;
            for (var k = 0; k < systems.Length; k++)
            {
                var deficit = targets[k] - points[k];
                if (deficit <= 0) continue;
                if (perSlot[k] == 0)
                {
                    if (deficit > suffixPoints[depth][k]) return false;
                    continue;
                }
                slotsNeeded += deficit / perSlot[k];
            }
            var available = slotTotal + condition.WeaponSlots + currentCharm.Slots + suffixValue[depth];
            return available + 1e-9 >= slotsNeeded;
        }

        /// <summary>
        /// 上位 N 件が埋まっていて、この枝の最終防御力の上限が N 件目より低ければ切る。
        /// 同点は空きスロット・初期防御力で逆転し得るので切らない。
        /// </summary>
        private bool IsFullAndWorse(int defenseUpperBound) =>
            worstResult != null && defenseUpperBound < worstResult.MaxDefense;

        private void TryComplete(int[] points, int bodyMultiplier)
        {
            var holes = new int[4];
            var holeLabels = new List<(int Size, string Label)>();
            for (var part = 0; part < ArmorParts.Count; part++)
            {
                if (part == ArmorParts.Body || chosen[part].IsTorsoUp || chosen[part].Slots == 0) continue;
                holes[chosen[part].Slots]++;
                holeLabels.Add((chosen[part].Slots, ArmorParts.Names[part]));
            }
            // 倍化なしの場合、胴の穴はソルバー内で通常の穴として合算される (ラベルだけここで足す)
            var bodySlots = chosen[ArmorParts.Body].Slots;
            if (bodyMultiplier == 1 && bodySlots > 0) holeLabels.Add((bodySlots, ArmorParts.Names[ArmorParts.Body]));
            AddHole(holes, holeLabels, condition.WeaponSlots, "武器");
            AddHole(holes, holeLabels, currentCharm.Slots, "お守り");

            // まず各クラスの代表で、マイナススキルを気にせず装飾品が足りるかを見る (ほとんどの葉はここで落ちる)
            var feasible = solver.Solve(points, holes, bodySlots, bodyMultiplier, _ => true);
            if (solver.WasTruncated) truncatedSolves++;
            if (feasible == null) return;

            var capacity = holes[1] + 2 * holes[2] + 3 * holes[3] + bodySlots;
            var best = condition.AvoidNegativeSkills
                ? FindBestMembers(points, holes, bodySlots, bodyMultiplier, capacity)
                : (chosen.Select(c => c.Members[0]).ToArray(), feasible);
            if (best == null) return;

            var (members, solution) = best.Value;
            var freeSlots = FreeSlots(capacity, solution);
            if (worstResult != null && CompareRanks(members.Sum(a => a.MaxDefense), freeSlots, members.Sum(a => a.Defense),
                    worstResult.MaxDefense, worstResult.FreeSlotTotal, worstResult.Defense) <= 0) return;

            results.Add(BuildResult(members, solution, holeLabels, bodyMultiplier));
            if (results.Count > condition.MaxResults) results.Remove(worstResult!);
            worstResult = results.Count >= condition.MaxResults ? results.MinBy(r => r, ResultComparer) : null;
        }

        private static int FreeSlots(int capacity, DecorationSolver.Solution solution) =>
            capacity - solution.BodyDecorations.Sum(d => d.Slots) - solution.OtherDecorations.Sum(d => d.Slots);

        /// <summary>
        /// 各クラスから 1 つずつ防具を選び、マイナススキルが発動しない装飾品の組み合わせがある中で最も順位が高いものを返す。
        /// 防御力の上限で枝を切る (代表の組み合わせで通れば、ほぼそこで決まる)。
        /// </summary>
        private (Armor[] Members, DecorationSolver.Solution Solution)? FindBestMembers(
            int[] points, int[] holes, int bodySlots, int bodyMultiplier, int capacity)
        {
            (Armor[] Members, DecorationSolver.Solution Solution, int MaxDefense, int FreeSlots, int Defense)? best = null;
            var maxRemaining = new int[ArmorParts.Count + 1];
            for (var part = ArmorParts.Count - 1; part >= 0; part--)
                maxRemaining[part] = maxRemaining[part + 1] + chosen[part].MaxDefense;

            void Walk(int part, int maxDefense, int defense)
            {
                var upperBound = maxDefense + maxRemaining[part];
                if (best != null && upperBound < best.Value.MaxDefense) return;
                if (worstResult != null && upperBound < worstResult.MaxDefense) return;
                if (part == ArmorParts.Count)
                {
                    var negativeBase = NegativeBase(chosenMembers, bodyMultiplier);
                    var solution = solver.Solve(points, holes, bodySlots, bodyMultiplier,
                        s => !ActivatesNegative(negativeBase, s, bodyMultiplier));
                    if (solver.WasTruncated) truncatedSolves++;
                    if (solution == null) return;
                    var freeSlots = FreeSlots(capacity, solution);
                    if (best == null || CompareRanks(maxDefense, freeSlots, defense,
                            best.Value.MaxDefense, best.Value.FreeSlots, best.Value.Defense) > 0)
                        best = ((Armor[])chosenMembers.Clone(), solution, maxDefense, freeSlots, defense);
                    return;
                }
                foreach (var member in chosen[part].Members)
                {
                    chosenMembers[part] = member;
                    Walk(part + 1, maxDefense + member.MaxDefense, defense + member.Defense);
                }
            }

            Walk(0, 0, 0);
            return best == null ? null : (best.Value.Members, best.Value.Solution);
        }

        /// <summary>防具 (胴系統倍化の複製込み) とお守りによる、負のスキルを持つ系統のポイント合計。</summary>
        private int[] NegativeBase(Armor[] members, int bodyMultiplier)
        {
            var totals = (int[])charmNegativePoints.Clone();
            var body = members[ArmorParts.Body];
            foreach (var armor in members)
            {
                if (armor.IsTorsoUp) continue;
                var weight = armor == body ? bodyMultiplier : 1;
                var armorPoints = armorNegativePoints[armor];
                for (var i = 0; i < totals.Length; i++) totals[i] += armorPoints[i] * weight;
            }
            return totals;
        }

        private bool ActivatesNegative(int[] negativeBase, DecorationSolver.Solution solution, int bodyMultiplier)
        {
            var totals = (int[])negativeBase.Clone();
            foreach (var decoration in solution.BodyDecorations)
            {
                var decorationPoints = decorationNegativePoints[decoration];
                for (var i = 0; i < totals.Length; i++) totals[i] += decorationPoints[i] * bodyMultiplier;
            }
            foreach (var decoration in solution.OtherDecorations)
            {
                var decorationPoints = decorationNegativePoints[decoration];
                for (var i = 0; i < totals.Length; i++) totals[i] += decorationPoints[i];
            }
            for (var i = 0; i < totals.Length; i++)
                if (totals[i] <= negativeThresholds[i]) return true;
            return false;
        }

        private static void AddHole(int[] holes, List<(int Size, string Label)> labels, int size, string label)
        {
            if (size <= 0) return;
            holes[size]++;
            labels.Add((size, label));
        }

        private SearchResult BuildResult(Armor[] armors, DecorationSolver.Solution solution,
            List<(int Size, string Label)> holeLabels, int bodyMultiplier)
        {
            var body = armors[ArmorParts.Body];
            var totals = new Dictionary<string, int>();
            void Add(string system, int points) => totals[system] = totals.GetValueOrDefault(system) + points;

            foreach (var armor in armors.Where(a => !a.IsTorsoUp))
                foreach (var (system, points) in armor.Skills) Add(system, points);
            foreach (var (system, points) in body.Skills) Add(system, points * (bodyMultiplier - 1));
            foreach (var (system, points) in currentCharm.Skills()) Add(system, points);
            foreach (var deco in solution.BodyDecorations)
                foreach (var (system, points) in deco.Skills) Add(system, points * bodyMultiplier);
            foreach (var deco in solution.OtherDecorations)
                foreach (var (system, points) in deco.Skills) Add(system, points);

            var placed = solution.BodyDecorations.Select(d => new PlacedDecoration(d, "胴")).ToList();
            var remaining = holeLabels.Select(h => (h.Label, Capacity: h.Size)).ToList();
            foreach (var deco in solution.OtherDecorations.OrderByDescending(d => d.Slots))
            {
                // 最も残り容量が小さく収まる穴に入れる (best-fit)
                var index = remaining.Select((h, i) => (h, i)).Where(x => x.h.Capacity >= deco.Slots)
                    .OrderBy(x => x.h.Capacity).First().i;
                placed.Add(new PlacedDecoration(deco, remaining[index].Label));
                remaining[index] = (remaining[index].Label, remaining[index].Capacity - deco.Slots);
            }
            var free = remaining.Where(h => h.Capacity > 0).Select(h => $"{h.Label}{h.Capacity}").ToList();
            var bodyFree = body.Slots - solution.BodyDecorations.Sum(d => d.Slots);
            if (bodyMultiplier > 1 && bodyFree > 0) free.Add($"胴{bodyFree}");

            return new SearchResult
            {
                Armors = armors,
                Equivalents = chosen.Select((c, part) => (IReadOnlyList<Armor>)c.Members.Concat(c.Covered)
                    .Where(a => a != armors[part]).OrderByDescending(a => a.MaxDefense).ThenByDescending(a => a.Defense).ToList()).ToArray(),
                Charm = currentCharm,
                Decorations = placed,
                SkillTotals = totals,
                ActiveSkills = data.ResolveActiveSkills(totals),
                FreeSlots = free,
                FreeSlotTotal = remaining.Sum(h => h.Capacity) + (bodyMultiplier > 1 ? bodyFree : 0),
            };
        }

        private static readonly Comparer<SearchResult> ResultComparer = Comparer<SearchResult>.Create(CompareResults);

        private static int CharmPoints(Charm charm, string system) =>
            charm.Skills().Where(s => s.Key == system).Sum(s => s.Value);
    }
}
