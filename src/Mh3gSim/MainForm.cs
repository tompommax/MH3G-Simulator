using System.Text;
using Mh3gSim.Core;

namespace Mh3gSim;

/// <summary>スキル・条件を指定して装備構成を検索する画面。</summary>
internal sealed class MainForm : Form
{
    private const int MaxRequirements = 10;
    private const string AppTitle = "MH3G スキルシミュレーター";
    private static readonly string AppVersion = FormatVersion(typeof(MainForm).Assembly.GetName().Version);

    private readonly GameData data = GameData.LoadEmbedded();
    private readonly SkillSearcher searcher;
    private readonly HashSet<int> excludedArmorIds = [];
    private List<SearchResult> results = [];
    /// <summary>表示中の結果を出した検索条件 (詳細表示・保存は画面の今の入力ではなくこれを使う)。</summary>
    private SearchCondition? lastCondition;
    private CancellationTokenSource? searchCancellation;

    // 条件
    private readonly ComboBox classBox = DropDown("剣士", "ガンナー");
    private readonly ComboBox genderBox = DropDown("男", "女");
    private readonly NumericUpDown rarityBox = Number(1, 10, 10);
    private readonly NumericUpDown weaponSlotBox = Number(0, 3, 0);
    private readonly NumericUpDown maxResultsBox = Number(10, 2000, 200);
    private readonly CheckBox avoidNegativeBox = new() { Text = "マイナススキルを発動させない", Checked = true, AutoSize = true };

    // スキル
    private readonly TextBox skillFilterBox = new() { PlaceholderText = "スキル名で絞り込み", Dock = DockStyle.Top };
    private readonly ListBox skillListBox = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox requirementListBox = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    // お守り
    private readonly CharmEditor charmEditor;
    private readonly CheckBox includeNoCharmBox = new() { Text = "お守りなしも候補にする", Checked = true, AutoSize = true };

    // 実行・結果
    private readonly Button searchButton = new() { Text = "検索", Width = 110, Height = 32 };
    private readonly Button cancelButton = new() { Text = "中止", Width = 80, Height = 32, Enabled = false };
    private readonly Button exportButton = new() { Text = "テキスト保存", Width = 110, Height = 32, Enabled = false };
    private readonly ProgressBar progressBar = new() { Width = 180, Height = 20 };
    private readonly Label statusLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly Label excludedLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly DataGridView resultGrid = new();
    private readonly TextBox detailBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, Font = new Font("BIZ UDゴシック", 10f),
    };

    public MainForm()
    {
        searcher = new SkillSearcher(data);
        charmEditor = new CharmEditor(data) { Dock = DockStyle.Fill };
        Text = $"{AppTitle} v{AppVersion}";
        Font = new Font("Yu Gothic UI", 9.5f);
        ClientSize = new Size(1360, 820);
        StartPosition = FormStartPosition.CenterScreen;

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        Controls.Add(split);
        Load += (_, _) => split.SplitterDistance = 440;
        split.Panel1.Controls.Add(BuildConditionPanel());
        split.Panel2.Controls.Add(BuildResultPanel());

        RefreshSkillList();
        LoadCharms();
        UpdateExcludedLabel();
        statusLabel.Text = $"防具 {data.Armors.Count} / 装飾品 {data.Decorations.Count} / スキル系統 {data.SkillSystems.Count}";
    }

    // ───────── 画面構築 ─────────

    private Control BuildConditionPanel()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));

        var basic = new GroupBox { Text = "基本条件", Dock = DockStyle.Fill, AutoSize = true };
        var basicGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        AddLabeled(basicGrid, "職業", classBox);
        AddLabeled(basicGrid, "性別", genderBox);
        AddLabeled(basicGrid, "レア度上限", rarityBox);
        AddLabeled(basicGrid, "武器スロット", weaponSlotBox);
        AddLabeled(basicGrid, "最大表示件数", maxResultsBox);
        basicGrid.Controls.Add(avoidNegativeBox);
        basicGrid.SetColumnSpan(avoidNegativeBox, 2);
        basic.Controls.Add(basicGrid);
        layout.Controls.Add(basic);

        layout.Controls.Add(BuildSkillGroup());
        layout.Controls.Add(BuildCharmGroup());
        return layout;
    }

    private Control BuildSkillGroup()
    {
        var group = new GroupBox { Text = "欲しいスキル (ダブルクリック / Enter で追加、右をダブルクリックで削除)", Dock = DockStyle.Fill };
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(skillListBox);
        left.Controls.Add(skillFilterBox);

        var right = new Panel { Dock = DockStyle.Fill };
        var clearButton = new Button { Text = "全てクリア", Dock = DockStyle.Bottom, Height = 28 };
        right.Controls.Add(requirementListBox);
        right.Controls.Add(clearButton);

        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);
        group.Controls.Add(columns);

        skillFilterBox.TextChanged += (_, _) => RefreshSkillList();
        skillFilterBox.Enter += (_, _) => BeginInvoke(skillFilterBox.SelectAll);
        skillFilterBox.MouseClick += (_, _) => skillFilterBox.SelectAll();
        skillFilterBox.KeyDown += (_, e) =>
        {
            // Enter で絞り込み結果の先頭を追加
            if (e.KeyCode != Keys.Enter || skillListBox.Items.Count == 0) return;
            skillListBox.SelectedIndex = 0;
            AddSelectedSkill();
            skillFilterBox.Clear();
            e.SuppressKeyPress = true;
        };
        skillListBox.DoubleClick += (_, _) => AddSelectedSkill();
        skillListBox.SelectedIndexChanged += (_, _) => ShowSkillDescription(skillListBox.SelectedItem as SkillItem);
        requirementListBox.DoubleClick += (_, _) =>
        {
            if (requirementListBox.SelectedItem != null) requirementListBox.Items.Remove(requirementListBox.SelectedItem);
        };
        clearButton.Click += (_, _) => requirementListBox.Items.Clear();
        return group;
    }

    private Control BuildCharmGroup()
    {
        var group = new GroupBox { Text = "所持お守り (種類 → スキル → ポイントの順に選んで追加・自動保存)", Dock = DockStyle.Fill };
        group.Controls.Add(charmEditor);
        group.Controls.Add(includeNoCharmBox);
        includeNoCharmBox.Dock = DockStyle.Bottom;
        charmEditor.CharmsChanged += (_, _) => SaveCharms();
        return group;
    }

    private Control BuildResultPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6), WrapContents = false };
        var clearExcludedButton = new Button { Text = "除外を解除", Width = 100, Height = 32 };
        toolbar.Controls.AddRange([searchButton, cancelButton, progressBar, statusLabel, exportButton, clearExcludedButton, excludedLabel]);
        progressBar.Margin = new Padding(6, 8, 6, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(resultGrid);
        split.Panel2.Controls.Add(detailBox);
        panel.Controls.Add(split);
        panel.Controls.Add(toolbar);
        Load += (_, _) => split.SplitterDistance = 330;

        ConfigureResultGrid();
        searchButton.Click += async (_, _) => await RunSearchSafelyAsync();
        cancelButton.Click += (_, _) => searchCancellation?.Cancel();
        exportButton.Click += (_, _) => SaveAllResults();
        clearExcludedButton.Click += (_, _) => { excludedArmorIds.Clear(); UpdateExcludedLabel(); };
        return panel;
    }

    private void ConfigureResultGrid()
    {
        resultGrid.Dock = DockStyle.Fill;
        resultGrid.ReadOnly = true;
        resultGrid.AllowUserToAddRows = false;
        resultGrid.AllowUserToDeleteRows = false;
        resultGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        resultGrid.MultiSelect = false;
        resultGrid.RowHeadersVisible = false;
        resultGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        resultGrid.Columns.Add("No", "No");
        resultGrid.Columns.Add("Defense", "防御(初→最終)");
        foreach (var part in ArmorParts.Names) resultGrid.Columns.Add(part, part);
        resultGrid.Columns.Add("Charm", "お守り");
        resultGrid.Columns.Add("Free", "空きスロット");
        resultGrid.Columns["No"]!.FillWeight = 40;
        resultGrid.Columns["Defense"]!.FillWeight = 85;
        // 詳細は「現在行」に合わせる (右クリックで現在行を移した時も追従させるため SelectionChanged ではなくこちら)
        resultGrid.CurrentCellChanged += (_, _) => ShowSelectedDetail();

        var menu = new ContextMenuStrip();
        for (var part = 0; part < ArmorParts.Count; part++)
        {
            var index = part;
            menu.Items.Add($"この{ArmorParts.Names[part]}防具を除外して再検索", null, async (_, _) => await ExcludeAndSearch(index));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("この結果をコピー", null, (_, _) => CopySelectedResult());
        menu.Items.Add("この結果をテキスト保存", null, (_, _) => SaveSelectedResult());
        resultGrid.ContextMenuStrip = menu;
        resultGrid.CellMouseDown += (_, e) =>
        {
            // 右クリックした行を「現在行」にする (Selected だけ変えると CurrentRow が前の行のままで、
            // 右クリックメニューのコピー・除外が別の行に効いてしまう)
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            resultGrid.CurrentCell = resultGrid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
        };
    }

    // ───────── スキル選択 ─────────

    private sealed record SkillItem(SkillSystem System, SkillActivation Activation)
    {
        public override string ToString() => $"{Activation.Name}  ({System.System} {Activation.Points})";
    }

    private void RefreshSkillList()
    {
        var filter = skillFilterBox.Text.Trim();
        var items = data.SkillSystems
            .SelectMany(s => s.Activations.Where(a => a.Points > 0).Select(a => new SkillItem(s, a)))
            .Where(i => filter.Length == 0 || i.Activation.Name.Contains(filter) || i.System.System.Contains(filter))
            .OrderBy(i => i.System.System).ThenByDescending(i => i.Activation.Points)
            .ToArray();
        skillListBox.BeginUpdate();
        skillListBox.Items.Clear();
        skillListBox.Items.AddRange(items);
        skillListBox.EndUpdate();
    }

    private void AddSelectedSkill()
    {
        if (skillListBox.SelectedItem is not SkillItem item) return;
        // 同じ系統は 1 つだけ (後から選んだものに置き換える)
        var existing = requirementListBox.Items.Cast<SkillItem>().FirstOrDefault(i => i.System == item.System);
        if (existing != null) requirementListBox.Items.Remove(existing);
        else if (requirementListBox.Items.Count >= MaxRequirements)
        {
            MessageBox.Show(this, $"スキルは最大 {MaxRequirements} 個までです。", Text);
            return;
        }
        requirementListBox.Items.Add(item);
    }

    private void ShowSkillDescription(SkillItem? item)
    {
        if (item == null) return;
        detailBox.Text = $"{item.Activation.Name}（{item.System.System} {item.Activation.Points}pt）\r\n{item.Activation.Description}";
    }

    // ───────── お守り ─────────

    private void LoadCharms() => charmEditor.SetCharms(CharmStore.Load());

    private void SaveCharms()
    {
        try { CharmStore.Save(charmEditor.Charms); }
        catch (IOException exception) { statusLabel.Text = $"お守りの保存に失敗: {exception.Message}"; }
    }

    // ───────── 検索 ─────────

    private async Task ExcludeAndSearch(int part)
    {
        if (SelectedResult() is not { } result) return;
        excludedArmorIds.Add(result.Armors[part].Id);
        UpdateExcludedLabel();
        await RunSearchSafelyAsync();
    }

    private async Task RunSearchSafelyAsync()
    {
        try { await RunSearchAsync(); }
        catch (Exception exception)
        {
            SetSearching(false);
            MessageBox.Show(this, exception.ToString(), "検索エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunSearchAsync()
    {
        var requirements = requirementListBox.Items.Cast<SkillItem>()
            .Select(i => new SkillRequirement(i.System.System, i.Activation.Points, i.Activation.Name)).ToList();
        if (requirements.Count == 0)
        {
            MessageBox.Show(this, "欲しいスキルを 1 つ以上追加してください。", Text);
            return;
        }

        var charms = charmEditor.Charms.ToList();
        var unknown = charms.SelectMany(c => c.Skills()).Select(c => c.Key)
            .Where(name => data.FindSystem(name) == null).Distinct().ToList();
        if (unknown.Count > 0)
        {
            MessageBox.Show(this, $"お守りのスキル名が見つかりません: {string.Join(", ", unknown)}", Text);
            return;
        }
        if (includeNoCharmBox.Checked || charms.Count == 0) charms.Insert(0, Charm.None);
        var condition = new SearchCondition
        {
            Requirements = requirements,
            IsGunner = classBox.SelectedIndex == 1,
            IsFemale = genderBox.SelectedIndex == 1,
            MaxRarity = (int)rarityBox.Value,
            WeaponSlots = (int)weaponSlotBox.Value,
            Charms = charms,
            AvoidNegativeSkills = avoidNegativeBox.Checked,
            ExcludedArmorIds = [.. excludedArmorIds],
            MaxResults = (int)maxResultsBox.Value,
        };

        searchCancellation = new CancellationTokenSource();
        var token = searchCancellation.Token;
        var progress = new Progress<double>(p => progressBar.Value = (int)(p * 100));
        SetSearching(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var outcome = await Task.Run(() => searcher.Search(condition, progress, token), token);
            results = outcome.Results;
            lastCondition = condition;
            statusLabel.Text = $"{results.Count} 件 ({watch.Elapsed.TotalSeconds:0.0} 秒)"
                + (outcome.TruncatedSolves > 0 ? $"  ※珠の探索を {outcome.TruncatedSolves} 構成で打ち切り (見落としの可能性あり)" : "");
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "中止しました";
        }
        finally
        {
            SetSearching(false);
        }
        ShowResults();
    }

    private void SetSearching(bool searching)
    {
        searchButton.Enabled = !searching;
        cancelButton.Enabled = searching;
        exportButton.Enabled = !searching && results.Count > 0;
        progressBar.Value = 0;
        if (searching) statusLabel.Text = "検索中…";
    }

    private void ShowResults()
    {
        exportButton.Enabled = results.Count > 0;
        resultGrid.Rows.Clear();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var row = new List<object> { i + 1, $"{r.Defense}→{r.MaxDefense}" };
            row.AddRange(r.Armors.Select(a => (object)a.Name));
            row.Add(r.Charm.ToString());
            row.Add(string.Join(" ", r.FreeSlots));
            resultGrid.Rows.Add(row.ToArray());
        }
        if (results.Count == 0) detailBox.Text = "条件を満たす組み合わせが見つかりませんでした。\r\nレア度上限・武器スロット・お守りを見直してください。";
    }

    private SearchResult? SelectedResult()
    {
        var index = resultGrid.CurrentRow?.Index ?? -1;
        return index >= 0 && index < results.Count ? results[index] : null;
    }

    private void UpdateExcludedLabel() => excludedLabel.Text = excludedArmorIds.Count == 0
        ? ""
        : "除外中: " + string.Join(", ", data.Armors.Where(a => excludedArmorIds.Contains(a.Id)).Select(a => a.Name));

    // ───────── 詳細表示・テキスト出力 ─────────

    private void ShowSelectedDetail()
    {
        if (SelectedResult() is { } result) detailBox.Text = FormatSelected(result);
    }

    private string FormatSelected(SearchResult result) =>
        ResultTextFormatter.FormatResult(result, lastCondition?.WeaponSlots ?? 0);

    private void CopySelectedResult()
    {
        if (SelectedResult() is not { } result) return;
        Clipboard.SetText(FormatSelected(result));
        statusLabel.Text = "選択中の結果をコピーしました";
    }

    private void SaveSelectedResult()
    {
        if (SelectedResult() is not { } result) return;
        var number = results.IndexOf(result) + 1;
        var content = $"{AppTitle} v{AppVersion}  No.{number}{Environment.NewLine}{Environment.NewLine}{FormatSelected(result)}";
        SaveText($"MH3G装備_No{number}_{DateTime.Now:yyyyMMdd_HHmm}.txt", content);
    }

    private void SaveAllResults()
    {
        if (lastCondition == null || results.Count == 0) return;
        var title = $"{AppTitle} v{AppVersion} 検索結果 ({DateTime.Now:yyyy-MM-dd HH:mm})";
        SaveText($"MH3G検索結果_{DateTime.Now:yyyyMMdd_HHmm}.txt", ResultTextFormatter.FormatExport(lastCondition, results, title));
    }

    private void SaveText(string defaultFileName, string content)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "テキスト ファイル (*.txt)|*.txt",
            FileName = defaultFileName,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            // メモ帳などで文字化けしないよう BOM 付き UTF-8 で書く
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            statusLabel.Text = $"保存しました: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"保存できませんでした。{Environment.NewLine}{exception.Message}", AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ───────── 小物 ─────────

    private static ComboBox DropDown(params string[] items)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        box.Items.AddRange(items);
        box.SelectedIndex = 0;
        return box;
    }

    private static NumericUpDown Number(int min, int max, int value) =>
        new() { Minimum = min, Maximum = max, Value = value, Width = 70 };

    private static void AddLabeled(TableLayoutPanel panel, string label, Control control)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) });
        panel.Controls.Add(control);
    }

    private static string FormatVersion(Version? version) =>
        version == null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
}
