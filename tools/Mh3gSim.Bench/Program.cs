using System.Diagnostics;
using Mh3gSim.Core;

// 検索ロジックの速度・結果を確認するためのベンチ。引数: 系統:ポイント ... [--gunner] [--rare N] [--weapon N]
Console.OutputEncoding = System.Text.Encoding.UTF8;
GameData data;
try { data = GameData.LoadEmbedded(); }
catch (DataLoadException exception)
{
    Console.WriteLine("check-data: NG (読み込み失敗)\n  " + exception.Message);
    return 1;
}
var requirements = args.Where(a => a.Contains(':') && !a.StartsWith("charm=") && !File.Exists(a)).Select(a =>
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
    return 0;
}
if (args.Contains("--check-data"))
{
    // データファイル (src/Mh3gSim.Core/Data/*.json) を手で直した後の整合チェック。NG なら終了コード 1
    var problems = DataValidator.Validate(data);
    var charmModel = data.Charms;
    Console.WriteLine($"防具 {data.Armors.Count} / 装飾品 {data.Decorations.Count} / スキル系統 {data.SkillSystems.Count} / お守りの種類 {charmModel.Kinds.Count} / テーブル {charmModel.Tables.Count}");
    foreach (var kind in charmModel.Kinds)
        Console.WriteLine($"  {kind.Name}: 第 1 スキル {kind.FirstSkills.Count} / 第 2 スキル {kind.SecondSkills.Count} / スロット 0-{CharmLimits.MaxSlotsOf(kind)} / {string.Join("・", kind.Names.Select(n => n.Name))}");
    // 値の範囲が正しければ、実際に全テーブルのお守りを作れることも確かめる
    if (problems.Count == 0) Console.WriteLine($"  実在するお守り {CharmGenerator.GenerateAll(charmModel).Count} 種類");
    Console.WriteLine(problems.Count == 0 ? "check-data: OK" : "check-data: NG\n  " + string.Join("\n  ", problems));
    return problems.Count == 0 ? 0 : 1;
}
if (args.Contains("--charm-model-test"))
{
    // お守りの作り方の回帰テスト: 全テーブルで作ったお守りの数 (合計・テーブルごと) と、決まった位置のお守りを固定値と比べる。
    // 続けて CSV (種類,スキル1,ポイント1,スキル2,ポイント2,スロット,テーブル[空白区切り]) を渡すと、全件を 1 件ずつ突き合わせる
    var modelWatch = Stopwatch.StartNew();
    var entries = CharmGenerator.GenerateAll(data.Charms);
    var perTable = data.Charms.Tables.Select(t => entries.Count(e => e.AppearsOn(t.Table))).ToArray();
    Console.WriteLine($"実在するお守り {entries.Count} 種類 ({modelWatch.ElapsedMilliseconds} ms)");
    Console.WriteLine("  テーブルごと: " + string.Join(" ", perTable.Select((count, i) => $"T{i + 1}:{count}")));
    int[] expectedPerTable = [12911, 12891, 12745, 12987, 12843, 13049, 12843, 12957, 12851, 12762, 768, 212, 12956, 12936, 778, 211, 213];
    var first = CharmGenerator.Generate(data.Charms, data.Charms.Kinds[0], data.Charms.Tables[0].Seed);
    var modelFailures = new List<string>();
    if (entries.Count != 121952) modelFailures.Add($"合計 {entries.Count} (期待 121952)");
    if (!perTable.SequenceEqual(expectedPerTable)) modelFailures.Add("テーブルごとの数が期待値と違う");
    if ($"{first.Name} {first.ToCharm()}" != "闘士の護石 采配+2 [○－－]") modelFailures.Add($"T1 なぞの 1 番目が {first.Name} {first.ToCharm()} (期待 闘士の護石 采配+2 [○－－])");

    var csvArgument = Array.IndexOf(args, "--charm-model-test") + 1;
    if (csvArgument < args.Length && File.Exists(args[csvArgument]))
    {
        string Line(string kind, string s1, int p1, string s2, int p2, int slots, IEnumerable<int> tables) =>
            $"{kind},{s1},{p1},{s2},{(s2.Length == 0 ? "" : p2)},{slots},{string.Join(" ", tables)}";
        var generated = entries.Select(e => Line(e.Kind, e.Skill1, e.Points1, e.Skill2, e.Points2, e.Slots,
            data.Charms.Tables.Select(t => t.Table).Where(e.AppearsOn))).ToHashSet();
        var reference = File.ReadLines(args[csvArgument]).Skip(1).Select(l => l.TrimStart('﻿')).ToHashSet();
        var onlyGenerated = generated.Except(reference).ToList();
        var onlyReference = reference.Except(generated).ToList();
        Console.WriteLine($"  CSV 突き合わせ: 参照 {reference.Count} 行 / 作ったものだけ {onlyGenerated.Count} / 参照だけ {onlyReference.Count}");
        foreach (var line in onlyGenerated.Take(5)) Console.WriteLine($"    作ったものだけ: {line}");
        foreach (var line in onlyReference.Take(5)) Console.WriteLine($"    参照だけ: {line}");
        if (onlyGenerated.Count + onlyReference.Count > 0) modelFailures.Add("CSV と一致しない");
    }
    Console.WriteLine(modelFailures.Count == 0 ? "charm-model-test: OK" : "charm-model-test: NG\n  " + string.Join("\n  ", modelFailures));
    return modelFailures.Count == 0 ? 0 : 1;
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
    return 0;
}
if (args.Contains("--find-charm"))
{
    // お守りの自動計算 (--table N でそのテーブルのお守りだけ)。--verify を付けると、候補のお守りを全部 1 つずつ検索した結果 (総当たり) と最小限のお守りの集合を突き合わせる
    int? table = Option("--table", 0) is var number and > 0 ? number : null;
    var catalog = new CharmCatalog(data.Charms);
    var catalogWatch = Stopwatch.StartNew();
    var candidates = catalog.RequirementsFor(requirements, table);
    var catalogMs = catalogWatch.ElapsedMilliseconds;
    if (args.Contains("--cancel-test"))
    {
        // 途中で中止した時に OperationCanceledException になること (AggregateException で包まれてエラー表示にならないこと)
        var cancelResults = new List<string>();
        foreach (var delay in new[] { 0, 5, 20, 60 })
        {
            using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(delay));
            try
            {
                new CharmFinder(new SkillSearcher(data)).Find(condition, candidates, null, source.Token);
                cancelResults.Add($"{delay}ms: 完了");
            }
            catch (OperationCanceledException) { cancelResults.Add($"{delay}ms: 中止"); }
            catch (Exception exception) { cancelResults.Add($"{delay}ms: NG {exception.GetType().Name}"); }
        }
        Console.WriteLine("cancel-test: " + string.Join(" / ", cancelResults));
        return cancelResults.Any(r => r.Contains("NG")) ? 1 : 0;
    }
    var finderWatch = Stopwatch.StartNew();
    // --direct: 打ち切りが起きた時の道 (推論せず候補を全部検索) を通して、結果が同じになるかを確かめる
    var found = new CharmFinder(new SkillSearcher(data)) { ForceDirectProbing = args.Contains("--direct") }
        .Find(condition, candidates, null, CancellationToken.None);
    Console.WriteLine($"候補 {candidates.Count} ({catalogMs} ms) / 必要なお守り {found.Suggestions.Count} 通り / {finderWatch.ElapsedMilliseconds} ms / 珠探索の打ち切り {found.TruncatedSolves}");
    foreach (var s in found.Suggestions.Take(Option("--show", 20)))
    {
        var availability = catalog.Describe(s.Requirement, requirements, table, 1);
        Console.WriteLine($"  {ResultTextFormatter.ShortRequirement(s.Requirement, requirements)}  ({string.Join("・", availability.Kinds)} / テーブル {ResultTextFormatter.FormatTables(availability.Tables, catalog.TableNumbers)} / {availability.Count} 種類"
            + $"{(availability.Examples.Count > 0 ? $" / 例 {availability.Examples[0]}" : "")})  防御 {s.Best.Defense}->{s.Best.MaxDefense}: {string.Join(" / ", s.Best.Armors.Select(a => a.Name))}");
    }
    if (args.Contains("--export"))
    {
        // テキスト保存と同じ書式 (見出し + 結果ごとの必要なお守り) を先頭だけ表示する
        var suggestionsForExport = found.Suggestions.Select(s => s with { Availability = catalog.Describe(s.Requirement, requirements, table, 3) }).ToList();
        var exportText = ResultTextFormatter.FormatExport(condition, [.. suggestionsForExport.Select(s => s.Best)], "export-test",
            new ResultTextFormatter.CharmSearchContext(suggestionsForExport, table, catalog.TableNumbers));
        Console.WriteLine(string.Join("\n", exportText.Split('\n').Take(Option("--export-lines", 24))));
    }
    if (!args.Contains("--verify")) return 0;

    var bruteWatch = Stopwatch.StartNew();
    var feasible = new System.Collections.Concurrent.ConcurrentBag<CharmRequirement>();
    var searcherForBrute = new SkillSearcher(data);
    var probeCondition = condition.WithCharms([.. candidates.Select(c => c.ToCharm(requirements))]);
    Parallel.ForEach(candidates, () => searcherForBrute.CreateProbe(probeCondition, CancellationToken.None),
        (candidate, _, probe) => { if (probe.FindBest(candidate.ToCharm(requirements)) != null) feasible.Add(candidate); return probe; },
        _ => { });
    var feasibleList = feasible.ToList();
    var trueMinimal = feasibleList.Where(c => !feasibleList.Any(o => o != c && c.IsAtLeast(o.Points, o.Slots) && !o.IsAtLeast(c.Points, c.Slots))).ToList();
    string Key(CharmRequirement c) => $"{string.Join(",", c.Points)}|{c.Slots}";
    var expected = trueMinimal.Select(Key).ToHashSet();
    var actual = found.Suggestions.Select(s => Key(s.Requirement)).ToHashSet();
    Console.WriteLine($"総当たり: 成立 {feasibleList.Count} / 最小 {trueMinimal.Count} ({bruteWatch.ElapsedMilliseconds} ms)");
    Console.WriteLine(expected.SetEquals(actual)
        ? "verify: OK (自動計算の結果と総当たりの最小集合が一致)"
        : $"verify: NG  総当たりだけ: {string.Join(" ", expected.Except(actual))}  自動計算だけ: {string.Join(" ", actual.Except(expected))}");

    // 出したお守りの条件は、どれも実在するお守り (テーブル指定ならそのテーブル) の中にちょうど同じものがあること
    var realKeys = catalog.Entries.Where(e => table is not { } only || e.AppearsOn(only))
        .Select(e => requirements.Select(r => (e.Skill1 == r.System ? e.Points1 : 0) + (e.Skill2 == r.System ? e.Points2 : 0)).ToArray())
        .Zip(catalog.Entries.Where(e => table is not { } only || e.AppearsOn(only)), (points, e) => $"{string.Join(",", points)}|{e.Slots}")
        .ToHashSet();
    var unreal = found.Suggestions.Where(s => !realKeys.Contains(Key(s.Requirement))).ToList();
    Console.WriteLine(unreal.Count == 0
        ? "verify: OK (出したお守りの条件はすべて実在するお守りでちょうど作れる)"
        : $"verify: NG 実在しない条件 {string.Join(" ", unreal.Select(s => Key(s.Requirement)))}");
    return expected.SetEquals(actual) && unreal.Count == 0 ? 0 : 1;
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
return 0;
