using Mh3gSim.Core;

namespace Mh3gSim;

/// <summary>
/// 所持お守りの入力欄 (プルダウン) と一覧。
/// 種類を選ぶと、その系統に付き得るスキル・ポイント・スロットだけを選べる。
/// 表に無いお守り用に「種類を指定しない」(全スキル・広い範囲) も残す。
/// </summary>
internal sealed class CharmEditor : UserControl
{
    private const string UnspecifiedType = "（種類を指定しない）";
    private const string UnspecifiedLabel = "（指定なし）";
    private const string NoSecondSkill = "（なし）";
    private const int UnspecifiedMaxPoints = 13;
    private const int UnspecifiedMinPoints = -10;
    private const int UnspecifiedMaxSlots = 3;

    private readonly Dictionary<string, int> skillOrder;
    private readonly List<Charm> charms = [];

    private readonly ComboBox typeBox = Combo(170);
    private readonly ComboBox slotsBox = Combo(55);
    private readonly ComboBox skill1Box = Combo(130);
    private readonly ComboBox points1Box = Combo(55);
    private readonly ComboBox skill2Box = Combo(130);
    private readonly ComboBox points2Box = Combo(55);
    private readonly Button addButton = new() { Text = "追加", Width = 64, Height = 28 };
    private readonly Button removeButton = new() { Text = "選択を削除", Width = 100, Height = 28 };
    private readonly DataGridView grid = new();

    public CharmEditor(GameData data)
    {
        skillOrder = data.SkillSystems.Select((s, i) => (s.System, i)).ToDictionary(x => x.System, x => x.i);
        var unspecified = new CharmCategory
        {
            MaxSlots = UnspecifiedMaxSlots,
            SecondSkill = true,
            SecondSkillMin = UnspecifiedMinPoints,
            Skills = data.SkillSystems.ToDictionary(s => s.System, _ => UnspecifiedMaxPoints),
        };
        typeBox.Items.Add(new TypeItem(UnspecifiedType, "", unspecified));
        foreach (var category in data.CharmCategories)
            foreach (var type in category.Types)
                typeBox.Items.Add(new TypeItem($"{type}（{category.Name}）", type, category));

        BuildLayout();
        typeBox.SelectedIndexChanged += (_, _) => OnTypeChanged();
        skill1Box.SelectedIndexChanged += (_, _) => OnSkill1Changed();
        skill2Box.SelectedIndexChanged += (_, _) => OnSkill2Changed();
        addButton.Click += (_, _) => AddCharm();
        removeButton.Click += (_, _) => RemoveSelected();
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete) return;
            RemoveSelected();
            e.Handled = true;
        };
        typeBox.SelectedIndex = 0;
    }

    /// <summary>一覧の追加・削除で発生する (保存のきっかけ)。</summary>
    public event EventHandler? CharmsChanged;

    public IReadOnlyList<Charm> Charms => charms;

    public void SetCharms(IEnumerable<Charm> items)
    {
        charms.Clear();
        charms.AddRange(items);
        RefreshGrid();
    }

    private sealed record TypeItem(string Label, string Type, CharmCategory Category)
    {
        public override string ToString() => Label;
    }

    private sealed record PointItem(int Value)
    {
        public override string ToString() => Value.ToString("+0;-0");
    }

    private CharmCategory CurrentCategory => ((TypeItem)typeBox.SelectedItem!).Category;

    // ───────── 画面 ─────────

    private void BuildLayout()
    {
        var inputs = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        inputs.Controls.Add(Row(Caption("種類"), typeBox, Caption("スロット"), slotsBox));
        inputs.Controls.Add(Row(Caption("スキル1"), skill1Box, points1Box));
        inputs.Controls.Add(Row(Caption("スキル2"), skill2Box, points2Box, addButton));

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = true;
        grid.RowHeadersVisible = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Add("Type", "種類");
        grid.Columns.Add("Skill1", "スキル1");
        grid.Columns.Add("Points1", "pt");
        grid.Columns.Add("Skill2", "スキル2");
        grid.Columns.Add("Points2", "pt");
        grid.Columns.Add("Slots", "ｽﾛｯﾄ");
        grid.Columns["Type"]!.FillWeight = 120;
        foreach (var name in new[] { "Points1", "Points2", "Slots" }) grid.Columns[name]!.FillWeight = 40;

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        bottom.Controls.Add(removeButton);

        Controls.Add(grid);
        Controls.Add(bottom);
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

    private void OnTypeChanged()
    {
        var category = CurrentCategory;
        Fill(skill1Box, OrderedSkills(category).Cast<object>());
        Fill(slotsBox, Enumerable.Range(0, category.MaxSlots + 1).Cast<object>());
        skill2Box.Enabled = category.SecondSkill;
        OnSkill1Changed();
    }

    private void OnSkill1Changed()
    {
        var category = CurrentCategory;
        var skill1 = skill1Box.SelectedItem as string;
        var maximum = skill1 == null ? 0 : category.Skills[skill1];
        Fill(points1Box, Enumerable.Range(1, maximum).Reverse().Select(v => (object)new PointItem(v)));

        // 第 2 スキルは第 1 スキル以外から選ぶ。選択中のものは可能なら残す
        var previous = skill2Box.SelectedItem as string;
        var options = category.SecondSkill
            ? OrderedSkills(category).Where(s => s != skill1).Prepend(NoSecondSkill).ToList()
            : [NoSecondSkill];
        Fill(skill2Box, options.Cast<object>(), options.IndexOf(previous ?? NoSecondSkill));
        OnSkill2Changed();
    }

    private void OnSkill2Changed()
    {
        var category = CurrentCategory;
        var skill2 = skill2Box.SelectedItem as string;
        var hasSkill2 = skill2 != null && skill2 != NoSecondSkill;
        points2Box.Enabled = hasSkill2;
        var values = hasSkill2
            ? Enumerable.Range(1, category.Skills[skill2!]).Reverse()
                .Concat(Enumerable.Range(1, -category.SecondSkillMin).Select(v => -v))
            : [];
        Fill(points2Box, values.Select(v => (object)new PointItem(v)));
    }

    private IEnumerable<string> OrderedSkills(CharmCategory category) =>
        category.Skills.Keys.OrderBy(s => skillOrder.GetValueOrDefault(s, int.MaxValue));

    private static void Fill(ComboBox box, IEnumerable<object> items, int selectedIndex = 0)
    {
        box.BeginUpdate();
        box.Items.Clear();
        box.Items.AddRange(items.ToArray());
        box.EndUpdate();
        if (box.Items.Count > 0) box.SelectedIndex = Math.Clamp(selectedIndex, 0, box.Items.Count - 1);
    }

    // ───────── 追加・削除 ─────────

    private void AddCharm()
    {
        if (skill1Box.SelectedItem is not string skill1 || points1Box.SelectedItem is not PointItem points1) return;
        var skill2 = skill2Box.SelectedItem as string;
        var hasSkill2 = skill2 != null && skill2 != NoSecondSkill;
        var charm = new Charm
        {
            Type = ((TypeItem)typeBox.SelectedItem!).Type,
            Skill1 = skill1,
            Points1 = points1.Value,
            Skill2 = hasSkill2 ? skill2! : "",
            Points2 = hasSkill2 && points2Box.SelectedItem is PointItem points2 ? points2.Value : 0,
            Slots = slotsBox.SelectedItem is int slots ? slots : 0,
        };
        if (charms.Any(c => c.Type == charm.Type && c.SameContent(charm)))
        {
            MessageBox.Show(this, "同じお守りが登録済みです。", "お守り");
            return;
        }
        charms.Add(charm);
        RefreshGrid();
        grid.ClearSelection();
        grid.Rows[^1].Selected = true;
        grid.FirstDisplayedScrollingRowIndex = grid.Rows.Count - 1;
        CharmsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveSelected()
    {
        var indexes = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).OrderByDescending(i => i).ToList();
        if (indexes.Count == 0) return;
        foreach (var index in indexes) charms.RemoveAt(index);
        RefreshGrid();
        CharmsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshGrid()
    {
        grid.Rows.Clear();
        foreach (var charm in charms)
        {
            grid.Rows.Add(
                string.IsNullOrEmpty(charm.Type) ? UnspecifiedLabel : charm.Type,
                charm.Skill1, charm.Points1.ToString("+0;-0"),
                charm.Skill2, string.IsNullOrEmpty(charm.Skill2) ? "" : charm.Points2.ToString("+0;-0"),
                charm.Slots);
        }
    }
}
