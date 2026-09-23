using Mh3gSim.Core;

namespace Mh3gSim;

/// <summary>
/// 装備するお守り 1 つの入力欄 (プルダウン)。お守りは 1 つしか装備できないので、入力欄の内容がそのまま装備中のお守り。
/// スキル1 を「（なし）」にするとお守りなし。
/// スキルはどれかのお守りに付き得るものだけ、ポイントはそのスキルの最大値まで、スロットは上限までを選べる。
/// </summary>
internal sealed class CharmEditor : UserControl
{
    private const string NoSkill = "（なし）";

    private readonly CharmLimits limits;
    private readonly List<string> firstSkills;
    private readonly List<string> secondSkills;
    /// <summary>プログラムから値を入れている間は変更通知 (保存) を出さない。</summary>
    private bool suppressChanged;

    private readonly ComboBox skill1Box = Combo(150);
    private readonly ComboBox points1Box = Combo(55);
    private readonly ComboBox slotsBox = Combo(55);
    private readonly ComboBox skill2Box = Combo(150);
    private readonly ComboBox points2Box = Combo(55);

    public CharmEditor(GameData data)
    {
        limits = CharmLimits.From(data.CharmCategories);
        // 並びはスキル一覧 (skills.json) の順
        firstSkills = data.SkillSystems.Select(s => s.System).Where(limits.FirstSkillMax.ContainsKey).ToList();
        secondSkills = data.SkillSystems.Select(s => s.System).Where(limits.SecondSkillMax.ContainsKey).ToList();

        BuildLayout();
        skill1Box.SelectedIndexChanged += (_, _) => { OnSkill1Changed(); NotifyChanged(); };
        skill2Box.SelectedIndexChanged += (_, _) => { OnSkill2Changed(); NotifyChanged(); };
        points1Box.SelectedIndexChanged += (_, _) => NotifyChanged();
        points2Box.SelectedIndexChanged += (_, _) => NotifyChanged();
        slotsBox.SelectedIndexChanged += (_, _) => NotifyChanged();

        suppressChanged = true;
        Fill(skill1Box, firstSkills.Prepend(NoSkill).Cast<object>());
        suppressChanged = false;
    }

    /// <summary>入力が変わった時に発生する (保存のきっかけ)。</summary>
    public event EventHandler? CharmChanged;

    /// <summary>装備中のお守り。スキル1 が「（なし）」なら Charm.None。</summary>
    public Charm Charm
    {
        get
        {
            if (skill1Box.SelectedItem is not string skill1 || skill1 == NoSkill || points1Box.SelectedItem is not PointItem points1)
                return Charm.None;
            var skill2 = skill2Box.SelectedItem as string;
            var hasSkill2 = skill2 != null && skill2 != NoSkill && points2Box.SelectedItem is PointItem;
            return new Charm
            {
                Skill1 = skill1,
                Points1 = points1.Value,
                Skill2 = hasSkill2 ? skill2! : "",
                Points2 = hasSkill2 ? ((PointItem)points2Box.SelectedItem!).Value : 0,
                Slots = slotsBox.SelectedItem is int slots ? slots : 0,
            };
        }
    }

    /// <summary>保存済みのお守りを入力欄に入れる。選択肢に無い値 (データを直した後など) は近い値に寄せる。</summary>
    public void SetCharm(Charm charm)
    {
        suppressChanged = true;
        try
        {
            SelectItem(skill1Box, string.IsNullOrEmpty(charm.Skill1) ? NoSkill : charm.Skill1);
            SelectPoint(points1Box, charm.Points1);
            SelectItem(slotsBox, charm.Slots);
            SelectItem(skill2Box, string.IsNullOrEmpty(charm.Skill2) ? NoSkill : charm.Skill2);
            SelectPoint(points2Box, charm.Points2);
        }
        finally
        {
            suppressChanged = false;
        }
    }

    private sealed record PointItem(int Value)
    {
        public override string ToString() => Value.ToString("+0;-0");
    }

    // ───────── 画面 ─────────

    private void BuildLayout()
    {
        var inputs = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        inputs.Controls.Add(Row(Caption("スキル1"), skill1Box, points1Box, Caption("スロット"), slotsBox));
        inputs.Controls.Add(Row(Caption("スキル2"), skill2Box, points2Box));
        Controls.Add(inputs);
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0), Margin = new Padding(3, 0, 0, 0) };

    private static ComboBox Combo(int width) =>
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, MaxDropDownItems = 20 };

    // ───────── 連動 ─────────

    private void OnSkill1Changed()
    {
        var skill1 = skill1Box.SelectedItem as string;
        var hasCharm = skill1 != null && skill1 != NoSkill;
        points1Box.Enabled = slotsBox.Enabled = skill2Box.Enabled = hasCharm;

        var maximum = hasCharm ? limits.FirstSkillMax[skill1!] : 0;
        Fill(points1Box, Enumerable.Range(1, maximum).Reverse().Select(v => (object)new PointItem(v)));
        Fill(slotsBox, Enumerable.Range(0, hasCharm ? limits.MaxSlots + 1 : 1).Cast<object>(), slotsBox.SelectedIndex);

        // 第 2 スキルは第 1 スキル以外から選ぶ。選択中のものは可能なら残す
        var previous = skill2Box.SelectedItem as string;
        var options = hasCharm ? secondSkills.Where(s => s != skill1).Prepend(NoSkill).ToList() : [NoSkill];
        Fill(skill2Box, options.Cast<object>(), options.IndexOf(previous ?? NoSkill));
        OnSkill2Changed();
    }

    private void OnSkill2Changed()
    {
        var skill2 = skill2Box.SelectedItem as string;
        var hasSkill2 = skill2Box.Enabled && skill2 != null && skill2 != NoSkill;
        points2Box.Enabled = hasSkill2;
        var values = hasSkill2
            ? Enumerable.Range(1, limits.SecondSkillMax[skill2!]).Reverse()
                .Concat(Enumerable.Range(1, -limits.SecondSkillMin).Select(v => -v))
            : [];
        Fill(points2Box, values.Select(v => (object)new PointItem(v)));
    }

    private void NotifyChanged()
    {
        if (!suppressChanged) CharmChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Fill(ComboBox box, IEnumerable<object> items, int selectedIndex = 0)
    {
        var wasSuppressed = suppressChanged;
        suppressChanged = true;
        box.BeginUpdate();
        box.Items.Clear();
        box.Items.AddRange(items.ToArray());
        box.EndUpdate();
        if (box.Items.Count > 0) box.SelectedIndex = Math.Clamp(selectedIndex, 0, box.Items.Count - 1);
        suppressChanged = wasSuppressed;
    }

    private static void SelectItem(ComboBox box, object value)
    {
        var index = box.Items.IndexOf(value);
        if (index >= 0) box.SelectedIndex = index;
    }

    private static void SelectPoint(ComboBox box, int value)
    {
        // 最大値を超える値 (データを小さく直した時など) は選べる中で一番近い値にする
        var points = box.Items.Cast<PointItem>().ToList();
        if (points.Count == 0) return;
        var nearest = points.MinBy(p => Math.Abs(p.Value - value))!;
        box.SelectedIndex = points.IndexOf(nearest);
    }
}
