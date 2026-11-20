using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using Forms = System.Windows.Forms;

namespace Murmur;

/// <summary>
/// Developer checks for voice editing and read aloud (see README):
///   --edittest out.txt          the phrase table, the planner, and every edit command in its own test windows (a WPF text
///                               box, a WinForms text box, a rich edit box, a password box; another process, --edithost) and
///                               in a browser text area (Edge with a throw-away profile), each result checked exactly
///   --edithost                  the test windows themselves (driven over stdin by --edittest)
///   --speaktest folder out.txt  the voices Windows has, Danish and English samples synthesised to WAV files in the folder
///                               (nothing is played), timings, and the read-aloud card driven by a silent player
///   --editspeak-demo[=folder]   plays the new card states; with a folder, saves each as a PNG and quits
/// Nothing here reads, clicks or types into any window other than the ones these checks open.
/// </summary>
public static class EditSpeakTests
{
    public static bool TryRun(Application app, string[] args)
    {
        if (args.Length >= 1 && args[0] == "--edithost") { EditHost.Run(); return true; }
        if (args.Length == 2 && args[0] == "--edittest") { _ = Finish(app, args[1], EditAsync); return true; }
        if (args.Length == 3 && args[0] == "--speaktest") { _ = Finish(app, args[2], lines => SpeakAsync(lines, args[1])); return true; }
        if (args.Length == 1 && args[0].StartsWith("--editspeak-demo"))
        {
            Theme.Init(app);
            var overlay = new OverlayWindow();
            overlay.Start(false);
            _ = DemoAsync(overlay, args[0].Length > 17 ? args[0][17..] : null);
            return true;
        }
        return false;
    }

    private static int _pass, _fail;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    private static async Task Finish(Application app, string outPath, Func<List<string>, Task> test)
    {
        var lines = new List<string>();
        _pass = _fail = 0;
        try { await test(lines); }
        catch (Exception ex) { lines.Add("FAILED " + ex); _fail++; }
        lines.Insert(0, $"{_pass} passed, {_fail} failed");
        File.WriteAllLines(outPath, lines);
        app.Shutdown(_fail == 0 ? 0 : 1);
    }

    private static void Check(List<string> lines, bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        lines.Add((ok ? "ok    " : "FAIL  ") + what);
    }

    private static string Show(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    // ---------- the phrase table ----------

    /// <summary>(phrase, selection, expected): an EditCommand's ToString(), "read_aloud", "read_control:stop" … or another intent id / "rewrite".</summary>
    internal static readonly (string Phrase, string Selection, string Expected)[] Phrases =
    [
        ("Ret tirsdag til onsdag", "", "replace “tirsdag” → “onsdag”"),
        ("Ret tirsdag til onsdag.", "", "replace “tirsdag” → “onsdag”"),
        ("Kan du rette kl. 10 til kl. 11?", "", "replace “kl. 10” → “kl. 11”"),
        ("Skift Peter ud med Mette", "", "replace “Peter” → “Mette”"),
        ("Erstat møde med frokost", "", "replace “møde” → “frokost”"),
        ("Lav fredag om til lørdag", "", "replace “fredag” → “lørdag”"),
        ("Change Tuesday to Wednesday.", "", "replace “Tuesday” → “Wednesday”"),
        ("Replace Tuesday with Wednesday", "", "replace “Tuesday” → “Wednesday”"),
        ("Could you change the date to the 5th?", "", "replace “the date” → “the 5th”"),
        ("Marker sidste sætning", "", "select 1 Sentence"),
        ("Markér det sidste ord.", "", "select 1 Word"),
        ("Marker sidste afsnit", "", "select 1 Paragraph"),
        ("Marker de sidste tre ord", "", "select 3 Word"),
        ("Marker alt", "", "select 1 All"),
        ("Select the last sentence.", "", "select 1 Sentence"),
        ("Select the last word", "", "select 1 Word"),
        ("Select the last paragraph", "", "select 1 Paragraph"),
        ("Select the last two words", "", "select 2 Word"),
        ("Slet sidste sætning", "", "delete 1 Sentence"),
        ("Slet det sidste ord", "", "delete 1 Word"),
        ("Slet de sidste to ord", "", "delete 2 Word"),
        ("Slet sidste afsnit.", "", "delete 1 Paragraph"),
        ("Delete the last sentence.", "", "delete 1 Sentence"),
        ("Delete the last word", "", "delete 1 Word"),
        ("Delete that", "", "delete 1 That"),
        ("Slet det", "noget tekst", "delete 1 That"),
        ("Gør det mere høfligt", "", "rewrite That (gør det mere høfligt)"),
        ("Gør det kortere.", "", "rewrite That (gør det kortere)"),
        ("Gør den lidt mere formel", "", "rewrite That (gør den lidt mere formel)"),
        ("Make it more polite.", "", "rewrite That (make it more polite)"),
        ("Make it shorter", "", "rewrite That (make it shorter)"),
        ("Make that more formal", "", "rewrite That (make that more formal)"),
        ("Store bogstaver", "", "case Upper of That"),
        ("Med store bogstaver", "", "rewrite"),
        ("Skriv det med store bogstaver", "", "case Upper of That"),
        ("Sidste ord med store bogstaver", "", "case Upper of Word"),
        ("Små bogstaver", "", "case Lower of That"),
        ("Capitalize that.", "", "case Upper of That"),
        ("All caps", "", "case Upper of That"),
        ("Make it uppercase", "", "case Upper of That"),
        ("Lowercase that", "", "case Lower of That"),
        ("Title case", "", "case Title of That"),
        ("Flyt markøren til slutningen", "", "move End"),
        ("Gå til slutningen af teksten.", "", "move End"),
        ("Flyt markøren til starten", "", "move Start"),
        ("Gå til slutningen af linjen", "", "move LineEnd"),
        ("Go to the end.", "", "move End"),
        ("Move the cursor to the beginning", "", "move Start"),
        ("Go to the end of the line", "", "move LineEnd"),
        // read aloud
        ("Læs op", "", "read_aloud"),
        ("Læs det her op.", "", "read_aloud"),
        ("Læs mailen op", "", "read_aloud"),
        ("Kan du læse mailen højt for mig?", "", "read_aloud"),
        ("Oplæs teksten", "", "read_aloud"),
        ("Read this aloud.", "", "read_aloud"),
        ("Read it out loud", "", "read_aloud"),
        ("Read the email", "", "read_aloud"),
        ("Read me the email, please", "", "read_aloud"),
        ("Read aloud", "", "read_aloud"),
        ("Stop reading", "", "read_control:stop"),
        ("Stop med at læse", "", "read_control:stop"),
        ("Læs langsommere", "", "read_control:slower"),
        ("Læs lidt hurtigere", "", "read_control:faster"),
        ("Read faster", "", "read_control:faster"),
        ("Slower, please", "", "read_control:slower"),
        // must stay what they were
        ("Read the email and tell me what they want", "", "rewrite"),
        ("Stop", "", "rewrite"),                                    // nothing is being read
        ("Gør det her mere formelt", "Hej, vi ses", "rewrite"),     // a selection: command mode as before
        ("Make this shorter", "long text", "rewrite"),
        ("Kan du gøre det her kortere?", "lang tekst", "rewrite"),
        ("Ret stavefejlene", "Det er en fejl", "rewrite"),
        ("Fix the grammar", "he go home", "rewrite"),
        ("Lav det om til punktform", "a, b og c", "rewrite"),
        ("Skift sproget til engelsk", "Hej", "rewrite"),
        ("Change this to English", "Hej", "rewrite"),
        ("Change the tone to formal", "hey there", "rewrite"),
        ("Ret det her til en mail", "", "rewrite"),
        ("Oversæt det her til engelsk", "Hej", "rewrite"),
        ("Turn this into a bulleted list", "a, b and c", "rewrite"),
        ("Skriv en mail til Peter om at mødet er flyttet til fredag", "", "rewrite"),
        ("Write an email to Peter saying the meeting moved to Friday", "", "rewrite"),
        ("Lav en liste over ting jeg skal købe", "", "rewrite"),
        ("Hvad betyder det her?", "Carpe diem", "screen_question"),
        ("Svar på mailen og sig ja tak til torsdag", "", "reply"),
        ("Opsummer det her.", "", "summarize"),
        ("Ret tirsdag til onsdag", "Vi ses på mandag", "rewrite"),  // the word is not in the selection
        ("Ret tirsdag til onsdag", "Vi ses på tirsdag", "replace “tirsdag” → “onsdag”"),
    ];

    private static string Route(IntentRequest request)
    {
        var m = IntentRouter.MatchRules(request);
        if (m == null) return "rewrite";
        if (m.Intent is VoiceEditIntent) return VoiceEditIntent.FromArgs(m) is { } c ? c.Action == EditAction.Rewrite ? $"rewrite That ({request.Normalized})" : c.ToString() : "edit:?";
        if (m.Intent is ReadControlIntent) return "read_control:" + m.Arg("action");
        return m.Intent.Id;
    }

    private static void PhraseTable(List<string> lines, AppSettings settings)
    {
        lines.Add("-- phrases (rules, no AI) --");
        var app = new ActiveWindowInfo("notepad", "Untitled - Notepad");
        foreach (var (phrase, selection, expected) in Phrases)
        {
            string got = Route(IntentRequest.From(phrase, selection, null, app, settings));
            Check(lines, got == expected, $"[{phrase}]{(selection.Length > 0 ? $" sel “{selection}”" : "")} → {got}{(got == expected ? "" : $" (expected {expected})")}");
        }
        // Nothing the foundation's table expects may have changed.
        int same = 0;
        var changed = new List<string>();
        foreach (var (phrase, selection, pending, expected) in HandsFreeTests.RouterCases)
        {
            var request = new IntentRequest
            {
                Spoken = phrase, Normalized = IntentRules.Normalize(phrase), Selection = selection, App = app, Settings = settings,
                PendingConfirmation = pending ? "Send this reply?" : null,
            };
            var m = IntentRouter.MatchRules(request);
            string got = m == null ? "rewrite" : m.Intent.Id == "confirm" ? "confirm:" + m.Arg("answer") : m.Intent.Id;
            if (got == expected) same++; else changed.Add($"{phrase} → {got} (expected {expected})");
        }
        Check(lines, changed.Count == 0, $"the hands-free foundation's {HandsFreeTests.RouterCases.Length} phrases route as before: {same} same" + (changed.Count > 0 ? "; changed: " + string.Join("; ", changed) : ""));
        // Off in Settings: nothing is taken.
        var off = new AppSettings { VoiceEditing = false, ReadAloudEnabled = false };
        Check(lines, Route(IntentRequest.From("Ret tirsdag til onsdag", "", null, app, off)) == "rewrite" && Route(IntentRequest.From("Læs op", "", null, app, off)) == "rewrite",
              "with voice editing and read aloud off, their phrases are ordinary commands");
        var watch = Stopwatch.StartNew();
        int n = 0;
        for (int round = 0; round < 20; round++)
            foreach (var (phrase, selection, _) in Phrases) { IntentRouter.MatchRules(IntentRequest.From(phrase, selection, null, app, settings)); n++; }
        lines.Add($"      rule layer with these intents: {watch.Elapsed.TotalMilliseconds / n:0.000} ms per phrase over {n} phrases");
    }

    // ---------- the planner ----------

    /// <summary>(text, caret, selection length, command phrase, last dictation, expected text after, expected selection or "").</summary>
    internal static readonly (string Text, int Caret, int SelLength, string Phrase, string? Last, string After, string Selected)[] Plans =
    [
        ("Mødet er på tirsdag kl. 10. Vi ses tirsdag hos Mette.", -1, 0, "Ret tirsdag til onsdag", null, "Mødet er på tirsdag kl. 10. Vi ses onsdag hos Mette.", ""),
        ("Mødet er på tirsdag kl. 10. Vi ses tirsdag hos Mette.", 27, 0, "Ret tirsdag til onsdag", null, "Mødet er på onsdag kl. 10. Vi ses tirsdag hos Mette.", ""),
        ("Tirsdag passer fint.", -1, 0, "Ret tirsdag til onsdag", null, "Onsdag passer fint.", ""),
        ("Mødet er kl. 10 i morgen.", -1, 0, "Ret kl. 10 til kl. 11", null, "Mødet er kl. 11 i morgen.", ""),
        ("See you on Tuesday. TUESDAY works.", -1, 0, "Change Tuesday to Wednesday", null, "See you on Tuesday. WEDNESDAY works.", ""),
        ("Hej Peter. Tak for i går. Vi ses snart.", -1, 0, "Marker sidste sætning", null, "Hej Peter. Tak for i går. Vi ses snart.", "Vi ses snart."),
        ("Mødet er kl. 10 i morgen. Husk kaffe", -1, 0, "Marker sidste sætning", null, "Mødet er kl. 10 i morgen. Husk kaffe", "Husk kaffe"),
        ("Det er f.eks. sådan her. Og sådan.", -1, 0, "Marker sidste sætning", null, "Det er f.eks. sådan her. Og sådan.", "Og sådan."),
        ("Hej Peter. Tak for i går. Vi ses snart.", 22, 0, "Marker sidste sætning", null, "Hej Peter. Tak for i går. Vi ses snart.", "Tak for i går."),
        ("Thanks for yesterday. See you soon.", -1, 0, "Select the last word", null, "Thanks for yesterday. See you soon.", "soon"),
        ("Første afsnit.\r\nAndet afsnit her.", -1, 0, "Marker sidste afsnit", null, "Første afsnit.\r\nAndet afsnit her.", "Andet afsnit her."),
        ("Hej Peter. Tak for i går. Vi ses snart.", -1, 0, "Slet sidste sætning", null, "Hej Peter. Tak for i går.", ""),
        ("Hej Peter. Tak for i går. Vi ses snart. ", -1, 0, "Slet sidste sætning", null, "Hej Peter. Tak for i går. ", ""),
        ("Thanks for yesterday. See you soon.", -1, 0, "Delete the last sentence", null, "Thanks for yesterday.", ""),
        ("Vi ses i morgen tidlig", -1, 0, "Slet de sidste to ord", null, "Vi ses i", ""),
        ("Første afsnit.\r\n\r\nAndet afsnit.", -1, 0, "Slet sidste afsnit", null, "Første afsnit.", ""),
        ("Kun en sætning.", -1, 0, "Slet sidste sætning", null, "", ""),
        ("Det er vigtigt at vi når det.", 7, 7, "Store bogstaver", null, "Det er VIGTIGT at vi når det.", ""),
        ("Hej. see you soon", -1, 0, "Capitalize that", "see you soon", "Hej. SEE YOU SOON", ""),
        ("Hej. see you soon", -1, 0, "Capitalize that", null, "Hej. see you SOON", ""),
        ("Hej. Vi ses snart", -1, 0, "Små bogstaver", "Vi ses snart", "Hej. vi ses snart", ""),
        ("Navnet er peter jensen", -1, 0, "Title case", "peter jensen", "Navnet er Peter Jensen", ""),
        ("Hej Mette, vi ses på tirsdag.", 0, 0, "Gå til slutningen", null, "Hej Mette, vi ses på tirsdag.", "@29"),
        ("Hej Mette, vi ses på tirsdag.", -1, 0, "Go to the start", null, "Hej Mette, vi ses på tirsdag.", "@0"),
        ("Linje et\nLinje to", 3, 0, "Gå til slutningen af linjen", null, "Linje et\nLinje to", "@8"),
        ("Vi ses tirsdag.", -1, 0, "Slet det", "ses tirsdag.", "Vi", ""),
    ];

    private static void PlannerTable(List<string> lines, AppSettings settings)
    {
        lines.Add("-- planner (what would change; no window) --");
        var app = new ActiveWindowInfo("notepad", "x");
        foreach (var (text, caretIn, selLength, phrase, last, after, selected) in Plans)
        {
            int caret = caretIn < 0 ? text.Length : caretIn;
            string sel = selLength > 0 ? text.Substring(caret, selLength) : "";
            var c = EditParser.Parse(IntentRequest.From(phrase, sel, null, app, settings));
            if (c == null) { Check(lines, false, $"[{phrase}] not understood"); continue; }
            var plan = EditPlanner.Plan(c, text, caret, selLength, last);
            string got = plan.Ok ? plan.Expected(text) : "ERROR " + plan.Error;
            string gotSel = plan.MoveOnly ? "@" + plan.Start : !plan.Delete && plan.Replacement == null ? text.Substring(plan.Start, plan.Length) : "";
            bool ok = plan.Ok && got == after && gotSel == selected;
            Check(lines, ok, $"[{phrase}] on “{Show(text)}” → “{Show(got)}”{(gotSel.Length > 0 ? $" selects “{Show(gotSel)}”" : "")}{(ok ? "" : $" (expected “{Show(after)}”{(selected.Length > 0 ? $" / “{selected}”" : "")})")}");
        }
        var missing = EditPlanner.Plan(EditParser.Parse("ret fredag til lørdag", "Ret fredag til lørdag", "")!, "Vi ses på tirsdag.", 18, 0, null);
        Check(lines, !missing.Ok && missing.Error.Contains("fredag"), $"a word that is not there is not guessed: “{missing.Error}”");
        var none = EditPlanner.Plan(new EditCommand(EditAction.Rewrite), "Noget tekst", 11, 0, null);
        Check(lines, !none.Ok, $"a rewrite with nothing selected and no dictation says so: “{none.Error}”");
    }

    // ---------- --edittest ----------

    private static async Task EditAsync(List<string> lines)
    {
        if (!AppSettings.IsTestInstance) { lines.Add("FAIL  run with MURMUR_DATA_DIR set to a test data folder"); _fail++; return; }
        string shots = Environment.GetEnvironmentVariable("MURMUR_SHOTS") ?? Path.Combine(AppSettings.DataDir, "shots");
        Directory.CreateDirectory(shots);
        Theme.Init(Application.Current);
        var settings = AppSettings.Load(); // the test folder's settings; never saved here
        settings.HandsFreeIntents = true;
        settings.VoiceEditing = settings.ReadAloudEnabled = true;
        settings.RestoreClipboard = true;
        settings.HandsFreeAiRouter = false; // the rule layer only; the AI router is the foundation's and checked there
        var llm = new LlmClient(settings);
        bool ai = llm.IsConfigured && Environment.GetEnvironmentVariable("MURMUR_EDITTEST_AI") != "0";

        PhraseTable(lines, settings);
        PlannerTable(lines, settings);

        var processor = new TextProcessor(settings, llm);
        var overlay = new OverlayWindow();
        overlay.Start(false);
        var hf = new HandsFree(settings, processor, llm, overlay);
        void SaveCard(string name)
        {
            if (overlay.Content is not FrameworkElement card) return;
            var bitmap = new RenderTargetBitmap((int)card.ActualWidth * 2, (int)card.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(card);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(shots, $"card-{name}.png"));
            png.Save(file);
        }

        lines.Add("-- in the test windows (--edithost, another process) --");
        var host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--edithost") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            async Task<string> Send(string command)
            {
                await host.StandardInput.WriteLineAsync(command);
                await host.StandardInput.FlushAsync();
                return await host.StandardOutput.ReadLineAsync() ?? "";
            }
            var ready = (await host.StandardOutput.ReadLineAsync() ?? "").Split(' ');
            var wpfWindow = (IntPtr)long.Parse(ready[1]);
            var formsWindow = (IntPtr)long.Parse(ready[2]);
            lines.Add($"      test windows pid {host.Id} ready");
            bool InFront() => ScreenReader.PidOf(Native.GetForegroundWindow()) == host.Id;
            async Task<bool> Front(string field)
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (attempt > 0) Native.SendMaskKey();
                    AllowSetForegroundWindow(host.Id);
                    await Send("focus " + field);
                    await Task.Delay(220 + attempt * 400);
                    if (InFront()) return true;
                }
                return false;
            }
            IntPtr WindowOf(string field) => field is "winforms" or "rich" ? formsWindow : wpfWindow;
            async Task<(string Text, int SelStart, int SelLength)> Get(string field)
            {
                var j = JsonDocument.Parse(await Send("get " + field)).RootElement;
                return (j.GetProperty("text").GetString() ?? "", j.GetProperty("selStart").GetInt32(), j.GetProperty("selLength").GetInt32());
            }
            int skipped = 0;
            // Runs one spoken command against a field with known text; checks the text (and selection) after it exactly.
            async Task Case(string field, string text, int caret, int selLength, string phrase, string language, string after, string? selected = null,
                string? last = null, bool expectRefused = false, string? shot = null)
            {
                if (caret < 0) caret = text.Length;
                var window = WindowOf(field);
                string sel = selLength > 0 ? text.Substring(caret, selLength) : "";
                bool handled = false;
                long ms = 0;
                // Another window taking the foreground halfway (the command is then refused, nothing typed): once more.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await Send($"set {field} {caret} {selLength} {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))}");
                    if (!await Front(field)) { skipped++; lines.Add($"SKIP  [{phrase}] in {field}: the test window is not in front, so nothing is sent to it"); return; }
                    if (last != null) VoiceEditor.NoteTyped(last, window); else VoiceEditor.ForgetLastDictation();
                    var watch = Stopwatch.StartNew();
                    handled = await hf.TryHandleAsync(phrase, sel, language, window);
                    if (!hf.HasPendingConfirmation) await hf.LastWork;
                    ms = watch.ElapsedMilliseconds;
                    if (expectRefused || VoiceEditor.Last is not { Ok: false } failed || !failed.Message.StartsWith("Click into") || InFront()) break;
                    lines.Add($"      (retry [{phrase}] in {field}: another window took the foreground)");
                }
                await Task.Delay(150);
                var (gotText, gotStart, gotLength) = await Get(field);
                var r = VoiceEditor.Last;
                bool textOk = VoiceEditor.Same(gotText, after);
                bool selOk = selected == null || (selected.StartsWith('@') ? gotStart == int.Parse(selected[1..]) && gotLength == 0 : VoiceEditor.Same(SafeSub(gotText, gotStart, gotLength), selected));
                bool okFlag = expectRefused ? r is { Ok: false } : r is { Ok: true };
                Check(lines, handled && textOk && selOk && okFlag,
                      $"{field,-8} [{phrase}] on “{Show(text)}” → “{Show(gotText)}”{(selected != null ? $" sel {gotStart}+{gotLength} “{Show(SafeSub(gotText, gotStart, gotLength))}”" : "")}; {r?.Mode} {(r?.Verified == true ? "checked" : "")} “{r?.Message}” in {ms}ms" +
                      (textOk && selOk ? "" : $" (expected “{Show(after)}”{(selected != null ? $" / {selected}" : "")})"));
                if (shot != null) SaveCard(shot);
            }
            static string SafeSub(string s, int start, int length) => start >= 0 && length >= 0 && start + length <= s.Length ? s.Substring(start, length) : "";

            const string Da = "Mødet er på tirsdag kl. 10. Vi ses tirsdag hos Mette. Husk kaffe";
            const string En = "The meeting is on Tuesday. See you Tuesday at noon";
            const string Three = "Hej Peter. Tak for i går. Vi ses snart.";
            const string Paras = "Første afsnit.\r\nAndet afsnit her.";
            foreach (string field in new[] { "wpf", "winforms", "rich" })
            {
                lines.Add($"   - {field}, by UI Automation");
                VoiceEditor.ForceKeys = false;
                await Case(field, Da, -1, 0, "Ret tirsdag til onsdag", "da", "Mødet er på tirsdag kl. 10. Vi ses onsdag hos Mette. Husk kaffe", shot: field == "wpf" ? "edit-changed" : null);
                await Case(field, Da, 28, 0, "Ret tirsdag til onsdag.", "da", "Mødet er på onsdag kl. 10. Vi ses tirsdag hos Mette. Husk kaffe");
                await Case(field, En, -1, 0, "Change Tuesday to Wednesday.", "en", "The meeting is on Tuesday. See you Wednesday at noon");
                await Case(field, Three, -1, 0, "Marker sidste sætning", "da", Three, selected: "Vi ses snart.", shot: field == "wpf" ? "edit-selected" : null);
                await Case(field, "Thanks for yesterday. See you soon.", -1, 0, "Select the last word", "en", "Thanks for yesterday. See you soon.", selected: "soon");
                await Case(field, Paras, -1, 0, "Marker sidste afsnit", "da", Paras, selected: "Andet afsnit her.");
                await Case(field, Three, -1, 0, "Slet sidste sætning", "da", "Hej Peter. Tak for i går.");
                await Case(field, "Thanks for yesterday. See you soon.", -1, 0, "Delete the last sentence.", "en", "Thanks for yesterday.");
                await Case(field, "Vi ses i morgen tidlig", -1, 0, "Slet de sidste to ord", "da", "Vi ses i");
                await Case(field, "Det er vigtigt at vi når det.", 7, 7, "Store bogstaver", "da", "Det er VIGTIGT at vi når det.");
                await Case(field, "Hej. see you soon", -1, 0, "Capitalize that.", "en", "Hej. SEE YOU SOON", last: "see you soon");
                await Case(field, "Hej Mette, vi ses på tirsdag.", 0, 0, "Flyt markøren til slutningen", "da", "Hej Mette, vi ses på tirsdag.", selected: "@29");
                await Case(field, "Hej Mette, vi ses på tirsdag.", -1, 0, "Go to the start", "en", "Hej Mette, vi ses på tirsdag.", selected: "@0");
                await Case(field, "Vi ses på tirsdag.", -1, 0, "Ret fredag til lørdag", "da", "Vi ses på tirsdag.", expectRefused: true, shot: field == "wpf" ? "edit-not-found" : null);
            }
            foreach (string field in new[] { "wpf", "winforms" })
            {
                lines.Add($"   - {field}, by keys only (no UI Automation)");
                VoiceEditor.ForceKeys = true;
                await Case(field, Da, -1, 0, "Ret tirsdag til onsdag", "da", "Mødet er på tirsdag kl. 10. Vi ses onsdag hos Mette. Husk kaffe");
                await Case(field, Three, -1, 0, "Slet sidste sætning", "da", "Hej Peter. Tak for i går.");
                await Case(field, "Thanks for yesterday. See you soon.", -1, 0, "Select the last word", "en", "Thanks for yesterday. See you soon.", selected: "soon");
                await Case(field, Paras, -1, 0, "Slet sidste afsnit", "da", "Første afsnit.");
                await Case(field, "Det er vigtigt at vi når det.", 7, 7, "Store bogstaver", "da", "Det er VIGTIGT at vi når det.");
                await Case(field, "Hej Mette, vi ses på tirsdag.", 0, 0, "Go to the end", "en", "Hej Mette, vi ses på tirsdag.", selected: "@29");
                VoiceEditor.ForceKeys = false;
            }

            lines.Add("   - safety");
            // A password field: nothing read, nothing typed.
            await Case("password", "hunter2 secret", -1, 0, "Slet sidste ord", "da", "hunter2 secret", expectRefused: true, shot: "edit-password");
            // An excluded app (the test window's process is Murmur).
            string excludedBefore = settings.ExcludedApps;
            settings.ExcludedApps = "Murmur";
            await Case("wpf", Three, -1, 0, "Slet sidste sætning", "da", Three, expectRefused: true, shot: "edit-excluded");
            settings.ExcludedApps = excludedBefore;
            // A command about a window that is not in front: nothing is typed.
            await Send($"set wpf 0 0 {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Three))}");
            if (await Front("wpf"))
            {
                var r = await VoiceEditor.RunAsync(new TestHost(hf, GetShellWindow()), IntentRequest.From("Slet sidste sætning", "", "da", new ActiveWindowInfo("explorer", "x"), settings), EditParser.Parse("slet sidste sætning", "Slet sidste sætning", "")!, CancellationToken.None);
                var (t, _, _) = await Get("wpf");
                Check(lines, !r.Ok && t == Three, $"a command about another window than the one in front types nothing: “{r.Message}”");
            }
            // A big deletion asks first; "nej" keeps it, "ja" deletes it.
            string big = "Kort indledning.\r\n" + string.Join(" ", Enumerable.Repeat("Denne sætning er en del af et meget langt afsnit, som ikke må slettes uden et ja.", 5));
            await Send($"set wpf {big.Length} 0 {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(big))}");
            if (await Front("wpf"))
            {
                VoiceEditor.ForgetLastDictation();
                await hf.TryHandleAsync("Slet sidste afsnit", "", "da", wpfWindow);
                bool asked = hf.HasPendingConfirmation;
                await Task.Delay(500);
                SaveCard("edit-confirm-delete");
                await hf.TryHandleAsync("Nej", "", "da", wpfWindow);
                await hf.LastWork;
                await Task.Delay(200);
                var (kept, _, _) = await Get("wpf");
                Check(lines, asked && VoiceEditor.Same(kept, big), $"deleting {big.Length - 18} characters waits for a yes; “nej” keeps the text ({VoiceEditor.Last?.Message})");
                await Front("wpf");
                await hf.TryHandleAsync("Slet sidste afsnit", "", "da", wpfWindow);
                bool asked2 = hf.HasPendingConfirmation;
                var work = hf.LastWork;
                await hf.TryHandleAsync("Ja", "", "da", wpfWindow);
                await work;
                await Task.Delay(200);
                var (deleted, _, _) = await Get("wpf");
                Check(lines, asked2 && VoiceEditor.Same(deleted, "Kort indledning."), $"after “ja” it is deleted: “{Show(deleted)}”");
            }

            if (ai)
            {
                lines.Add("   - rewriting the last dictation (AI model, 2 calls)");
                const string headDa = "Hej Søren.\r\n";
                const string lastDa = "send mig filen nu";
                await Send($"set wpf {headDa.Length + lastDa.Length} 0 {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(headDa + lastDa))}");
                if (await Front("wpf"))
                {
                    VoiceEditor.NoteTyped(lastDa, wpfWindow);
                    await hf.TryHandleAsync("Gør det mere høfligt", "", "da", wpfWindow);
                    await hf.LastWork;
                    await Task.Delay(150);
                    var (t, _, _) = await Get("wpf");
                    string rewritten = VoiceEditor.Lines(t).StartsWith(VoiceEditor.Lines(headDa)) ? VoiceEditor.Lines(t)[VoiceEditor.Lines(headDa).Length..] : "";
                    Check(lines, VoiceEditor.Last is { Ok: true, Verified: true } && rewritten.Length > 0 && rewritten != lastDa && !t.Contains(lastDa),
                          $"“gør det mere høfligt” rewrote only the last dictation: “{Show(t)}”");
                    SaveCard("edit-rewritten");
                }
                const string headEn = "Hi Sam, ";
                const string lastEn = "I need the report by friday and it was late last time so dont be late";
                await Send($"set winforms {headEn.Length + lastEn.Length} 0 {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(headEn + lastEn))}");
                if (await Front("winforms"))
                {
                    VoiceEditor.NoteTyped(lastEn, formsWindow);
                    await hf.TryHandleAsync("Make it shorter.", "", "en", formsWindow);
                    await hf.LastWork;
                    await Task.Delay(150);
                    var (t, _, _) = await Get("winforms");
                    Check(lines, VoiceEditor.Last is { Ok: true } && t.StartsWith(headEn) && t.Length < headEn.Length + lastEn.Length && !t.Contains(lastEn),
                          $"“make it shorter” rewrote only the last dictation: “{Show(t)}”");
                }
            }
            else lines.Add("      (rewrites skipped: no AI model, or MURMUR_EDITTEST_AI=0)");
            VoiceEditor.ForceKeys = false;
            if (skipped > 0) lines.Add($"      {skipped} cases skipped because the test window could not be brought to the front");
        }
        finally
        {
            try { await host.StandardInput.WriteLineAsync("quit"); await host.StandardInput.FlushAsync(); } catch { }
            if (!host.WaitForExit(3000)) try { host.Kill(); } catch { }
        }

        if (Environment.GetEnvironmentVariable("MURMUR_EDITTEST_BROWSER") != "0") await BrowserAsync(lines, hf, settings, shots);
    }

    /// <summary>An intent host for one window that is deliberately not the one in front (a safety check).</summary>
    private sealed class TestHost(HandsFree inner, IntPtr window) : IIntentHost
    {
        public AppSettings Settings => inner.Settings;
        public TextProcessor Processor => inner.Processor;
        public LlmClient Llm => inner.Llm;
        public IntPtr Window => window;
        public ActiveWindowInfo App => new("explorer", "");
        public void ShowBusy(string text) { }
        public void Flash(string text, bool error = false) { }
        public Task<ScreenContent?> ReadWindowAsync(CancellationToken ct = default) => Task.FromResult<ScreenContent?>(null);
        public void ShowAnswer(string title, string text, string context = "") { }
        public Task<PasteOutcome> PasteAsync(string text, string status = "Inserted", string note = "") => Task.FromResult(PasteOutcome.Blocked);
        public Task<bool> ConfirmAsync(string question, string detail, string verb, TimeSpan? timeout = null) => Task.FromResult(false);
        public Task<bool> PressAsync(ushort modifier, ushort key, string label) => Task.FromResult(false);
        public void ResolveConfirmation(bool yes) { }
    }

    // ---------- a browser text area ----------

    private static async Task BrowserAsync(List<string> lines, HandsFree hf, AppSettings settings, string shots)
    {
        lines.Add("-- a browser text area (Edge, throw-away profile) --");
        string edge = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        }.FirstOrDefault(File.Exists) ?? "";
        if (edge.Length == 0) { lines.Add("SKIP  Edge is not installed"); return; }
        string folder = Path.Combine(AppSettings.DataDir, "browser");
        Directory.CreateDirectory(folder);
        string page = Path.Combine(folder, "edit-test.html");
        File.WriteAllText(page, """
            <!doctype html><html><head><meta charset="utf-8"><title>MET|0|0|</title></head>
            <body style="font:16px Segoe UI;margin:24px"><p>Murmur edit test (browser)</p>
            <textarea id="t" rows="8" cols="70" autofocus></textarea>
            <script>
            const t = document.getElementById('t');
            function upd() { document.title = 'MET|' + t.selectionStart + '|' + t.selectionEnd + '|' + encodeURIComponent(t.value); }
            t.addEventListener('input', upd); document.addEventListener('selectionchange', upd); setInterval(upd, 150); t.focus();
            </script></body></html>
            """);
        string profile = Path.Combine(folder, "edge-profile");
        var started = Process.Start(new ProcessStartInfo(edge, $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-sync --window-size=900,500 --window-position=80,80 --app=\"{new Uri(page).AbsoluteUri}\"") { UseShellExecute = false });
        IntPtr window = IntPtr.Zero;
        try
        {
            for (int i = 0; i < 60 && window == IntPtr.Zero; i++)
            {
                await Task.Delay(250);
                window = FindTitled("MET|");
            }
            if (window == IntPtr.Zero) { lines.Add("SKIP  the browser window did not open"); return; }
            await Task.Delay(800);
            int pid = ScreenReader.PidOf(window);
            async Task<bool> Front()
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    Native.SendMaskKey();
                    SetForegroundWindow(window);
                    await Task.Delay(250 + attempt * 300);
                    if (ScreenReader.PidOf(Native.GetForegroundWindow()) == pid) return true;
                }
                return false;
            }
            (string Text, int Start, int End) State()
            {
                var sb = new System.Text.StringBuilder(8192);
                GetWindowText(window, sb, sb.Capacity);
                var parts = sb.ToString().Split('|', 4);
                return parts.Length == 4 && parts[0] == "MET" ? (Uri.UnescapeDataString(parts[3].Split(" - ")[0]), int.Parse(parts[1]), int.Parse(parts[2])) : ("?", -1, -1);
            }
            async Task Case(string text, string phrase, string language, string after, string? selected = null, string? last = null)
            {
                bool handled = false;
                var watch = Stopwatch.StartNew();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (!await Front()) { lines.Add($"SKIP  [{phrase}] in the browser: its window is not in front"); return; }
                    // Reset the test page's own text area: select all and paste (the test's own window only).
                    Keys.Chord(Keys.Control, (ushort)'A');
                    await TextInjector.PasteAsync(text, true);
                    await Task.Delay(350);
                    if (last != null) VoiceEditor.NoteTyped(last, window); else VoiceEditor.ForgetLastDictation();
                    watch.Restart();
                    handled = await hf.TryHandleAsync(phrase, "", language, window);
                    await hf.LastWork;
                    if (VoiceEditor.Last is not { Ok: false } failed || !failed.Message.StartsWith("Click into")) break;
                    lines.Add($"      (retry [{phrase}] in the browser: another window took the foreground)");
                }
                await Task.Delay(400);
                var (got, s, e) = State();
                var r = VoiceEditor.Last;
                bool selOk = selected == null || (selected.StartsWith('@') ? s == int.Parse(selected[1..]) && e == s : s >= 0 && e <= got.Length && got[s..e] == selected);
                Check(lines, handled && r is { Ok: true } && got == after && selOk,
                      $"browser  [{phrase}] → “{Show(got)}” sel {s}-{e}; {r?.Mode} {(r?.Verified == true ? "checked" : "")} “{r?.Message}” in {watch.ElapsedMilliseconds}ms{(got == after && selOk ? "" : $" (expected “{Show(after)}” {selected})")}");
            }
            // UI Automation in Chromium wakes up on the first question; ask once before the cases.
            if (await Front()) { await VoiceEditor.ReadAsync(); await Task.Delay(500); }
            await Case("Mødet er på tirsdag kl. 10. Vi ses tirsdag hos Mette.", "Ret tirsdag til onsdag", "da", "Mødet er på tirsdag kl. 10. Vi ses onsdag hos Mette.");
            await Case("Hej Peter. Tak for i går. Vi ses snart.", "Marker sidste sætning", "da", "Hej Peter. Tak for i går. Vi ses snart.", selected: "Vi ses snart.");
            await Case("Hej Peter. Tak for i går. Vi ses snart.", "Slet sidste sætning", "da", "Hej Peter. Tak for i går.");
            await Case("Første afsnit.\nAndet afsnit her.", "Delete the last paragraph", "en", "Første afsnit.");
            await Case("Hej. see you soon", "Capitalize that", "en", "Hej. SEE YOU SOON", last: "see you soon");
            await Case("The meeting is on Tuesday.", "Go to the start", "en", "The meeting is on Tuesday.", selected: "@0");
            using (var picture = ScreenReader.Capture(window)) picture?.Save(Path.Combine(shots, "browser-after.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            if (window != IntPtr.Zero) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE: the test's own browser window
            await Task.Delay(1500);
            // Anything still running from the throw-away profile (never the user's own Edge, which has another profile).
            try
            {
                if (started is { HasExited: false }) started.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr l);

    private static IntPtr FindTitled(string prefix)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().StartsWith(prefix, StringComparison.Ordinal)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---------- the test windows ----------

    private static class EditHost
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

        public static void Run()
        {
            var wpfBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, FontSize = 14, Margin = new Thickness(8) };
            System.Windows.Automation.AutomationProperties.SetName(wpfBox, "WPF text");
            var password = new PasswordBox { Width = 240, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "WPF text box", Margin = new Thickness(8, 8, 8, 0) });
            panel.Children.Add(wpfBox);
            panel.Children.Add(new TextBlock { Text = "Password", Margin = new Thickness(8, 8, 8, 0) });
            panel.Children.Add(password);
            var window = new Window
            {
                Title = "Murmur edit test - WPF", Width = 640, Height = 360, Left = 60, Top = 60, WindowStartupLocation = WindowStartupLocation.Manual,
                Content = panel, Background = Brushes.White,
            };
            var form = new Forms.Form { Text = "Murmur edit test - WinForms", Width = 640, Height = 380, StartPosition = Forms.FormStartPosition.Manual, Left = 720, Top = 60 };
            var formsBox = new Forms.TextBox { Multiline = true, Left = 10, Top = 10, Width = 600, Height = 140, Font = new System.Drawing.Font("Segoe UI", 11), AccessibleName = "WinForms text" };
            var rich = new Forms.RichTextBox { Left = 10, Top = 170, Width = 600, Height = 140, Font = new System.Drawing.Font("Segoe UI", 11), AccessibleName = "Rich text", DetectUrls = false };
            form.Controls.Add(formsBox);
            form.Controls.Add(rich);
            window.Show();
            form.Show();
            Console.Out.WriteLine($"ready {new System.Windows.Interop.WindowInteropHelper(window).Handle} {form.Handle}");
            Console.Out.Flush();

            void Front(bool forms)
            {
                var handle = forms ? form.Handle : new System.Windows.Interop.WindowInteropHelper(window).Handle;
                SetForegroundWindow(handle); // allowed once the test passed its foreground right on (AllowSetForegroundWindow)
                SwitchToThisWindow(handle, true);
                if (forms) form.Activate(); else window.Activate();
            }
            void Focus(string field)
            {
                switch (field)
                {
                    case "wpf": Front(false); wpfBox.Focus(); System.Windows.Input.Keyboard.Focus(wpfBox); break;
                    case "password": Front(false); password.Focus(); System.Windows.Input.Keyboard.Focus(password); break;
                    case "winforms": Front(true); formsBox.Focus(); break;
                    case "rich": Front(true); rich.Focus(); break;
                }
            }
            new Thread(() =>
            {
                string? line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    string command = line, answer = "done";
                    window.Dispatcher.Invoke(() =>
                    {
                        string[] parts = command.Split(' ');
                        switch (parts[0])
                        {
                            case "set":
                            {
                                string field = parts[1];
                                int caret = int.Parse(parts[2]), length = int.Parse(parts[3]);
                                string text = parts.Length > 4 ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[4])) : "";
                                switch (field)
                                {
                                    case "wpf": wpfBox.Text = text; wpfBox.Select(caret, length); break;
                                    case "winforms": formsBox.Text = text; formsBox.Select(caret, length); break;
                                    case "rich": rich.Text = text; rich.Select(RichIndex(rich.Text, text, caret), RichIndex(rich.Text, text, caret + length) - RichIndex(rich.Text, text, caret)); break;
                                    case "password": password.Password = text; break;
                                }
                                break;
                            }
                            case "focus": Focus(parts[1]); break;
                            case "get":
                            {
                                (string Text, int Start, int Length) state = parts[1] switch
                                {
                                    "wpf" => (wpfBox.Text, wpfBox.SelectionStart, wpfBox.SelectionLength),
                                    "winforms" => (formsBox.Text, formsBox.SelectionStart, formsBox.SelectionLength),
                                    // RichEdit keeps "\r" for a line break; give it back as "\r\n" positions like the others.
                                    "rich" => (rich.Text.Replace("\n", "\r\n"), FromRich(rich.Text, rich.SelectionStart), FromRich(rich.Text, rich.SelectionStart + rich.SelectionLength) - FromRich(rich.Text, rich.SelectionStart)),
                                    _ => (password.Password, 0, 0),
                                };
                                answer = JsonSerializer.Serialize(new { text = state.Text, selStart = state.Start, selLength = state.Length });
                                break;
                            }
                            case "quit": Application.Current.Shutdown(); break;
                        }
                    });
                    Console.Out.WriteLine(answer);
                    Console.Out.Flush();
                    if (command == "quit") break;
                }
                window.Dispatcher.Invoke(() => Application.Current.Shutdown());
            }) { IsBackground = true }.Start();
        }

        /// <summary>A position in "\r\n" text → the rich box's position (it stores a line break as one "\n").</summary>
        private static int RichIndex(string rich, string original, int index) => index - CountCrLf(original, index);
        private static int FromRich(string rich, int index) => index + rich[..Math.Min(index, rich.Length)].Count(ch => ch == '\n');
        private static int CountCrLf(string s, int upTo) { int n = 0; for (int i = 0; i + 1 < upTo && i + 1 < s.Length; i++) if (s[i] == '\r' && s[i + 1] == '\n') n++; return n; }
    }

    // ---------- --speaktest ----------

    /// <summary>Plays nothing: reads the audio faster than real time, so the read-aloud card can be checked in silence.</summary>
    internal sealed class SilentPlayer(double speed) : IWavePlayer
    {
        private IWaveProvider? _source;
        private volatile bool _stop, _pause;
        private Task? _run;
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
        public WaveFormat OutputWaveFormat => _source!.WaveFormat;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider waveProvider) => _source = waveProvider;
        public void Play() { _pause = false; PlaybackState = PlaybackState.Playing; _run ??= Task.Run(Loop); }
        public void Pause() { _pause = true; PlaybackState = PlaybackState.Paused; }
        public void Stop() => _stop = true;
        public void Dispose() => _stop = true;
        private void Loop()
        {
            var buffer = new byte[Math.Max(1024, _source!.WaveFormat.AverageBytesPerSecond / 10)];
            while (!_stop)
            {
                if (_pause) { Thread.Sleep(20); continue; }
                if (_source.Read(buffer) == 0) break;
                Thread.Sleep((int)Math.Max(1, 100 / speed));
            }
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(null));
        }
    }

    internal const string DanishSample = "Hej Mette. Tak for din mail om mødet på torsdag. Jeg kommer gerne klokken ti, og jeg tager tilbuddet på det nye dashboard med.";
    internal const string EnglishSample = "Hi James. Thanks for your note about the quarterly review. I will send the slides before Wednesday, and Thursday at three works for me.";

    private static async Task SpeakAsync(List<string> lines, string folder)
    {
        if (!AppSettings.IsTestInstance) { lines.Add("FAIL  run with MURMUR_DATA_DIR set to a test data folder"); _fail++; return; }
        Directory.CreateDirectory(folder);
        Theme.Init(Application.Current);
        var settings = AppSettings.Load(); // never saved here
        settings.ReadAloudRate = 1;
        settings.HandsFreeIntents = settings.ReadAloudEnabled = true;
        settings.HandsFreeAiRouter = false;
        var voices = SpeechVoices.All();
        lines.Add($"Voices Windows offers apps ({voices.Count}):");
        foreach (var v in voices) lines.Add($"      {v.Name} ({v.Language}, {v.Gender})");
        Check(lines, voices.Count > 0, "Windows has speech voices");
        var da = SpeechVoices.Pick("da", settings, out bool daExact);
        var en = SpeechVoices.Pick("en", settings, out bool enExact);
        lines.Add($"      picked for Danish: {da?.DisplayName} ({da?.Language}){(daExact ? "" : " - NOT a Danish voice")}; for English: {en?.DisplayName} ({en?.Language}){(enExact ? "" : " - not English")}");
        Check(lines, enExact, "an English voice is installed");
        if (!daExact) lines.Add("NOTE  no Danish voice is installed on this PC: Danish text is read by another voice and the card says so (Settings › Time & language › Speech › Add voices)");

        Check(lines, SpeechVoices.LanguageOf(DanishSample, settings) == "da" && SpeechVoices.LanguageOf(EnglishSample, settings) == "en", "the language of each sample is told from the text");
        var mixed = SpeechVoices.Chunks(DanishSample + "\n\n" + EnglishSample, settings);
        Check(lines, mixed.Count >= 2 && mixed.First().Language == "da" && mixed.Last().Language == "en", $"a Danish paragraph and an English one get their own voices: {string.Join(" | ", mixed.Select(c => c.Language + ": " + EditPlanner.Short(c.Text, 30)))}");
        var longText = string.Join(" ", Enumerable.Repeat("Dette er en sætning i en lang tekst, som skal deles op i mindre stykker.", 12));
        var pieces = SpeechVoices.Chunks(longText, settings);
        Check(lines, pieces.Count > 2 && pieces.All(p => p.Text.Length <= 300) && string.Join(" ", pieces.Select(p => p.Text)) == longText, $"a long text is read in {pieces.Count} pieces of at most 300 characters, nothing lost");

        async Task<SpeechAudio> Synth(string name, string text, string language, double rate)
        {
            var audio = await SpeechVoices.SynthesizeAsync(new SpeechChunk(text, language), settings, rate);
            string path = Path.Combine(folder, name + ".wav");
            await File.WriteAllBytesAsync(path, audio.Wav);
            var length = SpeechVoices.Duration(audio.Wav);
            using var reader = new WaveFileReader(path);
            lines.Add($"      {name}.wav: {audio.Voice}, rate {rate:0.00}, {length.TotalSeconds:0.00}s of speech, {reader.WaveFormat.SampleRate} Hz, {audio.Wav.Length / 1024} KB, made in {audio.SynthMs:0}ms ({audio.SynthMs / Math.Max(1, length.TotalMilliseconds) * 100:0.0}% of its length)");
            return audio;
        }
        var first = await Synth("warmup", "Hej.", "da", 1); // the first call loads the voice
        var daAudio = await Synth("danish", DanishSample, "da", 1);
        var enAudio = await Synth("english", EnglishSample, "en", 1);
        var enFast = await Synth("english-fast", EnglishSample, "en", 1.5);
        var enSlow = await Synth("english-slow", EnglishSample, "en", 0.8);
        double d1 = SpeechVoices.Duration(enAudio.Wav).TotalSeconds, dFast = SpeechVoices.Duration(enFast.Wav).TotalSeconds, dSlow = SpeechVoices.Duration(enSlow.Wav).TotalSeconds;
        Check(lines, daAudio.Wav.Length > 10000 && enAudio.Wav.Length > 10000, "Danish and English samples were synthesised to WAV files");
        Check(lines, dFast < d1 * 0.85 && dSlow > d1 * 1.1, $"speed works: 1.5× gives {dFast:0.0}s, 1× {d1:0.0}s, 0.8× {dSlow:0.0}s");
        Check(lines, enAudio.SynthMs < 3000, $"a sentence is ready to play in {enAudio.SynthMs:0}ms (first call {first.SynthMs:0}ms)");

        // The read-aloud card, with a player that plays nothing (8× faster than real time).
        lines.Add("-- the read-aloud card (silent player) --");
        // The card's recording state reads the app's settings; a developer check has none of its own yet.
        typeof(App).GetProperty(nameof(App.Settings))!.SetValue(Application.Current, settings);
        var overlay = new OverlayWindow();
        overlay.Start(false);
        var reader2 = ReadAloud.Attach(overlay, settings);
        reader2.SaveSettings = false;
        reader2.PlayerFactory = () => new SilentPlayer(8);
        string shots = Environment.GetEnvironmentVariable("MURMUR_SHOTS") ?? Path.Combine(folder, "shots");
        Directory.CreateDirectory(shots);
        void SaveCard(string name)
        {
            if (overlay.Content is not FrameworkElement card) return;
            var bitmap = new RenderTargetBitmap((int)card.ActualWidth * 2, (int)card.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(card);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(shots, $"card-{name}.png"));
            png.Save(file);
        }
        var watch = Stopwatch.StartNew();
        var reading = reader2.ReadAsync(DanishSample + "\n\n" + EnglishSample, "selection");
        await Task.Delay(1200);
        Check(lines, reader2.IsActive, "reading has started");
        SaveCard("reading");
        // A recording pauses it; "læs hurtigere" picks it up again, faster.
        overlay.ShowRecording(HotkeyKind.Command, false);
        await Task.Delay(300);
        Check(lines, reader2.IsActive && reader2.IsPaused, "starting a recording pauses reading");
        SaveCard("reading-paused-by-recording");
        overlay.ShowBusy("Thinking");
        var hf = new HandsFree(settings, new TextProcessor(settings, new LlmClient(settings)), new LlmClient(settings), overlay);
        await hf.TryHandleAsync("Læs hurtigere", "", "da", IntPtr.Zero);
        await hf.LastWork;
        Check(lines, reader2.IsActive && !reader2.IsPaused && Math.Abs(reader2.Rate - 1.25) < 0.01, $"“læs hurtigere” resumes at {reader2.Rate:0.00}×");
        await Task.Delay(250);
        SaveCard("reading-faster");
        await reading;
        Check(lines, reader2.Events.Contains("finished") && reader2.Events.Any(e => e.Contains(" en ")) && reader2.Events.Any(e => e.Contains(" da ")),
              $"read to the end in {watch.ElapsedMilliseconds}ms (silent, 8×): {string.Join("; ", reader2.Events)}");
        Check(lines, reader2.Events.Any(e => e.Contains("rate 1.25")), "the pieces after the speed change were made at the new speed");

        // Esc (the card closing) stops it; so does "stop" in command mode.
        reading = reader2.ReadAsync(EnglishSample + " " + EnglishSample, "Outlook");
        await Task.Delay(800);
        overlay.HideNow(); // what Esc does
        await reading;
        Check(lines, !reader2.IsActive && reader2.Events.Contains("stopped"), "Esc (the card closing) stops reading");
        reading = reader2.ReadAsync(EnglishSample + " " + EnglishSample, "Outlook");
        await Task.Delay(800);
        overlay.ShowRecording(HotkeyKind.Command, false);
        overlay.ShowBusy("Thinking");
        await hf.TryHandleAsync("Stop", "", "en", IntPtr.Zero);
        await reading;
        Check(lines, !reader2.IsActive && reader2.Events.Contains("stopped") && hf.LastRoute.StartsWith("read_control"), $"“stop” in command mode stops reading ({hf.LastRoute})");
        reading = reader2.ReadAsync(DanishSample, "Notepad");
        await Task.Delay(800);
        overlay.ShowResult("Something else was dictated", "notepad", false);
        await reading;
        Check(lines, !reader2.IsActive, "another dictation taking the card stops reading");

        // "Læs mailen op" reads the window (the hands-free test mail window, another process; nothing is typed into it).
        var mail = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--mailhost") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            var ready = (await mail.StandardOutput.ReadLineAsync() ?? "").Split(' ');
            var hwnd = (IntPtr)long.Parse(ready[1]);
            await Task.Delay(500);
            var task = hf.TryHandleAsync("Læs mailen op", "", "da", hwnd);
            await Task.Delay(1500);
            SaveCard("reading-mail");
            var text = reader2.LastAudio?.Chunk.Text ?? "";
            Check(lines, hf.LastRoute.StartsWith("read_aloud") && hf.LastRead is { } read && read.Main.Contains("Vesterbrogade"), $"“læs mailen op” read the open mail ({hf.LastRead?.Source}, {hf.LastRead?.Main.Length} chars), first piece “{EditPlanner.Short(text, 60)}”");
            reader2.Stop(byUser: true);
            await task;
            await hf.LastWork;
            settings.ExcludedApps = "Murmur";
            await hf.TryHandleAsync("Læs mailen op", "", "da", hwnd);
            await hf.LastWork;
            Check(lines, !reader2.IsActive && hf.LastFlash.Contains("off in"), $"an excluded app is not read aloud: “{hf.LastFlash}”");
        }
        finally
        {
            try { await mail.StandardInput.WriteLineAsync("quit"); await mail.StandardInput.FlushAsync(); } catch { }
            if (!mail.WaitForExit(3000)) try { mail.Kill(); } catch { }
        }
    }

    // ---------- --editspeak-demo ----------

    public static async Task DemoAsync(OverlayWindow overlay, string? folder)
    {
        async Task Hold(string name, int ms = 2600)
        {
            await Task.Delay(1200);
            if (folder != null && overlay.Content is FrameworkElement card)
            {
                var bitmap = new RenderTargetBitmap((int)card.ActualWidth * 2, (int)card.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(card);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(folder);
                using var file = File.Create(Path.Combine(folder, $"card-{name}.png"));
                png.Save(file);
            }
            await Task.Delay(Math.Max(0, ms - 1200));
        }
        await Task.Delay(2500);
        var random = new Random(3);
        do
        {
            overlay.ShowReading("Reading aloud", "  Outlook · 2/6", "Kan vi mødes torsdag kl. 10 hos Zync på Vesterbrogade 12? Jeg vil gerne gennemgå tilbuddet på det nye dashboard.", 0.28);
            for (int i = 0; i < 30; i++) { overlay.PushReadingLevel((float)(0.2 + random.NextDouble() * 0.6)); await Task.Delay(30); }
            await Hold("reading");
            overlay.ShowReading("Reading aloud", "  selection · 5/6 · 1.25×", "Thanks for your note about the quarterly review. I will send the slides before Wednesday.", 0.83);
            await Hold("reading-faster");
            overlay.Flash("Changed “tirsdag” to “onsdag”");
            await Hold("edit-changed");
            overlay.Flash("Selected the last sentence");
            await Hold("edit-selected");
            overlay.Flash("Could not find “fredag” in the text.", error: true);
            await Hold("edit-not-found");
            overlay.ShowConfirm("Delete this text?", "Deletes 412 characters in Notepad: “Denne sætning er en del af et meget langt afsnit, som ikke må slettes uden et ja…”. Ctrl+Z brings it back.", "Delete", TimeSpan.FromSeconds(20));
            await Hold("edit-confirm-delete");
            overlay.Flash("The focused field is a password field.", error: true);
            await Hold("edit-password");
        } while (folder == null);
        Application.Current.Shutdown();
    }
}
