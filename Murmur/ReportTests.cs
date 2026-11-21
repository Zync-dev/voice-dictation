using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Murmur;

/// <summary>
/// Problem reports: developer checks.
///   --reporttest out.txt   triggers failures against the checks' own test window (a task step, a hands-free click, a
///                          paste that does not land), builds the reports and checks what is in them: the step and plan,
///                          the log, the app and its shape with the labels of its own buttons and tabs, and that nothing
///                          from list rows, documents, the title, a link or a password field gets in; the redaction
///                          checks (keys, email addresses, sizes), the 10-report limit, an excluded app, the setting off,
///                          the Report keycap, the report window (screenshot ticked and not, Save report, Copy summary)
///                          and the Settings list. MURMUR_SHOTS=folder saves the card and the window in both themes.
///   --reporthost           the test window itself (its own process, driven over stdin).
/// Run with MURMUR_DATA_DIR set (and MURMUR_HIDDEN_DESKTOP=1). No AI calls, nothing is pasted or clicked in a real app.
/// </summary>
public static class ReportTests
{
    public static bool TryRun(Application app, string[] args)
    {
        if (args.Length >= 1 && args[0] == "--reporthost") { Host.Run(); return true; }
        if (args.Length == 2 && args[0] == "--reporttest") { _ = FinishAsync(app, args[1]); return true; }
        return false;
    }

    // ---------- what the test window shows (none of it may get into a report) ----------

    private const string Title = "Murmur report test - Pelican Subject Line";
    private static readonly string[] Rows =
    [
        "Unread, Zelda Quartermaine, Wombat invoice 4471, 10:32, Please pay the wombat invoice by Friday",
        "Bartholomew Finchley, Lighthouse keys, Yesterday, The spare lighthouse key is under the mat",
        "Kumquat Weekly, Kumquat prices are up, Mon, Read about kumquats this week",
    ];
    private const string Body = "Secret body text: the vault code is in the blue folder. Write to secret.person@hidden.example before noon.";
    private const string LinkText = "Unsubscribe from Kumquat Weekly";
    private const string SignedIn = "Signed in as private.user@hidden.example";
    private const string Draft = "Secret draft about the Pelican merger for Bartholomew";

    /// <summary>Words that must never be in a report (the user said "Zelda", so that name may be).</summary>
    private static readonly string[] Secrets =
    [
        "Quartermaine", "Wombat", "wombat", "Lighthouse", "lighthouse", "Kumquat", "kumquat", "vault code", "blue folder", "secret.person", "private.user",
        "hidden.example", "Pelican", "Bartholomew", "Finchley", "hunter2", "Unsubscribe", "sk-test", "someone@private", "gsk_", "Signed in as",
    ];

    private static class Host
    {
        public static void Run()
        {
            var app = Application.Current;
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            foreach (var b in new[] { "Archive", "Move to", "Reply" }) toolbar.Children.Add(new Button { Content = b, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) });
            var search = new TextBox { Width = 220, Margin = new Thickness(8, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(search, "Search");
            toolbar.Children.Add(search);
            var tabs = new TabControl { Margin = new Thickness(8, 0, 8, 0), Height = 170 };
            var list = new ListBox();
            foreach (var r in Rows) list.Items.Add(new ListBoxItem { Content = r });
            tabs.Items.Add(new TabItem { Header = "Focused", Content = list });
            tabs.Items.Add(new TabItem { Header = "Other", Content = new TextBlock { Text = "Nothing here" } });
            var body = new RichTextBox { IsReadOnly = true, Height = 110, Margin = new Thickness(8) };
            body.Document.Blocks.Clear();
            var para = new Paragraph(new Run(Body + " "));
            para.Inlines.Add(new Hyperlink(new Run(LinkText)) { NavigateUri = new Uri("https://example.invalid/") });
            body.Document.Blocks.Add(para);
            var password = new PasswordBox { Password = "hunter2", Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 8, 8) };
            var root = new StackPanel();
            root.Children.Add(toolbar);
            root.Children.Add(tabs);
            root.Children.Add(body);
            root.Children.Add(new TextBlock { Text = SignedIn, Margin = new Thickness(8, 0, 8, 4) });
            root.Children.Add(password);
            var window = new Window { Title = Title, Width = 760, Height = 470, Left = 80, Top = 80, WindowStartupLocation = WindowStartupLocation.Manual, Content = root, Background = Brushes.White };
            window.Show();
            Console.Out.WriteLine("ready " + new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64());
            Console.Out.Flush();
            var stdin = new Thread(() =>
            {
                string? line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    string cmd = line.Trim();
                    string answer = app.Dispatcher.Invoke(() => cmd switch
                    {
                        "state" => $"{{\"selected\":{list.SelectedIndex}}}",
                        "quit" => "bye",
                        _ => "?",
                    });
                    Console.Out.WriteLine(answer);
                    Console.Out.Flush();
                    if (cmd == "quit") { app.Dispatcher.Invoke(() => app.Shutdown()); return; }
                }
                app.Dispatcher.Invoke(() => app.Shutdown());
            }) { IsBackground = true };
            stdin.Start();
        }
    }

    // ---------- the run ----------

    private static int _pass, _fail;
    private static readonly List<string> Lines = [];

    private static void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Lines.Add((ok ? "ok    " : "FAIL  ") + what);
    }

    private static async Task FinishAsync(Application app, string outPath)
    {
        try { await RunAsync(app); }
        catch (Exception ex) { Lines.Add("FAILED " + ex); _fail++; }
        finally
        {
            TaskPlanner.TestPlan = null;
            TaskRunner.NoAiPick = false;
            AppCatalog.FakeRunning = AppCatalog.FakeInstalled = null;
            ProblemReportWindow.TestMode = false;
        }
        Lines.Insert(0, $"{_pass} passed, {_fail} failed");
        File.WriteAllLines(outPath, Lines);
        app.Shutdown(_fail == 0 ? 0 : 1);
    }

    private static async Task<bool> Until(Func<bool> condition, int ms)
    {
        var w = Stopwatch.StartNew();
        while (w.ElapsedMilliseconds < ms) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private static string Leaks(string text) => string.Join(", ", Secrets.Where(s => text.Contains(s, StringComparison.Ordinal)));

    private static async Task RunAsync(Application app)
    {
        if (!AppSettings.IsTestInstance) { Check(false, "run with MURMUR_DATA_DIR set to a test data folder"); return; }
        string shots = Environment.GetEnvironmentVariable("MURMUR_SHOTS") ?? Path.Combine(AppSettings.DataDir, "shots", "report");
        Directory.CreateDirectory(shots);
        Theme.Init(app, "night");
        ProblemReportWindow.TestMode = true;
        try { if (Directory.Exists(ProblemReports.Folder)) Directory.Delete(ProblemReports.Folder, true); } catch { }
        try { if (Directory.Exists(ProblemReports.SaveFolder)) Directory.Delete(ProblemReports.SaveFolder, true); } catch { }
        Lines.Add($"hidden desktop: {HiddenDesktop.IsActive}; reports in {ProblemReports.Folder}; zips in {ProblemReports.SaveFolder}");

        Redaction();

        var settings = AppSettings.Load(); // the test folder's settings; never saved here
        settings.HandsFreeIntents = settings.ControlCommands = settings.TasksEnabled = settings.ProblemReportsKeep = true;
        settings.HandsFreeAiRouter = false;
        settings.ScreenVision = false;
        settings.ConfirmSeconds = 20;
        settings.TaskMaxSteps = 8;
        AppCatalog.FakeRunning = [];
        AppCatalog.FakeInstalled = [];
        TaskRunner.NoAiPick = true;

        var llm = new LlmClient(settings);
        var processor = new TextProcessor(settings, llm);
        var overlay = new OverlayWindow();
        overlay.Start(false);
        var hf = new HandsFree(settings, processor, llm, overlay);
        overlay.ActionRequested += action => { if (action.StartsWith("confirm-")) hf.OnCardAction(action); };

        var host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--reporthost") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        var ready = (await host.StandardOutput.ReadLineAsync() ?? "").Split(' ');
        var hwnd = (IntPtr)long.Parse(ready[1]);
        Lines.Add($"test window: pid {host.Id}");
        await Task.Delay(800);
        try
        {
            // ===== 1. a task step fails =====
            Lines.Add("== a task step fails ==");
            TaskPlanner.TestPlan = _ => new TaskPlan
            {
                Steps =
                [
                    new TaskStep("open_item", new Dictionary<string, string> { ["description"] = "mailen fra Zelda" }),
                    new TaskStep("click", new Dictionary<string, string> { ["target"] = "Kaffemaskine" }),
                ],
            };
            await hf.TryHandleAsync("Find mailen fra Zelda og klik på Kaffemaskine", "", "da", hwnd);
            var run = TaskRun.Current;
            // What a log line could carry in a real run: a row's text, a key and an address, none of them the user's words.
            Log.Write($"Task: open_item looked at “{Rows[1]}” and a key sk-test1234567890abcdefXYZ, gsk_ABCDEFGHIJKLMNOPQRSTUV, mail someone@private.example");
            Log.Write($"Report check: unquoted row text {Rows[2].Split(", ")[1]} and the title {Title}");
            await hf.TryHandleAsync("Kør", "", "da", hwnd);
            if (run != null) await Task.WhenAny(run.Completion, Task.Delay(40000));
            Check(run?.Outcome == "failed", $"the task failed at step {run?.FailedStep} [{string.Join(" | ", run?.Log ?? [])}]");
            await Task.Delay(200);
            var task = overlay.CardReport;
            Check(task != null && overlay.ReportKeyShown && overlay.StateName == "Task", $"the failed card shows Report next to Esc [{overlay.CardText}]");
            if (task != null)
            {
                await Task.WhenAny(task.Ready, Task.Delay(15000));
                Check(task.Ready.IsCompleted, "the report was gathered within the time limit");
                CheckReport(task, "task", run?.Request.Spoken ?? "");
                Check(task.FailedTool == "click" && task.Plan.Count == 2 && task.Plan[1].Contains("Kaffemaskine") && task.Plan[1].Contains("FAILED") && task.Plan[0].Contains("Zelda"),
                    $"the plan with its arguments and the failed step: {string.Join(" · ", task.Plan)}");
                Check(task.LogLines.Any(l => l.Contains("Task step 2/2 click")) && task.LogLines.Any(l => l.Contains("‹key›")) && task.LogLines.Any(l => l.Contains("‹email›")),
                    $"the run's log lines are there, keys and the address taken out ({task.LogLines.Count} lines)");
                Check(task.LogLines.Any(l => l.Contains("chars›")) && task.LogLines.Any(l => l.Contains("‹hidden›")),
                    "a quoted row and unquoted window text in the log are taken out");
                Check(File.Exists(Path.Combine(ProblemReports.Folder, $"report-{task.Id}.json")), "the report is kept in the reports folder");
                foreach (var l in task.LogLines.Take(40)) Lines.Add("        log: " + l);
            }

            // ===== 2. a hands-free click finds nothing =====
            Lines.Add("== a hands-free command fails ==");
            await hf.TryHandleAsync("Klik på Kaffemaskine", "", "da", hwnd);
            await Until(() => overlay.StateName == "Error", 8000);
            var command = overlay.CardReport;
            Check(command != null && overlay.ReportKeyShown && overlay.StateName == "Error", $"the error card shows Report and Esc [{overlay.CardText}] (route {hf.LastRoute})");
            if (command != null)
            {
                await Task.WhenAny(command.Ready, Task.Delay(15000));
                CheckReport(command, "command", "Klik på Kaffemaskine");
            }

            // ===== 3. a paste that does not land =====
            Lines.Add("== a paste that does not land ==");
            if (ScreenReader.PidOf(Native.GetForegroundWindow()) == host.Id)
                Lines.Add("skip  the test window is in front, so a paste could reach the clipboard; not tried");
            else
            {
                var outcome = await hf.PasteAsync(Draft, "Reply pasted, not sent");
                await Task.Delay(200);
                var paste = overlay.CardReport;
                Check(outcome != PasteOutcome.Pasted && paste != null && overlay.ReportKeyShown && overlay.StateName == "Answer", $"“Not pasted” shows Report ({outcome}) [{overlay.CardText}]");
                if (paste != null)
                {
                    await Task.WhenAny(paste.Ready, Task.Delay(15000));
                    CheckReport(paste, "paste", "Klik på Kaffemaskine");
                    Check(!paste.ToText().Contains("Secret draft"), "the text that was not pasted is not in the report");
                }
            }

            // ===== 4. an excluded app, and the setting off =====
            Lines.Add("== an excluded app; the setting off ==");
            var excludedSettings = AppSettings.Load();
            excludedSettings.ExcludedApps = "Murmur";
            var excluded = ProblemReports.ForCommand(excludedSettings, "command", "Test", "Excluded app check.", "klik på Archive", "click", hwnd, DateTime.Now, null);
            if (excluded != null) await Task.WhenAny(excluded.Ready, Task.Delay(10000));
            Check(excluded != null && excluded.Shape.Length == 0 && excluded.ShapeSummary.Contains("not read") && excluded.Window == IntPtr.Zero && await ProblemReports.ScreenshotAsync(excluded, excludedSettings) == null,
                $"an excluded app's window is not read and cannot be pictured: {excluded?.ShapeSummary}");
            var off = AppSettings.Load();
            off.ProblemReportsKeep = false;
            Check(ProblemReports.ForCommand(off, "command", "Test", "Off.", "", "click", hwnd, DateTime.Now, null) == null, "with the setting off no report is made");

            // ===== 5. the card's Report keycap and the report window, in both themes =====
            Lines.Add("== the Report keycap and the report window ==");
            if (task != null)
            {
                foreach (var theme in new[] { "night", "paper" })
                {
                    Theme.Init(app, theme);
                    await Task.Delay(200);
                    overlay.OfferReport(task);
                    overlay.ShowTask("Step 2 of 2 failed. No button or link called “Kaffemaskine” in Murmur.",
                        [("Open “mailen fra Zelda”", TaskStepState.Done), ("Click “Kaffemaskine”", TaskStepState.Failed)], final: true, failed: true, closeSeconds: 60);
                    await Task.Delay(900);
                    await ShootCardAsync(overlay, Path.Combine(shots, $"card-report-{theme}.png"));
                    var key = overlay.FindName("TaskReportKey") as FrameworkElement;
                    key?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    var w = ProblemReportWindow.Current;
                    Check(w != null && w.Report == task && overlay.StateName is "Hidden" or "Rest", $"{theme}: the Report keycap opens the report window and the card goes");
                    if (w == null) continue;
                    await w.Filled;
                    await Task.Delay(700);
                    await ShootWindowAsync(w, Path.Combine(shots, $"dialog-{theme}.png"));
                    w.ShowAll(true);
                    await w.SetScreenshotAsync(true);
                    w.ScrollToEnd();
                    await Task.Delay(700);
                    Check(w.Screenshot is { Length: > 1000 }, $"{theme}: ticking “Include a screenshot” takes a picture of the app's window and shows it ({w.Screenshot?.Length ?? 0} bytes)");
                    await ShootWindowAsync(w, Path.Combine(shots, $"dialog-{theme}-expanded.png"));
                    if (w.Screenshot != null) File.WriteAllBytes(Path.Combine(shots, $"included-screenshot-{theme}.png"), w.Screenshot);
                    if (theme == "night")
                    {
                        var withShot = w.Save();
                        CheckZip(withShot, expectScreenshot: true);
                        await w.SetScreenshotAsync(false);
                        var without = w.Save();
                        CheckZip(without, expectScreenshot: false);
                        Check(Path.GetFileName(without ?? "").StartsWith("Murmur-report-" + task.Time.ToString("yyyy-MM-dd"), StringComparison.Ordinal) && Path.GetDirectoryName(without) == ProblemReports.SaveFolder,
                            $"saved as {Path.GetFileName(without)} on the (test) Desktop");
                        w.CopySummary();
                        string summary = w.LastSummary;
                        Check(summary.Contains("Step 2 of 2 failed") && summary.Contains("Kaffemaskine") && summary.Length < 1500 && Leaks(summary).Length == 0
                              && ProblemReports.Check(summary, task.Spoken + "\n" + string.Join("\n", task.Plan), task.Hidden).Count == 0,
                            $"Copy summary: a short text ({summary.Length} chars), nothing from the window");
                        foreach (var l in summary.Split('\n')) Lines.Add("        summary: " + l.TrimEnd());
                    }
                    w.Close();
                }
                Theme.Init(app, "night");
            }

            // ===== 6. at most 10 reports =====
            Lines.Add("== at most 10 reports ==");
            var start = DateTime.Now.AddMinutes(1);
            for (int i = 0; i < 13; i++)
            {
                var t = start.AddSeconds(i);
                ProblemReports.Save(new ProblemReport { Id = t.ToString("yyyyMMdd-HHmmss-fff"), Time = t, Kind = "task", Headline = $"Step {i % 3 + 1} of 3 failed.", What = $"Test report {i}.", AppName = "Murmur report test" });
            }
            var kept = ProblemReports.List();
            Check(kept.Count == ProblemReports.Keep && kept[0].What == "Test report 12." && kept[^1].What == "Test report 3.", $"{kept.Count} reports kept, the newest first ({kept.FirstOrDefault()?.What} … {kept.LastOrDefault()?.What})");
            ProblemReports.Delete(kept[^1].Id);
            Check(ProblemReports.List().Count == ProblemReports.Keep - 1, "Delete removes one report");

            // ===== 7. the Settings list, in both themes =====
            Lines.Add("== Settings › Problem reports ==");
            foreach (var theme in new[] { "night", "paper" })
            {
                Theme.Init(app, theme);
                var panel = new StackPanel();
                ProblemReportList.Fill(panel);
                Check(panel.Children.Count == ProblemReports.Keep - 1, $"{theme}: the list shows {panel.Children.Count} reports with Open and Delete");
                var card = new Border { Child = panel, Margin = new Thickness(24) };
                card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
                var window = new Window { Title = "Problem reports", Width = 720, Height = 640, Content = card, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Ground");
                window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Ink");
                window.Show();
                await Task.Delay(600);
                await ShootWindowAsync(window, Path.Combine(shots, $"settings-{theme}.png"));
                // Open from the list opens the report window.
                var open = panel.Children.OfType<Grid>().First().Children.OfType<StackPanel>().Last().Children.OfType<Button>().First();
                open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(ProblemReportWindow.Current?.Report.What == "Test report 12.", $"{theme}: Open shows the newest report");
                ProblemReportWindow.Current?.Close();
                window.Close();
            }
            Theme.Init(app, "night");
        }
        finally
        {
            try { await host.StandardInput.WriteLineAsync("quit"); await host.StandardInput.FlushAsync(); } catch { }
            if (!host.WaitForExit(4000)) try { host.Kill(); } catch { }
            overlay.Close();
        }
    }

    /// <summary>The checks every report gets: what it holds, what it never holds, and its size.</summary>
    private static void CheckReport(ProblemReport r, string kind, string spoken)
    {
        string text = r.ToText();
        string json = System.Text.Json.JsonSerializer.Serialize(r);
        string words = r.Spoken + "\n" + string.Join("\n", r.Plan);
        Check(r.Kind == kind && r.Spoken == spoken && r.Headline.Length > 0 && r.What.Length > 0, $"{kind}: what went wrong [{r.Headline} {r.What}] and the words [{r.Spoken}]");
        Check(r.Process.Equals("Murmur", StringComparison.OrdinalIgnoreCase) && r.WindowClass.StartsWith("HwndWrapper", StringComparison.Ordinal) && r.Profile.Contains("WPF") && r.Profile.Contains("not a mail app"),
            $"{kind}: the app [{r.AppName}, {r.Process}, {r.WindowClass}] {r.Profile}");
        Check(r.MurmurVersion.Length > 0 && r.Windows.StartsWith("Windows") && r.Ai.Length > 0 && r.Speech.Length > 0, $"{kind}: Murmur {r.MurmurVersion}, {r.Windows}, AI {r.Ai}, speech {r.Speech}");
        Check(r.Shape.Contains("\"Archive\"") && r.Shape.Contains("\"Other\"") && r.Shape.Contains("\"Focused\"") && r.Shape.Contains("\"Search\"") && r.Shape.Contains("ListItem") && r.Shape.Contains("Document") && r.Shape.Contains("PASSWORD"),
            $"{kind}: the shape has the labels of the app's own buttons, tabs and field, and the list, the document and the password field without text ({r.ShapeSummary})");
        string leaks = Leaks(text + json);
        Check(leaks.Length == 0, $"{kind}: nothing from rows, the document, the title, the link, the password or the injected log line [{leaks}]");
        var problems = ProblemReports.Check(text, words, r.Hidden);
        var jsonProblems = ProblemReports.Check(json, words, r.Hidden, maxChars: ProblemReports.MaxReportChars * 2);
        Check(problems.Count == 0 && jsonProblems.Count == 0 && r.Hidden.Count > 0, $"{kind}: the redaction check passes ({string.Join("; ", problems.Concat(jsonProblems))}; {r.Hidden.Count} window names checked)");
        Check(text.Length < ProblemReports.MaxReportChars && r.LogLines.All(l => l.Length <= ProblemReports.MaxLineChars + 9) && r.LogLines.Count <= ProblemReports.MaxLogLines,
            $"{kind}: sizes are capped ({text.Length} chars, {r.LogLines.Count} log lines)");
    }

    private static void CheckZip(string? path, bool expectScreenshot)
    {
        if (path == null || !File.Exists(path)) { Check(false, "the zip was saved"); return; }
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        string text;
        using (var reader = new StreamReader(zip.GetEntry("report.txt")!.Open(), Encoding.UTF8)) text = reader.ReadToEnd();
        string json;
        using (var reader = new StreamReader(zip.GetEntry("report.json")!.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
        bool shot = names.Contains("screenshot.png");
        Check(names.Contains("report.txt") && names.Contains("report.json") && shot == expectScreenshot && Leaks(text + json).Length == 0,
            $"zip {Path.GetFileName(path)}: {string.Join(", ", names)} ({new FileInfo(path).Length / 1024} KB); the text holds nothing from the window");
    }

    // ---------- pictures (PrintWindow on the hidden desktop; a render of the window when that draws nothing) ----------

    private static async Task ShootWindowAsync(Window w, string path)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var picture = await ScreenReader.CaptureAsync(hwnd);
            // The title bar is drawn by Windows: only the window's own content counts.
            if (picture != null && picture.Height > 120)
            {
                using var client = picture.Clone(new System.Drawing.Rectangle(0, 60, picture.Width, picture.Height - 60), picture.PixelFormat);
                if (!Blank(client)) { picture.Save(path, System.Drawing.Imaging.ImageFormat.Png); Lines.Add($"      shot {Path.GetFileName(path)} (PrintWindow {picture.Width}x{picture.Height}{(attempt > 0 ? ", drawn in software" : "")})"); return; }
            }
            // A hidden desktop has no GPU presentation for PrintWindow to copy: draw this window in software and try again.
            if (System.Windows.Interop.HwndSource.FromHwnd(hwnd) is { } source) source.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            w.InvalidateVisual();
            await Task.Delay(500);
        }
        Render(w.Content as FrameworkElement, path, "render, PrintWindow drew nothing");
    }

    private static async Task ShootCardAsync(OverlayWindow overlay, string path)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
        using var picture = await ScreenReader.CaptureAsync(hwnd);
        if (picture != null && !Blank(picture) && overlay.FindName("Pill") is FrameworkElement pill)
        {
            // Only the card: its bounds in the window, in device pixels.
            var source = PresentationSource.FromVisual(overlay);
            double sx = source?.CompositionTarget.TransformToDevice.M11 ?? 1, sy = source?.CompositionTarget.TransformToDevice.M22 ?? 1;
            var at = pill.TransformToAncestor(overlay).Transform(new Point(0, 0));
            var crop = new System.Drawing.Rectangle((int)(at.X * sx) - 24, (int)(at.Y * sy) - 24, (int)(pill.ActualWidth * sx) + 48, (int)(pill.ActualHeight * sy) + 48);
            crop.Intersect(new System.Drawing.Rectangle(0, 0, picture.Width, picture.Height));
            if (crop.Width > 40 && crop.Height > 20)
            {
                using var part = picture.Clone(crop, picture.PixelFormat);
                if (!Blank(part)) { part.Save(path, System.Drawing.Imaging.ImageFormat.Png); Lines.Add($"      shot {Path.GetFileName(path)} (PrintWindow, cropped to the card {crop.Width}x{crop.Height})"); return; }
            }
        }
        Render(overlay.Content as FrameworkElement, path, "render, PrintWindow drew nothing");
    }

    private static void Render(FrameworkElement? e, string path, string how)
    {
        if (e == null) return;
        var bitmap = new RenderTargetBitmap((int)Math.Max(1, e.ActualWidth * 2), (int)Math.Max(1, e.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(e);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
        Lines.Add($"      shot {Path.GetFileName(path)} ({how})");
    }

    private static bool Blank(System.Drawing.Bitmap bmp)
    {
        var first = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        int same = 0, total = 0;
        for (int y = 2; y < bmp.Height; y += Math.Max(1, bmp.Height / 30))
        for (int x = 2; x < bmp.Width; x += Math.Max(1, bmp.Width / 30))
        {
            total++;
            if (bmp.GetPixel(x, y).ToArgb() == first.ToArgb()) same++;
        }
        return total == 0 || same > total * 0.985;
    }

    // ---------- redaction checks (no window needed) ----------

    private static void Redaction()
    {
        Lines.Add("== redaction ==");
        string raw = "keys: sk-abcdefghijklmnop1234 sk-ant-api03-xyzxyzxyz_1 gsk_ABCDEFGHIJKLMNOPQRST AIzaSyA1234567890abcdefghijklmnop Authorization: Bearer abc.def.ghi123 x-api-key: 0123456789abcdefgh";
        var before = ProblemReports.Check(raw, "");
        string clean = ProblemReports.Redact(raw, "");
        var after = ProblemReports.Check(clean, "");
        int marks = (clean.Length - clean.Replace("‹key›", "").Length) / "‹key›".Length;
        Check(before.Count >= 5 && after.Count == 0 && marks >= 6 && !clean.Contains("abcdefghijklmnop") && !clean.Contains("ABCDEFGHIJ") && !clean.Contains("AIza"),
            $"keys (sk-, sk-ant-, gsk_, AIza…, Bearer, x-api-key) are found ({before.Count}) and taken out ({marks}): [{clean}]");
        string mails = ProblemReports.Redact("to lukas@example.com and other.person@corp.example", "skriv til lukas@example.com at jeg kommer");
        Check(mails.Contains("lukas@example.com") && !mails.Contains("other.person") && mails.Contains("‹email›") && ProblemReports.Check(mails, "skriv til lukas@example.com").Count == 0
              && ProblemReports.Check("write to other.person@corp.example", "").Count == 1,
            $"an address the user said stays, another is taken out: [{mails}]");
        Check(ProblemReports.Check(new string('x', ProblemReports.MaxReportChars + 1), "").Count == 1 && ProblemReports.Check("short", "").Count == 0, "a report over the size cap fails the check");
        Check(ProblemReports.Check("the row Zelda Quartermaine invoice", "", ["Zelda Quartermaine invoice"]).Count == 1, "text seen in the window fails the check");
        int q = 0, h = 0;
        string corpus = ItemFinder.Fold("find mailen fra zelda\nclick (target: Kaffemaskine)");
        string line = ProblemReports.SanitizeLine("Task: open_item found “Unread, Zelda Quartermaine, Wombat invoice” no button “Kaffemaskine” in C:\\Users\\" + Environment.UserName + "\\x", corpus, ["Wombat invoice"], ref q, ref h);
        Check(line.Contains("“Kaffemaskine”") && !line.Contains("Quartermaine") && line.Contains("chars›") && line.Contains("%USERPROFILE%") && q == 1,
            $"a log line keeps the user's quoted words and loses the rest: [{line}]");
        Check(ProblemReports.SanitizeLine(new string('a', 5000), "", [], ref q, ref h).Length <= ProblemReports.MaxLineChars, "a long log line is cut");
    }
}
