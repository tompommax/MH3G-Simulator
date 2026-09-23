using System.Diagnostics;
using Mh3gSim.Core;

// 検索ロジックの速度・結果を確認するためのベンチ。引数: 系統:ポイント ... [--gunner] [--rare N] [--weapon N]
Console.OutputEncoding = System.Text.Encoding.UTF8;
var data = GameData.LoadEmbedded();
var requirements = args.Where(a => a.Contains(':') && !a.StartsWith("charm=")).Select(a =>
{
    var pieces = a.Split(':');
    return new SkillRequirement(pieces[0], int.Parse(pieces[1]), pieces[0]);
}).ToList();
int Option(string name, int fallback) =>
    Array.IndexOf(args, name) is var i and >= 0 ? int.Parse(args[i + 1]) : fallback;

var condition = new SearchCondition
{
    Requirements = requirements,
    IsGunner = args.Contains("--gunner"),
    MaxRarity = Option("--rare", 10),
    WeaponSlots = Option("--weapon", 0),
    AvoidNegativeSkills = !args.Contains("--allow-neg"),
    Charms = args.Where(a => a.StartsWith("charm=")).Select(a =>
    {
        var p = a[6..].Split(':');
        return new Charm { Skill1 = p[0], Points1 = int.Parse(p[1]), Skill2 = p[2], Points2 = int.Parse(p[3]), Slots = int.Parse(p[4]) };
    }).DefaultIfEmpty(Charm.None).ToList(),
};
if (args.Contains("--solver-test"))
{
    // 副スキルが互いにマイナスの珠 (匠珠 / 斬鉄珠) を両方使わないと満たせないケース
    // 匠 8 / 斬れ味 8 から 匠 10 / 斬れ味 10 へ: 匠珠【３】x3 + 斬鉄珠【３】x2 (15 スロット) で成立する
    var crossSolver = new DecorationSolver(["匠", "斬れ味"], [10, 10], data.Decorations);
    var crossSolution = crossSolver.Solve([8, 8], [0, 0, 0, 5], 0, 1, _ => true);
    Console.WriteLine(crossSolution == null
        ? "solver-test: NOT FOUND (false negative)"
        : "solver-test: found " + string.Join(", ", crossSolution.OtherDecorations.Select(d => d.Name)));

    // Codex レビューの例: 匠 0 / 斬れ味 0 → 1 / 1、3 穴 x4。匠珠【２】x3 + 斬鉄珠【３】x1 (9 スロット) で成立
    var codexSolver = new DecorationSolver(["匠", "斬れ味"], [1, 1], data.Decorations);
    var codexSolution = codexSolver.Solve([0, 0], [0, 0, 0, 4], 0, 1, _ => true);
    Console.WriteLine(codexSolution == null
        ? "solver-test2: NOT FOUND (false negative)"
        : "solver-test2: found " + string.Join(", ", codexSolution.OtherDecorations.Select(d => d.Name)));

    // 穴に収まらない時は見つからないこと (偽陽性の確認): 同条件で 3 穴 x2 (6 スロット) では不成立
    var tightSolution = codexSolver.Solve([0, 0], [0, 0, 0, 2], 0, 1, _ => true);
    Console.WriteLine(tightSolution == null ? "solver-test3: correctly not found" : "solver-test3: FALSE POSITIVE");
    return;
}
if (args.Contains("--check-data"))
{
    // お守り表の整合チェック: スキル名がスキル系統に存在する / スロット上限・第 2 スキル下限が妥当 / 種類名が重複しない
    var systemNames = data.SkillSystems.Select(s => s.System).ToHashSet();
    var problems = new List<string>();
    foreach (var category in data.CharmCategories)
    {
        problems.AddRange(category.Skills.Keys.Where(k => !systemNames.Contains(k)).Select(k => $"{category.Name}: 未知のスキル {k}"));
        problems.AddRange(category.Skills.Where(kv => kv.Value is < 1 or > 20).Select(kv => $"{category.Name}: {kv.Key} の最大値が範囲外 {kv.Value}"));
        if (category.MaxSlots is < 0 or > 3) problems.Add($"{category.Name}: スロット上限 {category.MaxSlots}");
        if (category.SecondSkill ? category.SecondSkillMin >= 0 : category.SecondSkillMin != 0) problems.Add($"{category.Name}: 第 2 スキル下限 {category.SecondSkillMin}");
        if (category.Types.Count == 0) problems.Add($"{category.Name}: 種類が無い");
        Console.WriteLine($"{category.Name}: 種類 {string.Join("/", category.Types)} / スキル {category.Skills.Count} / スロット 0-{category.MaxSlots} / 第2スキル {(category.SecondSkill ? $"あり (下限 {category.SecondSkillMin})" : "なし")}");
    }
    var duplicateTypes = data.CharmCategories.SelectMany(c => c.Types).GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key);
    problems.AddRange(duplicateTypes.Select(t => $"種類名の重複: {t}"));
    Console.WriteLine(problems.Count == 0 ? "check-data: OK" : "check-data: NG\n  " + string.Join("\n  ", problems));
    return;
}
if (args.Contains("--scan"))
{
    // マイナススキル回避の検証用: 各スキル (発動値 10) 単体について、レア度を絞った母集団で
    // 「回避あり総当たり」と検索結果の最上位を比較する。回避の有無で最上位が変わるケースを特に表示する。
    var rarity = Option("--rare", 1);
    var mismatches = 0;
    var negativeCases = 0;
    foreach (var system in data.SkillSystems.Where(s => s.Activations.Any(a => a.Points == 10)))
    {
        var requirement = new SkillRequirement(system.System, 10, system.System);
        (int, int, int) BruteBest(bool avoid)
        {
            var solver = new DecorationSolver([system.System], [10], data.Decorations);
            var pools = Enumerable.Range(0, 5).Select(p => data.Armors.Where(a => a.Part == p && a.Blade && a.Male && a.Rarity <= rarity).ToList()).ToArray();
            (int, int, int) best = (0, -1, 0);
            var combo = new Armor[5];
            void Walk(int part)
            {
                if (part == 5)
                {
                    var body = combo[1];
                    var mult = 1 + combo.Count(a => a.IsTorsoUp);
                    var pts = new[] { combo.Where(a => !a.IsTorsoUp).Sum(a => a.PointsOf(system.System)) + body.PointsOf(system.System) * (mult - 1) };
                    var holes = new int[4];
                    foreach (var a in combo) if (a != body && !a.IsTorsoUp && a.Slots > 0) holes[a.Slots]++;
                    var solution = solver.Solve(pts, holes, body.Slots, mult, sol =>
                    {
                        if (!avoid) return true;
                        var totals = new Dictionary<string, int>();
                        void Add(string k, int v) => totals[k] = totals.GetValueOrDefault(k) + v;
                        foreach (var armor in combo.Where(a => !a.IsTorsoUp)) foreach (var (k, v) in armor.Skills) Add(k, v);
                        foreach (var (k, v) in body.Skills) Add(k, v * (mult - 1));
                        foreach (var d in sol.BodyDecorations) foreach (var (k, v) in d.Skills) Add(k, v * mult);
                        foreach (var d in sol.OtherDecorations) foreach (var (k, v) in d.Skills) Add(k, v);
                        return data.ResolveActiveSkills(totals).All(x => !x.IsNegative);
                    });
                    if (solution == null) return;
                    var capacity = holes[1] + 2 * holes[2] + 3 * holes[3] + body.Slots;
                    var free = capacity - solution.BodyDecorations.Sum(d => d.Slots) - solution.OtherDecorations.Sum(d => d.Slots);
                    var key = (combo.Sum(a => a.MaxDefense), free, combo.Sum(a => a.Defense));
                    if (key.CompareTo(best) > 0) best = key;
                    return;
                }
                foreach (var a in pools[part]) { combo[part] = a; Walk(part + 1); }
            }
            Walk(0);
            return best;
        }

        var avoidBest = BruteBest(true);
        var allowBest = BruteBest(false);
        var searched = new SkillSearcher(data).Search(new SearchCondition
        {
            Requirements = [requirement], MaxRarity = rarity, Charms = [Charm.None], AvoidNegativeSkills = true,
        }, null, CancellationToken.None).Results.FirstOrDefault();
        var searchedKey = searched == null ? (0, -1, 0) : (searched.MaxDefense, searched.FreeSlotTotal, searched.Defense);
        var differs = avoidBest != allowBest;
        if (differs) negativeCases++;
        if (searchedKey != avoidBest) mismatches++;
        if (differs || searchedKey != avoidBest)
            Console.WriteLine($"{system.System}: brute(avoid) {avoidBest} brute(allow) {allowBest} searcher {searchedKey}{(searchedKey != avoidBest ? "  <-- MISMATCH" : "")}");
    }
    Console.WriteLine($"scan done: negative-affected cases {negativeCases}, mismatches {mismatches}");
    return;
}
var watch = Stopwatch.StartNew();
var outcome = new SkillSearcher(data).Search(condition, null, CancellationToken.None);
var results = outcome.Results;
Console.WriteLine($"{results.Count} 件 / {watch.ElapsedMilliseconds} ms / 珠探索の打ち切り {outcome.TruncatedSolves}");
foreach (var r in results.Take(Option("--show", 3)))
{
    Console.WriteLine($"防御 {r.Defense}->{r.MaxDefense} 守:{r.Charm}: {string.Join(" / ", r.Armors.Select(a => a.Name))}");
    Console.WriteLine("  珠: " + string.Join(", ", r.Decorations.Select(d => $"{d.Decoration.Name}({d.Location})")));
    Console.WriteLine("  発動: " + string.Join(", ", r.ActiveSkills.Select(s => $"{s.Activation.Name}[{s.Total}]")));
    Console.WriteLine("  空き: " + string.Join(",", r.FreeSlots));
}

if (args.Contains("--brute"))
{
    // 枝刈り・候補圧縮の検証用: 全組み合わせを総当たりし、最上位の順位キー (最終防御, 空きスロット, 初期防御) を比較する。
    // マイナススキル回避 (既定) の時は、負のスキルが発動しない装飾品の組み合わせがある構成だけを有効とする。
    var systems = requirements.Select(r => r.System).ToList();
    var targets = requirements.Select(r => r.Points).ToArray();
    var solver = new DecorationSolver(systems, targets, data.Decorations);
    var pools = Enumerable.Range(0, 5).Select(p => data.Armors.Where(a => a.Part == p && a.Blade && a.Male
        && a.Rarity <= condition.MaxRarity).ToList()).ToArray();
    Console.WriteLine("pool: " + string.Join(",", pools.Select(p => p.Count)));
    (int Max, int Free, int Def) best = (0, -1, 0);
    long valid = 0;
    var combo = new Armor[5];

    Dictionary<string, int> Totals(Armor[] armors, int mult, DecorationSolver.Solution solution)
    {
        var totals = new Dictionary<string, int>();
        void Add(string system, int points) => totals[system] = totals.GetValueOrDefault(system) + points;
        foreach (var armor in armors.Where(a => !a.IsTorsoUp))
            foreach (var (system, points) in armor.Skills) Add(system, points);
        foreach (var (system, points) in armors[1].Skills) Add(system, points * (mult - 1));
        foreach (var deco in solution.BodyDecorations)
            foreach (var (system, points) in deco.Skills) Add(system, points * mult);
        foreach (var deco in solution.OtherDecorations)
            foreach (var (system, points) in deco.Skills) Add(system, points);
        return totals;
    }

    void Walk(int part)
    {
        if (part == 5)
        {
            var body = combo[1];
            var mult = 1 + combo.Count(a => a.IsTorsoUp);
            var pts = systems.Select(s => combo.Where(a => !a.IsTorsoUp).Sum(a => a.PointsOf(s)) + body.PointsOf(s) * (mult - 1)).ToArray();
            var holes = new int[4];
            foreach (var a in combo) if (a != body && !a.IsTorsoUp && a.Slots > 0) holes[a.Slots]++;
            if (condition.WeaponSlots > 0) holes[condition.WeaponSlots]++;
            var solution = solver.Solve(pts, holes, body.Slots, mult, s =>
                !condition.AvoidNegativeSkills || data.ResolveActiveSkills(Totals(combo, mult, s)).All(x => !x.IsNegative));
            if (solution == null) return;
            valid++;
            var capacity = holes[1] + 2 * holes[2] + 3 * holes[3] + body.Slots;
            var free = capacity - solution.BodyDecorations.Sum(d => d.Slots) - solution.OtherDecorations.Sum(d => d.Slots);
            var key = (combo.Sum(a => a.MaxDefense), free, combo.Sum(a => a.Defense));
            if (key.CompareTo(best) > 0) best = key;
            return;
        }
        foreach (var a in pools[part]) { combo[part] = a; Walk(part + 1); }
    }
    Walk(0);
    var top = results.FirstOrDefault();
    var searcherKey = top == null ? "none" : $"({top.MaxDefense}, {top.FreeSlotTotal}, {top.Defense})";
    Console.WriteLine($"brute: valid {valid}, best {best}; searcher best {searcherKey}");

    // 検索結果が本当に条件を満たすかを独立に検算する
    var broken = 0;
    foreach (var r in results)
    {
        var totals = r.SkillTotals;
        var meets = requirements.All(q => totals.GetValueOrDefault(q.System) >= q.Points);
        var noNegative = !condition.AvoidNegativeSkills || data.ResolveActiveSkills(totals).All(x => !x.IsNegative);
        if (!meets || !noNegative) broken++;
    }
    Console.WriteLine($"verify: {results.Count - broken}/{results.Count} results satisfy requirements");
}
