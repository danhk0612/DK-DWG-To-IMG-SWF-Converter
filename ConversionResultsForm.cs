using System.Diagnostics;

namespace DwgToPngPoC;

internal readonly record struct ConversionOutputItem(
    string InputPath,
    string Format,
    string OutputPath);

internal sealed class ConversionResultsForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly Func<string, Task> _openSwf;

    public ConversionResultsForm(
        IReadOnlyList<ConversionOutputItem> results,
        Func<string, Task> openSwf)
    {
        _openSwf = openSwf;

        Text = "변환 결과";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        Width = 1040;
        Height = 480;
        MinimumSize = new Size(760, 360);

        BuildUi(results);
    }

    private void BuildUi(IReadOnlyList<ConversionOutputItem> results)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.CellContentClick += GridCellContentClickAsync;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Input",
            HeaderText = "입력 파일",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 30
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Format",
            HeaderText = "형식",
            Width = 62
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Output",
            HeaderText = "결과 파일",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 55
        });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Preview",
            HeaderText = "",
            Text = "미리보기",
            UseColumnTextForButtonValue = true,
            Width = 82
        });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Folder",
            HeaderText = "",
            Text = "폴더 열기",
            UseColumnTextForButtonValue = true,
            Width = 82
        });

        foreach (var result in results)
        {
            var rowIndex = _grid.Rows.Add(
                Path.GetFileName(result.InputPath),
                result.Format,
                result.OutputPath);
            _grid.Rows[rowIndex].Tag = result;
        }

        root.Controls.Add(_grid, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0)
        };
        var close = new Button
        {
            Text = "닫기",
            Width = 92,
            Height = 30
        };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        root.Controls.Add(buttons, 0, 1);
    }

    private async void GridCellContentClickAsync(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 ||
            _grid.Rows[e.RowIndex].Tag is not ConversionOutputItem item)
        {
            return;
        }

        try
        {
            if (_grid.Columns[e.ColumnIndex].Name == "Preview")
            {
                if (!File.Exists(item.OutputPath))
                {
                    MessageBox.Show(this, "결과 파일이 존재하지 않습니다.", Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (string.Equals(item.Format, "SWF", StringComparison.OrdinalIgnoreCase))
                {
                    await _openSwf(item.OutputPath);
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = item.OutputPath,
                        UseShellExecute = true
                    });
                }
            }
            else if (_grid.Columns[e.ColumnIndex].Name == "Folder")
            {
                var folder = Path.GetDirectoryName(item.OutputPath);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "결과 열기 실패",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
