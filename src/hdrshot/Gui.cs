using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HdrShot;

/// <summary>
/// Minimal drag-drop GUI: pick .jxr files (or drop them), choose output formats and folder, convert.
/// The CLI remains the primary interface; this is a convenience shell over the same pipeline.
/// </summary>
internal static class Gui
{
    public static int Run()
    {
        Console.WriteLine("[gui] starting");
        ApplicationConfiguration.Initialize();
        Console.WriteLine("[gui] app config done");
        int exit = 2;
        var form = new ConvertForm(() => exit = 0);
        Console.WriteLine("[gui] form constructed");
        if (Environment.GetEnvironmentVariable("HDRSHOT_GUI_SMOKE") == "1")
        {
            // headless self-test: exercise the full construction/layout code, then bail out
            // without showing a window (GUI interaction requires an interactive desktop session)
            form.Dispose();
            Console.WriteLine("[gui] smoke OK (form constructed)");
            return 0;
        }
        Application.Run(form);
        Console.WriteLine($"[gui] message loop exited, exit={exit}");
        return exit;
    }

    private sealed class ConvertForm : Form
    {
        private readonly ListBox _files = new();
        private readonly CheckBox _jpeg = new();
        private readonly CheckBox _png = new();
        private readonly CheckBox _avif = new();
        private readonly NumericUpDown _jpgPeak = new();
        private readonly NumericUpDown _jpgScale = new();
        private readonly TextBox _outDir = new();
        private readonly TextBox _log = new();
        private readonly Button _convert = new();
        private readonly Button _browse = new();
        private readonly Action _onReady;
        private bool _busy;

        public ConvertForm(Action onReady)
        {
            _onReady = onReady;
            Text = "hdrshot — HDR 截图转换";
            Size = new Size(740, 560);
            AllowDrop = true;
            Font = new Font("Microsoft YaHei UI", 9f);

            var addBtn = new Button { Text = "添加 .jxr 文件", Left = 12, Top = 12, AutoSize = true };
            var clearBtn = new Button { Text = "清空列表", Left = 170, Top = 12, AutoSize = true };
            var hint = new Label { Text = "也可直接把 .jxr 拖进窗口", Left = 290, Top = 17, AutoSize = true, ForeColor = Color.DimGray };
            _files.SetBounds(12, 44, 500, 180);
            _files.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;

            var fmtLabel = new Label { Text = "输出格式：", Left = 12, Top = 236, Width = 80 };
            _jpeg.Text = "UltraHDR JPEG（推荐分享）"; _jpeg.Left = 95; _jpeg.Top = 234; _jpeg.AutoSize = true; _jpeg.Checked = true;
            _png.Text = "HDR PNG"; _png.Left = 345; _png.Top = 234; _png.AutoSize = true; _png.Checked = true;
            _avif.Text = "HDR AVIF"; _avif.Left = 470; _avif.Top = 234; _avif.AutoSize = true;

            var peakLabel = new Label { Text = "JPG 峰值：", Left = 12, Top = 268, Width = 80 };
            _jpgPeak.SetBounds(95, 265, 90, 24);
            _jpgPeak.Minimum = 0; _jpgPeak.Maximum = 10000; _jpgPeak.Increment = 50;
            _jpgPeak.Value = (decimal)Program.DefaultJpgPeakNits;
            var peakHint = new Label { Text = "nits（0 = 不压缩，最低 203）", Left = 195, Top = 268, AutoSize = true, ForeColor = Color.DimGray };

            var scaleLabel = new Label { Text = "亮度系数：", Left = 360, Top = 268, Width = 70 };
            _jpgScale.SetBounds(435, 265, 70, 24);
            _jpgScale.DecimalPlaces = 2; _jpgScale.Minimum = 0.10m; _jpgScale.Maximum = 1.00m; _jpgScale.Increment = 0.05m;
            _jpgScale.Value = (decimal)Program.DefaultJpgScale;
            var scaleHint = new Label { Text = "越小越暗", Left = 515, Top = 268, AutoSize = true, ForeColor = Color.DimGray };

            var outLabel = new Label { Text = "输出目录：", Left = 12, Top = 300, Width = 80 };
            _outDir.SetBounds(95, 296, 410, 24);
            _browse.Text = "浏览…"; _browse.Left = 510; _browse.Top = 295; _browse.AutoSize = true;

            _convert.Text = "开始转换"; _convert.SetBounds(12, 332, 120, 34);
            _log.SetBounds(12, 376, 700, 133);
            _log.Multiline = true; _log.ScrollBars = ScrollBars.Vertical; _log.ReadOnly = true;
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            Controls.AddRange([addBtn, clearBtn, hint, _files, fmtLabel, _jpeg, _png, _avif, peakLabel, _jpgPeak, peakHint, scaleLabel, _jpgScale, scaleHint, outLabel, _outDir, _browse, _convert, _log]);

            addBtn.Click += (_, _) =>
            {
                using var dlg = new OpenFileDialog { Filter = "HDR 截图 (*.jxr)|*.jxr|所有文件|*.*", Multiselect = true };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    foreach (var f in dlg.FileNames) AddFile(f);
            };
            clearBtn.Click += (_, _) => _files.Items.Clear();
            _browse.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog();
                if (Directory.Exists(_outDir.Text)) dlg.SelectedPath = _outDir.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) _outDir.Text = dlg.SelectedPath;
            };
            _convert.Click += (_, _) => _ = RunConvertAsync();
            DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
            DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files) AddFile(f);
            };
            FormClosed += (_, _) => _onReady();
        }

        private void AddFile(string path)
        {
            if ((path.EndsWith(".jxr", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".wdp", StringComparison.OrdinalIgnoreCase))
                && !_files.Items.Contains(path))
                _files.Items.Add(path);
        }

        private void Log(string message)
        {
            if (_log.InvokeRequired) _log.BeginInvoke(new Action(() => _log.AppendText(message + Environment.NewLine)));
            else _log.AppendText(message + Environment.NewLine);
        }

        private async Task RunConvertAsync()
        {
            if (_busy) return;
            if (_files.Items.Count == 0) { Log("没有待转换的文件。"); return; }
            if (!_jpeg.Checked && !_png.Checked && !_avif.Checked) { Log("请至少选择一种输出格式。"); return; }

            _busy = true;
            _convert.Enabled = false;
            try
            {
                string? outDir = string.IsNullOrWhiteSpace(_outDir.Text) ? null : _outDir.Text;
                bool jpeg = _jpeg.Checked, png = _png.Checked, avif = _avif.Checked;
                double jpgPeak = (double)_jpgPeak.Value;
                var files = _files.Items.Cast<string>().ToList();
                int failures = 0;

                await Task.Run(() =>
                {
                    foreach (string input in files)
                    {
                        try
                        {
                            Program.ConvertOne(input, outDir, png, avif, jpeg, 60, 0, jpgPeak, (double)_jpgScale.Value, false, avif ? Program.RequireTool("avifenc.exe", "HDRSHOT_AVIFENC") : null, jpeg ? Program.RequireTool("ultrahdr_app.exe", "HDRSHOT_ULTRAHDR") : null);
                            Log($"完成: {Path.GetFileName(input)}");
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            Log($"error [{Path.GetFileName(input)}]: {ex.Message}");
                        }
                    }
                    Log(failures == 0 ? "全部转换成功。" : $"完成，{failures} 个失败。");
                });
            }
            finally
            {
                _busy = false;
                _convert.Enabled = true;
            }
        }
    }
}
