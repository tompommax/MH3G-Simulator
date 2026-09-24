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
    private readonly CharmCatalog charmCatalog;
    /// <summary>実在するお守りの一覧づくり (起動直後に裏で始める)。</summary>
    private readonly Task catalogReady;
    private readonly CharmFinder charmFinder;
    /// <summary>検索のたびに増やす番号 (終わった検索から遅れて届く進捗を無視するため)。</summary>
    private int searchGeneration;
    private bool isSearching;
    private readonly HashSet<int> excludedArmorIds = [];
    private List<SearchResult> results = [];
    /// <summary>表示中の結果がお守りの自動計算なら、結果ごとの必要なお守り (results と同じ並び)。通常の検索なら null。</summary>
    private List<CharmSuggestion>? suggestions;
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
    private readonly RadioButton charmInputRadio = new() { Text = "入力したお守りで検索", Checked = true, AutoSize = true };
    private readonly RadioButton charmAutoRadio = new() { Text = "自動計算 (成立するお守りを探す)", AutoSize = true };
    /// <summary>自動計算で候補にするお守りのテーブル (先頭は「すべて」)。</summary>
    private readonly ComboBox charmTableBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Enabled = false };
    /// <summary>入力中のお守りが出るテーブル (自分のテーブルを調べる手がかり)。</summary>
    private readonly Label charmTablesLabel = new() { AutoSize = true, MaximumSize = new Size(420, 0), Padding = new Padding(3, 2, 0, 2) };
    /// <summary>表示中の自動計算の結果を出した時のテーブル (null = すべて)。</summary>
    private int? lastCharmTable;
    private const int CharmExampleCount = 3;

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
        charmCatalog = new CharmCatalog(data.Charms);
        catalogReady = Task.Run(() => charmCatalog.Entries.Count);
        charmFinder = new CharmFinder(searcher);
        charmEditor = new CharmEditor(data) { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        charmTableBox.Items.Add("すべて");
        charmTableBox.Items.AddRange([.. charmCatalog.TableNumbers.Select(t => (object)t.ToString())]);
        charmTableBox.SelectedIndex = 0;
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
        LoadCharm();
        UpdateExcludedLabel();
        statusLabel.Text = $"防具 {data.Armors.Count} / 装飾品 {data.Decorations.Count} / スキル系統 {data.SkillSystems.Count}";
    }

    // ───────── 画面構築 ─────────

    private Control BuildConditionPanel()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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
        var group = new GroupBox
        {
            Text = "お守り (スキル1 を「（なし）」でお守りなし・自動保存)",
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        modes.Controls.AddRange([charmInputRadio, charmAutoRadio]);
        var tableRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var tableCaption = new Label { Text = "自動計算で使うテーブル", AutoSize = true, Padding = new Padding(3, 5, 0, 0) };
        tableRow.Controls.AddRange([tableCaption, charmTableBox]);
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        stack.Controls.Add(modes);
        stack.Controls.Add(tableRow);
        stack.Controls.Add(charmEditor);
        stack.Controls.Add(charmTablesLabel);
        group.Controls.Add(stack);
        charmEditor.CharmChanged += (_, _) =>
        {
            SaveCharm();
            UpdateCharmTablesLabel();
        };
        // 自動計算の時は入力欄を使わず、テーブルの絞り込みを使う
        charmAutoRadio.CheckedChanged += (_, _) =>
        {
            charmEditor.Enabled = !charmAutoRadio.Checked;
            charmTableBox.Enabled = charmAutoRadio.Checked;
        };
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
        resultGrid.Columns.Add("Kinds", "出るお守り");
        resultGrid.Columns.Add("Tables", "出るテーブル");
        resultGrid.Columns["Kinds"]!.Visible = false;
        resultGrid.Columns["Tables"]!.Visible = false;
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

    /// <summary>保存済みのお守りを読み込む (以前の版で複数登録していた場合は先頭の 1 つを使う)。</summary>
    private void LoadCharm()
    {
        charmEditor.SetCharm(CharmStore.Load().FirstOrDefault() ?? Charm.None);
        UpdateCharmTablesLabel();
    }

    /// <summary>入力中のお守りと同じスキル・ポイント・スロットのお守りが、どのテーブルに出るか。</summary>
    private async void UpdateCharmTablesLabel()
    {
        if (!catalogReady.IsCompleted && !charmEditor.Charm.IsNone)
        {
            // 実在するお守りの一覧は起動直後に裏で作っている (画面を止めない)
            charmTablesLabel.Text = "このお守りが出るテーブルを計算中…";
            await catalogReady;
        }
        var charm = charmEditor.Charm;
        if (charm.IsNone)
        {
            charmTablesLabel.Text = "";
            return;
        }
        var same = charmCatalog.FindSame(charm);
        if (same.Count == 0)
        {
            charmTablesLabel.Text = "このお守りはどのテーブルにも出ません (スキルの順番・ポイント・スロットを確かめてください)";
            return;
        }
        var tables = charmCatalog.TableNumbers.Where(t => same.Any(e => e.AppearsOn(t))).ToList();
        var names = string.Join("・", same.Select(e => e.Name).Distinct());
        charmTablesLabel.Text = $"このお守りが出るテーブル: {ResultTextFormatter.FormatTables(tables, charmCatalog.TableNumbers)} ({names})";
    }

    private void SaveCharm()
    {
        var charm = charmEditor.Charm;
        try { CharmStore.Save(charm.IsNone ? [] : [charm]); }
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

        var autoCharm = charmAutoRadio.Checked;
        var condition = new SearchCondition
        {
            Requirements = requirements,
            IsGunner = classBox.SelectedIndex == 1,
            IsFemale = genderBox.SelectedIndex == 1,
            MaxRarity = (int)rarityBox.Value,
            WeaponSlots = (int)weaponSlotBox.Value,
            Charms = autoCharm ? [] : [charmEditor.Charm],
            AvoidNegativeSkills = avoidNegativeBox.Checked,
            ExcludedArmorIds = [.. excludedArmorIds],
            MaxResults = (int)maxResultsBox.Value,
        };

        searchCancellation = new CancellationTokenSource();
        var token = searchCancellation.Token;
        var generation = ++searchGeneration;
        var progress = new Progress<double>(p =>
        {
            // 進捗は画面のスレッドへ後から届くので、終わった検索・前の検索の分は無視する
            if (isSearching && generation == searchGeneration) progressBar.Value = Math.Clamp((int)(p * 100), 0, 100);
        });
        SetSearching(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            int truncatedSolves;
            if (autoCharm)
            {
                int? table = charmTableBox.SelectedIndex > 0 ? charmCatalog.TableNumbers[charmTableBox.SelectedIndex - 1] : null;
                var (found, described) = await Task.Run(() =>
                {
                    var candidates = charmCatalog.RequirementsFor(requirements, table);
                    var outcome = charmFinder.Find(condition, candidates, progress, token);
                    var list = outcome.Suggestions.Take(condition.MaxResults)
                        .Select(s => s with { Availability = charmCatalog.Describe(s.Requirement, requirements, table, CharmExampleCount) })
                        .ToList();
                    return (outcome, list);
                }, token);
                suggestions = described;
                lastCharmTable = table;
                results = suggestions.Select(s => s.Best).ToList();
                truncatedSolves = found.TruncatedSolves;
                var where = table is { } only ? $"テーブル {only} で" : "実在する";
                statusLabel.Text = suggestions switch
                {
                    [] => $"{where}お守りでは成立しません",
                    [{ Requirement.IsZero: true }] => "お守りなしで成立します",
                    _ => $"必要なお守り {suggestions.Count} 通り{(table is { } shown ? $" (テーブル {shown})" : "")}",
                };
            }
            else
            {
                var outcome = await Task.Run(() => searcher.Search(condition, progress, token), token);
                suggestions = null;
                results = outcome.Results;
                truncatedSolves = outcome.TruncatedSolves;
                statusLabel.Text = $"{results.Count} 件";
            }
            lastCondition = condition;
            statusLabel.Text += $" ({watch.Elapsed.TotalSeconds:0.0} 秒)"
                + (truncatedSolves > 0 ? $"  ※珠の探索を {truncatedSolves} 構成で打ち切り (見落としの可能性あり)" : "");
        }
        catch (OperationCanceledException)
        {
            // 結果の一覧は前回の検索のまま残る (今回の条件の結果と見間違えないように書く)
            statusLabel.Text = results.Count > 0 ? "中止しました (表示は前回の検索結果)" : "中止しました";
        }
        finally
        {
            SetSearching(false);
        }
        ShowResults();
    }

    private void SetSearching(bool searching)
    {
        isSearching = searching;
        searchButton.Enabled = !searching;
        cancelButton.Enabled = searching;
        exportButton.Enabled = !searching && results.Count > 0;
        progressBar.Value = 0;
        if (searching) statusLabel.Text = "検索中…";
    }

    private void ShowResults()
    {
        exportButton.Enabled = results.Count > 0;
        var autoCharm = suggestions != null;
        resultGrid.Columns["Charm"]!.HeaderText = autoCharm ? "必要なお守り (以上)" : "お守り";
        // 自動計算では必要なお守りの説明が長いので広げる
        resultGrid.Columns["Charm"]!.FillWeight = autoCharm ? 170 : 100;
        resultGrid.Columns["Kinds"]!.Visible = autoCharm;
        resultGrid.Columns["Tables"]!.Visible = autoCharm;
        resultGrid.Rows.Clear();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var row = new List<object> { i + 1, $"{r.Defense}→{r.MaxDefense}" };
            row.AddRange(r.Armors.Select(a => (object)a.Name));
            if (suggestions != null)
            {
                var suggestion = suggestions[i];
                var shown = !suggestion.Requirement.IsZero && suggestion.Availability != null;
                row.Add(ResultTextFormatter.ShortRequirement(suggestion.Requirement, lastCondition!.Requirements));
                row.Add(shown ? string.Join("・", suggestion.Availability!.Kinds.Select(k => k.Replace("お守り", ""))) : "");
                row.Add(shown ? ResultTextFormatter.FormatTables(suggestion.Availability!.Tables, charmCatalog.TableNumbers) : "");
            }
            else
            {
                row.Add(r.Charm.ToString());
                row.Add("");
                row.Add("");
            }
            row.Add(string.Join(" ", r.FreeSlots));
            resultGrid.Rows.Add(row.ToArray());
        }
        if (results.Count == 0)
        {
            detailBox.Text = autoCharm
                ? "実在するお守りを使っても、条件を満たす組み合わせが見つかりませんでした。\r\nレア度上限・武器スロット・スキルを見直してください。"
                : "条件を満たす組み合わせが見つかりませんでした。\r\nレア度上限・武器スロット・お守りを見直してください。";
        }
    }

    private int SelectedIndex()
    {
        var index = resultGrid.CurrentRow?.Index ?? -1;
        return index >= 0 && index < results.Count ? index : -1;
    }

    private SearchResult? SelectedResult() => SelectedIndex() is var index and >= 0 ? results[index] : null;

    private void UpdateExcludedLabel() => excludedLabel.Text = excludedArmorIds.Count == 0
        ? ""
        : "除外中: " + string.Join(", ", data.Armors.Where(a => excludedArmorIds.Contains(a.Id)).Select(a => a.Name));

    // ───────── 詳細表示・テキスト出力 ─────────

    private void ShowSelectedDetail()
    {
        if (SelectedIndex() is var index and >= 0) detailBox.Text = FormatSelected(index);
    }

    private string FormatSelected(int index) => suggestions != null
        ? ResultTextFormatter.FormatSuggestion(suggestions[index], lastCondition!.Requirements, lastCondition.WeaponSlots, charmCatalog.TableNumbers)
        : ResultTextFormatter.FormatResult(results[index], lastCondition?.WeaponSlots ?? 0);

    private void CopySelectedResult()
    {
        if (SelectedIndex() is not (var index and >= 0)) return;
        Clipboard.SetText(FormatSelected(index));
        statusLabel.Text = "選択中の結果をコピーしました";
    }

    private void SaveSelectedResult()
    {
        if (SelectedIndex() is not (var index and >= 0)) return;
        var number = index + 1;
        var content = $"{AppTitle} v{AppVersion}  No.{number}{Environment.NewLine}{Environment.NewLine}{FormatSelected(index)}";
        SaveText($"MH3G装備_No{number}_{DateTime.Now:yyyyMMdd_HHmm}.txt", content);
    }

    private void SaveAllResults()
    {
        if (lastCondition == null || results.Count == 0) return;
        var title = $"{AppTitle} v{AppVersion} 検索結果 ({DateTime.Now:yyyy-MM-dd HH:mm})";
        SaveText($"MH3G検索結果_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            ResultTextFormatter.FormatExport(lastCondition, results, title, suggestions == null
                ? null
                : new ResultTextFormatter.CharmSearchContext(suggestions, lastCharmTable, charmCatalog.TableNumbers)));
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
