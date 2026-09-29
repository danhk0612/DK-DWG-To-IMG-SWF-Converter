using System.Diagnostics;

namespace DwgToPngPoC;

public sealed class MainForm : Form
{
    private readonly ListBox _files = new();
    private readonly CheckBox _exportSvg = new();
    private readonly CheckBox _exportPng = new();
    private readonly CheckBox _exportSwf = new();
    private readonly NumericUpDown _width = new();
    private readonly NumericUpDown _height = new();

    private readonly CheckBox _transparentBackground = new();
    private readonly Button _backgroundColorButton = new();
    private readonly CheckBox _overrideStrokeColor = new();
    private readonly Button _strokeColorButton = new();
    private readonly CheckBox _overrideStrokeWidth = new();
    private readonly NumericUpDown _strokeWidth = new();
    private readonly CheckBox _fillClosedShapes = new();
    private readonly Button _fillColorButton = new();

    private readonly NumericUpDown _contentWidth = new();
    private readonly NumericUpDown _contentHeight = new();
    private readonly ComboBox _horizontalPlacement = new();
    private readonly ComboBox _verticalPlacement = new();

    private readonly TextBox _outputFolder = new();
    private readonly Label _flashViewerStatus = new();
    private readonly Button _flashViewerAction = new();
    private readonly Button _convert = new();
    private readonly Button _viewSwf = new();
    private string? _lastSwfPath;
    private readonly TextBox _log = new();

    private Color _backgroundColor = Color.White;
    private Color _strokeColor = Color.Black;
    private Color _fillColor = Color.White;

    public MainForm(IEnumerable<string> initialFiles)
    {
        Text = "DK DWG To IMG/SWF Converter";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)!;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        Width = 960;
        Height = 700;
        MinimumSize = new Size(840, 620);
        AllowDrop = true;

        BuildUi();
        ApplySettings(SettingsStore.Load());
        LegacyFlashViewer.EnsureDirectories();
        UpdateFlashViewerStatus();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        FormClosing += (_, _) => SaveSettings();

        AddFiles(initialFiles);
    }

    private void BuildUi()
    {
        SuspendLayout();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 138));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildFilePanel(), 0, 0);
        root.Controls.Add(BuildSettingsTabs(), 0, 1);
        root.Controls.Add(BuildOutputPanel(), 0, 2);
        root.Controls.Add(BuildActionsPanel(), 0, 3);
        root.Controls.Add(BuildLogPanel(), 0, 4);

        ResumeLayout();
    }

    private Control BuildFilePanel()
    {
        var group = new GroupBox
        {
            Text = "입력 DWG",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 4)
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));

        _files.Dock = DockStyle.Fill;
        _files.Margin = new Padding(0, 0, 0, 4);
        _files.HorizontalScrollbar = true;
        _files.IntegralHeight = false;
        _files.BorderStyle = BorderStyle.FixedSingle;
        panel.Controls.Add(_files, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(6, 0, 0, 0)
        };

        var add = new Button { Text = "파일 추가", Width = 94, Height = 28, Margin = new Padding(0, 0, 0, 4) };
        add.Click += (_, _) => BrowseFiles();

        var remove = new Button { Text = "선택 제거", Width = 94, Height = 28, Margin = new Padding(0, 0, 0, 4) };
        remove.Click += (_, _) => RemoveSelected();

        var clear = new Button { Text = "전체 제거", Width = 94, Height = 28, Margin = new Padding(0) };
        clear.Click += (_, _) => _files.Items.Clear();

        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);
        buttons.Controls.Add(clear);
        panel.Controls.Add(buttons, 1, 0);

        group.Controls.Add(panel);
        return group;
    }

    private Control BuildSettingsTabs()
    {
        var group = new GroupBox
        {
            Text = "변환 설정",
            Dock = DockStyle.Fill,
            Padding = new Padding(6),
            Margin = new Padding(0, 0, 0, 6)
        };

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 4)
        };
        tabs.TabPages.Add(BuildOutputTab());
        tabs.TabPages.Add(BuildStyleTab());
        tabs.TabPages.Add(BuildLayoutTab());

        group.Controls.Add(tabs);
        return group;
    }

    private TabPage BuildOutputTab()
    {
        var page = new TabPage("출력");
        var grid = CreateSettingsGrid(3);
        page.Controls.Add(grid);

        _exportSvg.Text = "SVG";
        _exportPng.Text = "PNG";
        _exportSwf.Text = "SWF";
        ConfigureCheckBox(_exportSvg);
        ConfigureCheckBox(_exportPng);
        ConfigureCheckBox(_exportSwf);

        _width.Minimum = 64;
        _width.Maximum = 30000;
        ConfigureInputControl(_width);

        _height.Minimum = 64;
        _height.Maximum = 30000;
        ConfigureInputControl(_height);

        grid.Controls.Add(MakeLabel("출력 형식"), 0, 0);
        var formatGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        formatGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        formatGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        formatGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        formatGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        formatGrid.Controls.Add(_exportSvg, 0, 0);
        formatGrid.Controls.Add(_exportPng, 1, 0);
        formatGrid.Controls.Add(_exportSwf, 2, 0);
        grid.SetColumnSpan(formatGrid, 3);
        grid.Controls.Add(formatGrid, 1, 0);

        grid.Controls.Add(MakeLabel("작업 공간 너비"), 0, 1);
        grid.Controls.Add(_width, 1, 1);
        grid.Controls.Add(MakeLabel("작업 공간 높이"), 2, 1);
        grid.Controls.Add(_height, 3, 1);

        _flashViewerStatus.Dock = DockStyle.Fill;
        _flashViewerStatus.TextAlign = ContentAlignment.MiddleLeft;
        _flashViewerStatus.Margin = new Padding(3, 0, 3, 0);

        grid.Controls.Add(MakeLabel("SWF 뷰어"), 0, 2);
        grid.Controls.Add(_flashViewerStatus, 1, 2);

        _flashViewerAction.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _flashViewerAction.Height = 28;
        _flashViewerAction.Margin = new Padding(4, 2, 4, 2);
        _flashViewerAction.Click += FlashViewerActionClickedAsync;
        grid.Controls.Add(_flashViewerAction, 2, 2);

        var openViewerFolder = new Button
        {
            Text = "뷰어 폴더 열기",
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Height = 28,
            Margin = new Padding(4, 2, 4, 2)
        };
        openViewerFolder.Click += (_, _) => OpenFlashViewerFolder();
        grid.Controls.Add(openViewerFolder, 3, 2);

        return page;
    }

    private TabPage BuildStyleTab()
    {
        var page = new TabPage("스타일");
        var grid = CreateSettingsGrid(3);
        page.Controls.Add(grid);

        _transparentBackground.Text = "투명 배경";
        ConfigureCheckBox(_transparentBackground);
        _transparentBackground.CheckedChanged += (_, _) => UpdateStyleControlState();
        grid.Controls.Add(_transparentBackground, 0, 0);
        SetupColorButton(_backgroundColorButton, () => _backgroundColor, c => _backgroundColor = c);
        grid.Controls.Add(_backgroundColorButton, 1, 0);

        _overrideStrokeColor.Text = "선 색상 지정";
        ConfigureCheckBox(_overrideStrokeColor);
        _overrideStrokeColor.CheckedChanged += (_, _) => UpdateStyleControlState();
        grid.Controls.Add(_overrideStrokeColor, 0, 1);
        SetupColorButton(_strokeColorButton, () => _strokeColor, c => _strokeColor = c);
        grid.Controls.Add(_strokeColorButton, 1, 1);

        _overrideStrokeWidth.Text = "선 두께 지정";
        ConfigureCheckBox(_overrideStrokeWidth);
        _overrideStrokeWidth.CheckedChanged += (_, _) => UpdateStyleControlState();
        grid.Controls.Add(_overrideStrokeWidth, 2, 1);

        _strokeWidth.DecimalPlaces = 1;
        _strokeWidth.Increment = 0.1m;
        _strokeWidth.Minimum = 0.1m;
        _strokeWidth.Maximum = 100m;
        ConfigureInputControl(_strokeWidth);
        grid.Controls.Add(_strokeWidth, 3, 1);

        _fillClosedShapes.Text = "닫힌 영역 채우기";
        ConfigureCheckBox(_fillClosedShapes);
        _fillClosedShapes.CheckedChanged += (_, _) => UpdateStyleControlState();
        grid.Controls.Add(_fillClosedShapes, 0, 2);
        SetupColorButton(_fillColorButton, () => _fillColor, c => _fillColor = c);
        grid.Controls.Add(_fillColorButton, 1, 2);

        return page;
    }

    private TabPage BuildLayoutTab()
    {
        var page = new TabPage("크기 / 정렬");
        var grid = CreateSettingsGrid(2);
        page.Controls.Add(grid);

        SetupContentSize(_contentWidth);
        SetupContentSize(_contentHeight);

        grid.Controls.Add(MakeLabel("내용 최대 너비"), 0, 0);
        grid.Controls.Add(_contentWidth, 1, 0);
        grid.Controls.Add(MakeLabel("내용 최대 높이"), 2, 0);
        grid.Controls.Add(_contentHeight, 3, 0);

        _horizontalPlacement.DropDownStyle = ComboBoxStyle.DropDownList;
        _horizontalPlacement.Items.AddRange(["왼쪽", "가운데", "오른쪽"]);
        ConfigureInputControl(_horizontalPlacement);

        _verticalPlacement.DropDownStyle = ComboBoxStyle.DropDownList;
        _verticalPlacement.Items.AddRange(["위", "가운데", "아래"]);
        ConfigureInputControl(_verticalPlacement);

        grid.Controls.Add(MakeLabel("가로 정렬"), 0, 1);
        grid.Controls.Add(_horizontalPlacement, 1, 1);
        grid.Controls.Add(MakeLabel("세로 정렬"), 2, 1);
        grid.Controls.Add(_verticalPlacement, 3, 1);

        return page;
    }

    private Control BuildOutputPanel()
    {
        var group = new GroupBox
        {
            Text = "출력 폴더",
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(0, 0, 0, 4)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));

        _outputFolder.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _outputFolder.Margin = new Padding(0, 2, 6, 2);
        _outputFolder.PlaceholderText = "비워두면 원본 폴더 아래 형식별 폴더에 저장";
        grid.Controls.Add(_outputFolder, 0, 0);

        var browse = new Button
        {
            Text = "폴더 선택",
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Height = 28,
            Margin = new Padding(0, 2, 0, 2)
        };
        browse.Click += (_, _) => BrowseOutputFolder();
        grid.Controls.Add(browse, 1, 0);

        group.Controls.Add(grid);
        return group;
    }

    private Control BuildActionsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _viewSwf.Text = "SWF 보기";
        _viewSwf.Width = 96;
        _viewSwf.Height = 30;
        _viewSwf.Anchor = AnchorStyles.None;
        _viewSwf.Margin = new Padding(0, 3, 6, 3);
        _viewSwf.Click += (_, _) => OpenSwfViewer();

        var openOutput = new Button
        {
            Text = "출력 폴더 열기",
            Width = 116,
            Height = 30,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 3, 6, 3)
        };
        openOutput.Click += (_, _) => OpenOutputFolder();

        _convert.Text = "변환 시작";
        _convert.Width = 126;
        _convert.Height = 30;
        _convert.Anchor = AnchorStyles.None;
        _convert.Margin = new Padding(0, 3, 0, 3);
        _convert.Click += ConvertClickedAsync;

        panel.Controls.Add(_viewSwf, 1, 0);
        panel.Controls.Add(openOutput, 2, 0);
        panel.Controls.Add(_convert, 3, 0);
        return panel;
    }

    private Control BuildLogPanel()
    {
        var group = new GroupBox
        {
            Text = "작업 로그",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(0)
        };

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9F);

        group.Controls.Add(_log);
        return group;
    }

    private static TableLayoutPanel CreateSettingsGrid(int rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = rows,
            Padding = new Padding(8, 6, 8, 6)
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        for (var i = 0; i < rows; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));

        return grid;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        Padding = new Padding(4, 0, 6, 0),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static void ConfigureCheckBox(CheckBox checkBox)
    {
        checkBox.AutoSize = true;
        checkBox.Anchor = AnchorStyles.Left;
        checkBox.Margin = new Padding(4, 0, 4, 0);
    }

    private static void ConfigureInputControl(Control control)
    {
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(4, 2, 4, 2);
    }

    private void SetupColorButton(Button button, Func<Color> getter, Action<Color> setter)
    {
        button.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        button.Height = 28;
        button.Margin = new Padding(4, 2, 4, 2);
        button.Click += (_, _) =>
        {
            using var dialog = new ColorDialog
            {
                Color = getter(),
                FullOpen = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            setter(dialog.Color);
            UpdateColorButtons();
        };
    }

    private static void SetupContentSize(NumericUpDown control)
    {
        control.Minimum = 16;
        control.Maximum = 30000;
        ConfigureInputControl(control);
    }

    private void BrowseFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "DWG files (*.dwg)|*.dwg",
            Multiselect = true,
            Title = "DWG 파일 선택"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddFiles(dialog.FileNames);
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "출력 폴더 선택" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _outputFolder.Text = dialog.SelectedPath;
    }

    private void RemoveSelected()
    {
        var selected = _files.SelectedItems.Cast<object>().ToArray();
        foreach (var item in selected)
            _files.Items.Remove(item);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var existing = _files.Items.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                continue;
            var fullPath = Path.GetFullPath(path);
            if (existing.Add(fullPath))
                _files.Items.Add(fullPath);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            AddFiles(paths);
    }

    private async void ConvertClickedAsync(object? sender, EventArgs e)
    {
        if (_files.Items.Count == 0)
        {
            MessageBox.Show(this, "변환할 DWG 파일을 추가하세요.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settings = ReadSettingsFromUi();
        if (!settings.ExportSvg && !settings.ExportPng && !settings.ExportSwf)
        {
            MessageBox.Show(this, "SVG, PNG, SWF 중 하나 이상을 선택하세요.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var files = _files.Items.Cast<string>().ToArray();

        SaveSettings();
        _convert.Enabled = false;
        _log.Clear();

        var selectedOutput = settings.OutputFolder.Trim();
        var pipeline = new CadConversionPipeline(AppendLog, ConfirmDrawingRisk);

        try
        {
            foreach (var file in files)
            {
                AppendLog($"변환: {Path.GetFileName(file)}");
                try
                {
                    var result = await Task.Run(() => pipeline.Convert(file, selectedOutput, settings));
                    if (result.SvgPath is not null)
                        AppendLog($"  SVG: {result.SvgPath}");
                    if (result.PngPath is not null)
                        AppendLog($"  PNG: {result.PngPath}");
                    if (result.SwfPath is not null)
                    {
                        _lastSwfPath = result.SwfPath;
                        AppendLog($"  SWF: {result.SwfPath}");
                    }
                }
                catch (OutOfMemoryException)
                {
                    AppendLog("  실패: 변환 중 시스템 메모리가 부족해 작업을 중단했습니다.");
                    if (settings.ExportSvg || settings.ExportPng)
                    {
                        AppendLog("  대용량/복잡 도면에서는 PNG/SVG용 ACadSharp.Image 전체 SVG 렌더링이 10GB 이상의 메모리를 사용할 수 있습니다.");
                        if (settings.ExportSwf)
                            AppendLog("  SWF 직접 변환이 먼저 완료됐다면 위 로그의 SWF 저장 경로에 결과 파일이 남아 있습니다.");
                    }
                    AppendLog("  메모리 부족 이후에는 안정성을 위해 나머지 배치 변환도 중단합니다.");

                    MessageBox.Show(this,
                        "변환 중 시스템 메모리가 부족해 작업을 중단했습니다." + Environment.NewLine + Environment.NewLine +
                        ((settings.ExportSvg || settings.ExportPng)
                            ? "대용량/복잡 도면의 PNG/SVG 생성은 전체 SVG 렌더링 단계에서 메모리를 많이 사용할 수 있습니다." + Environment.NewLine
                            : string.Empty) +
                        "이 오류가 발생한 뒤에는 프로그램을 다시 실행한 후 다른 파일을 처리하는 것을 권장합니다.",
                        "메모리 부족", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    try
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                    catch
                    {
                        // Recovery is best-effort only after an OOM.
                    }

                    break;
                }
                catch (Exception ex)
                {
                    AppendLog($"  실패: {ex.Message}");
                }
            }
            AppendLog("완료");
        }
        finally
        {
            _convert.Enabled = true;
        }
    }

    private DrawingRiskDecision ConfirmDrawingRisk(DrawingRiskAssessment assessment)
    {
        if (InvokeRequired)
        {
            return (DrawingRiskDecision)Invoke(
                new Func<DrawingRiskDecision>(() => ConfirmDrawingRisk(assessment)));
        }

        var levelText = assessment.Level == DrawingRiskLevel.High ? "고위험" : "주의";
        var message =
            $"이 도면은 현재 변환 엔진 기준으로 {levelText} 구간입니다." + Environment.NewLine + Environment.NewLine +
            $"파일 크기: {assessment.FileSizeMb:0.0} MB" + Environment.NewLine +
            $"모델 공간 엔티티: {assessment.ModelEntityCount:N0}개" + Environment.NewLine +
            $"Hatch: {assessment.HatchCount:N0} / Insert: {assessment.InsertCount:N0} / Spline: {assessment.SplineCount:N0}" + Environment.NewLine + Environment.NewLine +
            "복잡한 도면은 변환 시간이 매우 길어지거나, 메모리 부족·누락·깨진 결과가 발생할 수 있습니다.";

        if (assessment.Level == DrawingRiskLevel.Caution)
        {
            var result = MessageBox.Show(this,
                message + Environment.NewLine + Environment.NewLine +
                "계속 변환하시겠습니까?",
                "도면 복잡도 주의",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            return result == DialogResult.Yes
                ? DrawingRiskDecision.ContinueSelectedOutputs
                : DrawingRiskDecision.SkipFile;
        }

        var hasPngOrSvg = assessment.RequestedPng || assessment.RequestedSvg;
        if (!hasPngOrSvg)
        {
            var result = MessageBox.Show(this,
                message + Environment.NewLine + Environment.NewLine +
                "SWF 직접 변환도 결과 품질을 보장할 수 없습니다." + Environment.NewLine +
                "그래도 계속하시겠습니까?",
                "고위험 도면",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            return result == DialogResult.Yes
                ? DrawingRiskDecision.ContinueSelectedOutputs
                : DrawingRiskDecision.SkipFile;
        }

        var highRiskResult = MessageBox.Show(this,
            message + Environment.NewLine + Environment.NewLine +
            "PNG/SVG는 전체 SVG 렌더링 과정에서 메모리 부족 가능성이 특히 높습니다." + Environment.NewLine +
            "SWF만 시도해도 결과 품질은 보장되지 않습니다." + Environment.NewLine + Environment.NewLine +
            "[예] 선택한 출력 형식으로 계속" + Environment.NewLine +
            "[아니요] SWF만 시도" + Environment.NewLine +
            "[취소] 이 파일 건너뛰기",
            "고위험 도면",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button3);

        return highRiskResult switch
        {
            DialogResult.Yes => DrawingRiskDecision.ContinueSelectedOutputs,
            DialogResult.No => DrawingRiskDecision.TrySwfOnly,
            _ => DrawingRiskDecision.SkipFile
        };
    }

    private ConverterSettings ReadSettingsFromUi()
    {
        return new ConverterSettings
        {
            ExportSvg = _exportSvg.Checked,
            ExportPng = _exportPng.Checked,
            ExportSwf = _exportSwf.Checked,
            Width = (int)_width.Value,
            Height = (int)_height.Value,
            TransparentBackground = _transparentBackground.Checked,
            BackgroundColor = ColorToHex(_backgroundColor),
            OverrideStrokeColor = _overrideStrokeColor.Checked,
            StrokeColor = ColorToHex(_strokeColor),
            OverrideStrokeWidth = _overrideStrokeWidth.Checked,
            StrokeWidthPixels = _strokeWidth.Value,
            FillClosedShapes = _fillClosedShapes.Checked,
            FillColor = ColorToHex(_fillColor),
            ContentWidth = (int)_contentWidth.Value,
            ContentHeight = (int)_contentHeight.Value,
            HorizontalPlacement = (HorizontalPlacement)Math.Max(0, _horizontalPlacement.SelectedIndex),
            VerticalPlacement = (VerticalPlacement)Math.Max(0, _verticalPlacement.SelectedIndex),
            OutputFolder = _outputFolder.Text.Trim()
        };
    }

    private void ApplySettings(ConverterSettings settings)
    {
        _exportSvg.Checked = settings.ExportSvg;
        _exportPng.Checked = settings.ExportPng;
        _exportSwf.Checked = settings.ExportSwf;
        SetNumeric(_width, settings.Width);
        SetNumeric(_height, settings.Height);

        _transparentBackground.Checked = settings.TransparentBackground;
        _backgroundColor = HexToColor(settings.BackgroundColor, Color.White);
        _overrideStrokeColor.Checked = settings.OverrideStrokeColor;
        _strokeColor = HexToColor(settings.StrokeColor, Color.Black);
        _overrideStrokeWidth.Checked = settings.OverrideStrokeWidth;
        SetNumeric(_strokeWidth, settings.StrokeWidthPixels);
        _fillClosedShapes.Checked = settings.FillClosedShapes;
        _fillColor = HexToColor(settings.FillColor, Color.White);

        SetNumeric(_contentWidth, settings.ContentWidth);
        SetNumeric(_contentHeight, settings.ContentHeight);
        _horizontalPlacement.SelectedIndex = Math.Clamp((int)settings.HorizontalPlacement, 0, 2);
        _verticalPlacement.SelectedIndex = Math.Clamp((int)settings.VerticalPlacement, 0, 2);
        _outputFolder.Text = settings.OutputFolder ?? string.Empty;

        UpdateColorButtons();
        UpdateStyleControlState();
    }

    private static void SetNumeric(NumericUpDown control, decimal value)
    {
        control.Value = Math.Clamp(value, control.Minimum, control.Maximum);
    }

    private void SaveSettings()
    {
        try
        {
            SettingsStore.Save(ReadSettingsFromUi());
        }
        catch (Exception ex)
        {
            AppendLog($"설정 저장 실패: {ex.Message}");
        }
    }

    private void UpdateStyleControlState()
    {
        _backgroundColorButton.Enabled = !_transparentBackground.Checked;
        _strokeColorButton.Enabled = _overrideStrokeColor.Checked;
        _strokeWidth.Enabled = _overrideStrokeWidth.Checked;
        _fillColorButton.Enabled = _fillClosedShapes.Checked;
    }

    private void UpdateColorButtons()
    {
        UpdateColorButton(_backgroundColorButton, _backgroundColor);
        UpdateColorButton(_strokeColorButton, _strokeColor);
        UpdateColorButton(_fillColorButton, _fillColor);
    }

    private static void UpdateColorButton(Button button, Color color)
    {
        button.BackColor = color;
        button.ForeColor = color.GetBrightness() < 0.5f ? Color.White : Color.Black;
        button.Text = ColorToHex(color);
        button.UseVisualStyleBackColor = false;
    }

    private void UpdateFlashViewerStatus()
    {
        _flashViewerStatus.Text = LegacyFlashViewer.StatusText;
        _flashViewerStatus.ForeColor = LegacyFlashViewer.IsAvailable ? Color.DarkGreen : Color.DarkRed;
        _flashViewerAction.Visible = !LegacyFlashViewer.IsAvailable;
        _flashViewerAction.Text = LegacyFlashViewer.IsChromiumX86
            ? "상태 새로고침"
            : "Chromium 다운로드";
    }

    private async void FlashViewerActionClickedAsync(object? sender, EventArgs e)
    {
        if (LegacyFlashViewer.IsChromiumX86)
        {
            UpdateFlashViewerStatus();
            return;
        }

        await DownloadChromiumAsync();
    }

    private async Task<bool> DownloadChromiumAsync()
    {
        var confirm = MessageBox.Show(this,
            "SWF 확인용 Chromium 53.0.2785.0 x86을 GitHub에서 다운로드합니다." + Environment.NewLine +
            "다운로드 크기는 약 98 MB입니다." + Environment.NewLine + Environment.NewLine +
            "Pepper Flash는 포함되지 않으며 x86 pepflashplayer.dll은 직접 넣어야 합니다." + Environment.NewLine + Environment.NewLine +
            "계속하시겠습니까?",
            "Chromium 다운로드",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1);

        if (confirm != DialogResult.Yes)
            return false;

        _flashViewerAction.Enabled = false;
        _flashViewerAction.Text = "다운로드 중...";
        AppendLog("Chromium 53 x86 다운로드 및 설치 시작");

        try
        {
            await LegacyFlashViewer.InstallChromiumAsync();
            UpdateFlashViewerStatus();
            AppendLog("Chromium 53 x86 설치 완료");

            MessageBox.Show(this,
                "Chromium 53 x86 설치가 완료되었습니다." + Environment.NewLine + Environment.NewLine +
                "SWF 보기를 사용하려면 x86 pepflashplayer.dll을 다음 폴더에 직접 넣어주세요." + Environment.NewLine +
                LegacyFlashViewer.PepperDirectory,
                "Chromium 설치 완료",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            UpdateFlashViewerStatus();
            AppendLog($"Chromium 설치 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Chromium 설치 실패",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _flashViewerAction.Enabled = true;
            UpdateFlashViewerStatus();
        }
    }

    private void OpenFlashViewerFolder()
    {
        Directory.CreateDirectory(LegacyFlashViewer.ChromiumDirectory);
        Directory.CreateDirectory(LegacyFlashViewer.PepperDirectory);
        Process.Start(new ProcessStartInfo { FileName = LegacyFlashViewer.ToolDirectory, UseShellExecute = true });
        UpdateFlashViewerStatus();
    }

    private void OpenPepperFlashFolder()
    {
        Directory.CreateDirectory(LegacyFlashViewer.PepperDirectory);
        Process.Start(new ProcessStartInfo { FileName = LegacyFlashViewer.PepperDirectory, UseShellExecute = true });
    }

    private async void OpenSwfViewer()
    {
        UpdateFlashViewerStatus();

        if (!LegacyFlashViewer.HasChromium || !LegacyFlashViewer.IsChromiumX86)
        {
            if (!await DownloadChromiumAsync())
                return;
        }

        if (!LegacyFlashViewer.HasPepperFlash || !LegacyFlashViewer.IsPepperFlashX86)
        {
            var reason = LegacyFlashViewer.HasPepperFlash
                ? "현재 pepflashplayer.dll이 x86 버전이 아닙니다."
                : "x86 pepflashplayer.dll이 없습니다.";

            var openFolder = MessageBox.Show(this,
                reason + Environment.NewLine + Environment.NewLine +
                "다음 폴더에 x86 pepflashplayer.dll을 직접 넣어주세요." + Environment.NewLine +
                LegacyFlashViewer.PepperDirectory + Environment.NewLine + Environment.NewLine +
                "Pepper Flash 폴더를 여시겠습니까?",
                "Pepper Flash 필요",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);

            if (openFolder == DialogResult.Yes)
                OpenPepperFlashFolder();

            UpdateFlashViewerStatus();
            return;
        }

        var swfPath = _lastSwfPath;
        if (string.IsNullOrWhiteSpace(swfPath) || !File.Exists(swfPath))
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "SWF files (*.swf)|*.swf",
                Title = "SWF 파일 선택"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            swfPath = dialog.FileName;
        }

        try
        {
            LegacyFlashViewer.Open(swfPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SWF 보기 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color HexToColor(string? value, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            return ColorTranslator.FromHtml(value);
        }
        catch
        {
            return fallback;
        }
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => AppendLog(message)));
            return;
        }
        _log.AppendText(message + Environment.NewLine);
    }

    private void OpenOutputFolder()
    {
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(_outputFolder.Text))
        {
            folder = _outputFolder.Text.Trim();
        }
        else if (_files.Items.Count > 0)
        {
            var first = (string)_files.Items[0]!;
            var subfolder = _exportSvg.Checked ? "SVG" : _exportPng.Checked ? "PNG" : "SWF";
            folder = Path.Combine(Path.GetDirectoryName(first)!, subfolder);
        }

        if (folder is null || !Directory.Exists(folder))
            return;
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }


}
