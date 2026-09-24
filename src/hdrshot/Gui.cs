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

            var addBtn = new Button { Text = "添加 .jxr 文件", Left = 12, Top = 12, Width = 120 };
            var clearBtn = new Button { Text = "清空列表", Left = 140, Top = 12, Width = 90 };
            var hint = new Label { Text = "也可直接把 .jxr 拖进窗口", Left = 240, Top = 17, Width = 300, ForeColor = Color.DimGray };
            _files.SetBounds(12, 44, 500, 180);
            _files.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;

            var fmtLabel = new Label { Text = "输出格式：", Left = 12, Top = 236, Width = 80 };
            _jpeg.Text = "UltraHDR JPEG（推荐分享）"; _jpeg.Left = 95; _jpeg.Top = 234; _jpeg.Width = 200; _jpeg.Checked = true;
            _png.Text = "HDR PNG"; _png.Left = 300; _png.Top = 234; _png.Width = 90; _png.Checked = true;
            _avif.Text = "HDR AVIF"; _avif.Left = 395; _avif.Top = 234; _avif.Width = 100;

            var outLabel = new Label { Text = "输出目录：", Left = 12, Top = 268, Width = 80 };
            _outDir.SetBounds(95, 264, 410, 24);
            _browse.Text = "浏览…"; _browse.Left = 510; _browse.Top = 263; _browse.Width = 60;

            _convert.Text = "开始转换"; _convert.SetBounds(12, 300, 120, 34);
            _log.SetBounds(12, 344, 700, 165);
            _log.Multiline = true; _log.ScrollBars = ScrollBars.Vertical; _log.ReadOnly = true;
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            Controls.AddRange([addBtn, clearBtn, hint, _files, fmtLabel, _jpeg, _png, _avif, outLabel, _outDir, _browse, _convert, _log]);

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
                var files = _files.Items.Cast<string>().ToList();
                int failures = 0;

                await Task.Run(() =>
                {
                    foreach (string input in files)
                    {
                        try
                        {
                            Program.ConvertOne(input, outDir, png, avif, jpeg, 60, 0, avif ? Program.RequireTool("avifenc.exe", "HDRSHOT_AVIFENC") : null, jpeg ? Program.RequireTool("ultrahdr_app.exe", "HDRSHOT_ULTRAHDR") : null);
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
