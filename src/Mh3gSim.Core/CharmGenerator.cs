namespace Mh3gSim.Core;

/// <summary>ゲームで出るお守り 1 種類 (お守りの種類・スキル・スロットが同じものは同じ) と、それが出るテーブル。</summary>
public sealed class CharmEntry
{
    /// <summary>拾った時のお守りの名前 (なぞのお守り 等)。</summary>
    public required string Kind { get; init; }
    /// <summary>護石の名前 (龍の護石 等)。</summary>
    public required string Name { get; init; }
    public required string Skill1 { get; init; }
    public required int Points1 { get; init; }
    /// <summary>第 2 スキル (付いていなければ空)。</summary>
    public required string Skill2 { get; init; }
    public required int Points2 { get; init; }
    public required int Slots { get; init; }
    /// <summary>出るテーブルのビット (テーブル t なら 1 &lt;&lt; (t - 1))。</summary>
    public int TableMask { get; internal set; }

    public bool AppearsOn(int table) => (TableMask & TableBit(table)) != 0;

    public Charm ToCharm() => new() { Skill1 = Skill1, Points1 = Points1, Skill2 = Skill2, Points2 = Points2, Slots = Slots };

    public bool SameContent(Charm charm) => Skill1 == charm.Skill1 && Points1 == charm.Points1
        && Skill2 == charm.Skill2 && Points2 == charm.Points2 && Slots == charm.Slots;

    /// <summary>「古びたお守り 龍の護石 攻撃+6 聴覚保護-3 [○－－]」</summary>
    public override string ToString() => $"{Kind} {Name} {ToCharm()}";

    internal static int TableBit(int table) => 1 << (table - 1);
}

/// <summary>
/// お守りの作り方 (MH3G): テーブルの乱数を 1 回ずつ進めながら、第 1 スキル → そのポイント → 第 2 スキルが付くか
/// → (付くなら) 第 2 スキル → プラスかマイナスか → そのポイント → スロット数 の順に決める。
/// 第 2 スキルが第 1 スキルと同じか 0 ポイントなら付かない (乱数は進んだまま)。
/// スロット数は「スロット値」(第 1 スキルのポイント × 10 ÷ 表の最大値 + 第 2 スキルがプラスなら同様。足してから切り捨て) の行の判定値と、
/// 乱数 % 100 を比べて決める。護石の名前は スロット値 + 2 × スロット数 で決まる。
/// </summary>
public static class CharmGenerator
{
    /// <summary>全テーブル・全種類について、乱数の周期の各位置から出るお守りを作り、同じお守りをまとめて返す。</summary>
    public static List<CharmEntry> GenerateAll(CharmModel model)
    {
        var entries = new Dictionary<(int Kind, string Skill1, int Points1, string Skill2, int Points2, int Slots), CharmEntry>();
        foreach (var table in model.Tables)
        {
            var bit = CharmEntry.TableBit(table.Table);
            var state = table.Seed;
            var steps = 0;
            do
            {
                for (var kind = 0; kind < model.Kinds.Count; kind++)
                {
                    var charm = Generate(model, model.Kinds[kind], state);
                    var key = (kind, charm.Skill1, charm.Points1, charm.Skill2, charm.Points2, charm.Slots);
                    if (!entries.TryGetValue(key, out var entry)) entries[key] = entry = charm;
                    entry.TableMask |= bit;
                }
                state = Next(model, state);
                // 乗数と法が互いに素なら必ず初期値に戻る (DataValidator で確認)。壊れたデータで止まらなくならないように
                if (++steps > model.Modulus) throw new InvalidOperationException($"テーブル {table.Table} の乱数が初期値に戻りません");
            } while (state != table.Seed);
        }
        return [.. entries.Values];
    }

    /// <summary>乱数が state の時に拾ったお守りから出る護石。</summary>
    public static CharmEntry Generate(CharmModel model, CharmKind kind, int state)
    {
        state = Next(model, state);
        var first = kind.FirstSkills[state % kind.FirstSkills.Count];
        state = Next(model, state);
        var points1 = first.Min + state % (first.Max - first.Min + 1);

        state = Next(model, state);
        CharmSkillRange? second = null;
        var points2 = 0;
        if (kind.SecondSkills.Count > 0 && state % 100 >= kind.SecondSkillThreshold)
        {
            state = Next(model, state);
            second = kind.SecondSkills[state % kind.SecondSkills.Count];
            state = Next(model, state);
            var positive = state % 2 == 1;
            state = Next(model, state);
            points2 = positive ? 1 + state % second.Max : second.Min + state % (1 - second.Min);
            if (second.Skill == first.Skill || points2 == 0)
            {
                second = null;
                points2 = 0;
            }
        }

        // マイナスの第 2 スキルはスロット値に数えない。小数のまま足してから切り捨てる
        var slotValue = (int)Math.Floor(points1 * 10.0 / first.Max + (points2 > 0 ? points2 * 10.0 / second!.Max : 0));
        state = Next(model, state);
        var slots = SlotCount(kind, slotValue, state % 100);
        return new CharmEntry
        {
            Kind = kind.Name,
            Name = NameOf(kind, slotValue + 2 * slots),
            Skill1 = first.Skill,
            Points1 = points1,
            Skill2 = second?.Skill ?? "",
            Points2 = points2,
            Slots = slots,
        };
    }

    private static int SlotCount(CharmKind kind, int slotValue, int roll)
    {
        var row = kind.SlotRows[Math.Clamp(slotValue, 1, kind.SlotRows.Count) - 1];
        if (roll >= row[1]) return roll >= row[2] ? 3 : 2;
        return roll >= row[0] ? 1 : 0;
    }

    private static string NameOf(CharmKind kind, int score) =>
        kind.Names.FirstOrDefault(n => n.MaxScore == null || score <= n.MaxScore)?.Name ?? "";

    private static int Next(CharmModel model, int state) => (int)((long)model.Multiplier * state % model.Modulus);
}
