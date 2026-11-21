using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Murmur;

/// <summary>
/// The task checks' fake mail client, a window in its own process (<c>--outlookhost</c>), driven over stdin by --tasktest.
/// It behaves the way new Outlook does where that matters to Murmur: it starts slowly (the window comes first, the
/// message list <c>--slow=ms</c> later), its rows are named "Unread, Sender, Subject, time, preview" or (<c>--parts</c>)
/// have no name and carry the parts in child elements, only the newest ten are in view, and its search box (Alt+Q, or
/// found by its name "Search"; with --parts it has no name) filters all mails on Enter. Ctrl+R opens the reply box,
/// Ctrl+Enter "sends". It only records what was done to it; nothing leaves the window.
/// </summary>
internal static class OutlookHost
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private sealed class Parts : StackPanel { public override string ToString() => ""; }
    private sealed class Row : ListBoxItem { public override string ToString() => ""; }

    internal sealed record Mail(bool Unread, string From, string Subject, string Time, string Preview);

    /// <summary>Newest first. The first ten are in view; the rest are found only by searching.</summary>
    internal static readonly Mail[] Mails =
    [
        new(true, "MyProtein", "Your order has shipped", "10:32", "Hi Lukas, your order 88213 is on its way and should arrive on Tuesday."),
        new(true, "Daniel Hansen", "Re: Tilbud på dashboard", "09:58", "Hej Lukas, tak for tilbuddet. Vi vil gerne gå videre, men kan I starte i januar?"),
        new(false, "Mette Sørensen", "Møde torsdag", "09:15", "Kan vi mødes torsdag kl. 10 hos Zync?"),
        new(false, "Myprotein", "20% off everything this weekend", "18-11-2026", "Our biggest sale of the year starts now."),
        new(false, "LinkedIn", "New jobs for you", "17-11-2026", "Senior developer roles in Copenhagen."),
        new(false, "Daniel Hansen", "Frokost fredag", "16-11-2026", "Skal vi tage frokost på fredag?"),
        new(false, "Bogholderiet", "Faktura 4471", "15-11-2026", "Vedhæftet finder du faktura 4471."),
        new(false, "James Carter", "Quarterly review", "14-11-2026", "Could you send me the slides before Wednesday?"),
        new(false, "Netflix", "New on Netflix", "13-11-2026", "Shows we think you'll like."),
        new(false, "Søren Kjær", "Billeder fra turen", "12-11-2026", "Her er billederne fra weekenden."),
        new(false, "Peter Jørgensen", "Kontrakt 2025, endelig udgave", "02-10-2026", "Hej Lukas, her er den endelige kontrakt til underskrift."),
        new(false, "Peter Jørgensen", "Kontrakt 2025, udkast", "01-10-2026", "Udkastet til kontrakten, som vi talte om."),
        new(false, "Hanne Lund", "Ferieplan", "20-09-2026", "Her er ferieplanen for efteråret."),
        new(false, "MyProtein", "Welcome to MyProtein", "01-06-2026", "Thanks for signing up."),
    ];

    public static void Run(string[] args)
    {
        int slow = args.FirstOrDefault(a => a.StartsWith("--slow="))?[7..] is { } ms && int.TryParse(ms, out int v) ? v : 0;
        bool partsOnly = args.Contains("--parts");
        var keys = new List<string>();
        var searches = new List<string>();
        bool sent = false, enterElsewhere = false;
        string opened = "";

        var list = new ListBox { Width = 330, Margin = new Thickness(4) };
        AutomationProperties.SetName(list, "Message list");
        var loading = new TextBlock { Text = "Loading…", Margin = new Thickness(8), Foreground = Brushes.Gray };
        var folders = new TreeView { Width = 150, Margin = new Thickness(4) };
        foreach (var f in new[] { "Inbox", "Sent Items", "Drafts", "Archive", "Deleted Items" }) folders.Items.Add(new TreeViewItem { Header = f });
        var search = new TextBox { Width = 360, Margin = new Thickness(4), FontSize = 13 };
        if (!partsOnly) AutomationProperties.SetName(search, "Search");
        var newMail = new Button { Content = "New mail", Margin = new Thickness(4), Padding = new Thickness(8, 2, 8, 2) };
        var toolbar = new ToolBar();
        foreach (var b in new[] { "Delete", "Archive", "Move to", "Reply", "Reply all", "Forward" }) toolbar.Items.Add(new Button { Content = b });
        var from = new TextBlock { FontWeight = FontWeights.SemiBold };
        var subject = new TextBlock { FontSize = 16, Margin = new Thickness(0, 2, 0, 6) };
        var body = new RichTextBox { IsReadOnly = true, Height = 160, BorderThickness = new Thickness(0), FontSize = 14 };
        var reply = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(reply, "Reply");
        var pane = new StackPanel { Margin = new Thickness(8), Children = { from, subject, body, reply } };
        var top = new DockPanel { Children = { newMail, search } };
        var middle = new DockPanel();
        DockPanel.SetDock(folders, Dock.Left);
        var listArea = new Grid { Children = { loading, list } };
        DockPanel.SetDock(listArea, Dock.Left);
        middle.Children.Add(folders);
        middle.Children.Add(listArea);
        middle.Children.Add(pane);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(toolbar);
        root.Children.Add(middle);
        var window = new Window
        {
            Title = "Murmur test outlook - Inbox", Width = 1100, Height = 640, Left = 90, Top = 90, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = root, Background = Brushes.White,
        };

        List<Mail> showing = [];
        void Fill(IEnumerable<Mail> mails)
        {
            showing = mails.ToList();
            list.Items.Clear();
            foreach (var m in showing)
            {
                var item = new Row { Padding = new Thickness(6, 4, 6, 4) };
                // A panel with no text of its own: the row's UI Automation name would otherwise be the panel's type name.
                var stack = new Parts();
                stack.Children.Add(new TextBlock { Text = m.From, FontWeight = m.Unread ? FontWeights.Bold : FontWeights.Normal });
                stack.Children.Add(new TextBlock { Text = m.Subject });
                stack.Children.Add(new TextBlock { Text = m.Time, Foreground = Brushes.Gray });
                stack.Children.Add(new TextBlock { Text = m.Preview.Length > 40 ? m.Preview[..40] + "…" : m.Preview, Foreground = Brushes.Gray });
                item.Content = stack;
                // As new Outlook names its rows; with --parts the row has no name and only its parts do.
                AutomationProperties.SetName(item, partsOnly ? "" : $"{(m.Unread ? "Unread, " : "")}{m.From}, {m.Subject}, {m.Time}, {m.Preview}");
                list.Items.Add(item);
            }
            loading.Visibility = showing.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (showing.Count == 0) loading.Text = list.Items.Count == 0 && searches.Count > 0 ? "We didn't find anything." : "Loading…";
        }
        void Open(int index)
        {
            if (index < 0 || index >= showing.Count) return;
            var m = showing[index];
            opened = m.Subject;
            from.Text = m.From;
            subject.Text = m.Subject;
            body.Document.Blocks.Clear();
            body.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(m.Preview + "\n\nVenlig hilsen\n" + m.From)));
        }
        list.SelectionChanged += (_, _) => Open(list.SelectedIndex);
        void OpenReply() { reply.Visibility = Visibility.Visible; reply.Focus(); }
        window.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.ImeProcessed) return;
            string mods = (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "");
            lock (keys) keys.Add(mods + key);
            if (key == Key.Q && Keyboard.Modifiers == ModifierKeys.Alt) { search.Focus(); search.SelectAll(); e.Handled = true; return; }
            if (key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { OpenReply(); e.Handled = true; return; }
            if (key == Key.Return && Keyboard.Modifiers == ModifierKeys.Control) { sent = true; e.Handled = true; return; }
            if (key == Key.Return && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (search.IsKeyboardFocused)
                {
                    string q = search.Text.Trim();
                    lock (keys) searches.Add(q);
                    Fill(q.Length == 0 ? Mails.Take(10) : Mails.Where(m => $"{m.From} {m.Subject} {m.Preview}".Contains(q, StringComparison.OrdinalIgnoreCase)));
                    e.Handled = true;
                }
                else if (list.IsKeyboardFocusWithin) Open(list.SelectedIndex);
                else if (!reply.IsKeyboardFocused) enterElsewhere = true;
            }
        };
        window.Show();
        if (slow > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(slow) };
            timer.Tick += (_, _) => { timer.Stop(); Fill(Mails.Take(10)); };
            timer.Start();
        }
        else Fill(Mails.Take(10));

        Console.Out.WriteLine($"ready {new System.Windows.Interop.WindowInteropHelper(window).Handle}");
        Console.Out.Flush();
        void Front()
        {
            if (window.IsActive) return;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            SetForegroundWindow(handle);
            SwitchToThisWindow(handle, true);
            window.Activate();
        }
        if (args.Contains("--standalone")) return;
        new Thread(() =>
        {
            string? line;
            while ((line = Console.In.ReadLine()) != null)
            {
                string command = line, answer = "done";
                int busy = 0;
                window.Dispatcher.Invoke(() =>
                {
                    string[] parts = command.Split(' ', 2);
                    string arg = parts.Length > 1 ? parts[1] : "";
                    switch (parts[0])
                    {
                        case "focus":
                            Front();
                            if (arg == "list") { if (list.Items.Count > 0) (list.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) as ListBoxItem)?.Focus(); else list.Focus(); }
                            else if (arg == "search") search.Focus();
                            break;
                        case "reset":
                            reply.Text = "";
                            reply.Visibility = Visibility.Collapsed;
                            sent = enterElsewhere = false;
                            opened = "";
                            search.Text = "";
                            list.SelectedIndex = -1;
                            lock (keys) { keys.Clear(); searches.Clear(); }
                            if (showing.Count > 0 && showing.Count != 10) Fill(Mails.Take(10));
                            break;
                        case "busy": busy = int.Parse(arg); break;
                        case "state":
                            lock (keys)
                                answer = JsonSerializer.Serialize(new { opened, reply = reply.Text, replyOpen = reply.Visibility == Visibility.Visible, sent, enterElsewhere, keys = keys.ToArray(), searches = searches.ToArray(), shown = showing.Count, search = search.Text });
                            break;
                        case "quit": Application.Current.Shutdown(); break;
                    }
                });
                Console.Out.WriteLine(answer);
                Console.Out.Flush();
                // The app hangs for a while (its UI thread is busy), as a big app does right after it starts.
                if (busy > 0) window.Dispatcher.BeginInvoke(() => Thread.Sleep(busy));
                if (command == "quit") break;
            }
            window.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }) { IsBackground = true }.Start();
    }

    /// <summary>The same mail client as a web page, for Edge (a Chromium window, as new Outlook's WebView2 is): rows with role="option" and an aria-label, a search box, the list after <c>?delay=ms</c>. The page's title says what was opened and searched.</summary>
    internal static string WebPage()
    {
        string json = JsonSerializer.Serialize(Mails.Select(m => new { unread = m.Unread, from = m.From, subject = m.Subject, time = m.Time, preview = m.Preview }));
        return """
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Murmur web mail - Inbox</title>
            <style>
              body { font: 14px "Segoe UI", sans-serif; margin: 0; display: grid; grid-template-columns: 150px 340px 1fr; grid-template-rows: 44px 1fr; height: 100vh; }
              header { grid-column: 1 / 4; padding: 8px; background: #f3f3f3; } header input { width: 360px; padding: 4px; }
              nav, #list { border-right: 1px solid #ddd; overflow: auto; } [role=option] { padding: 6px 8px; border-bottom: 1px solid #eee; cursor: default; }
              [role=option][aria-selected=true] { background: #dbeafe; } [role=option] span { display: block; } .t { color: #777; } main { padding: 12px; }
            </style></head>
            <body>
            <header role="banner"><input id="q" type="search" aria-label="Search" placeholder="Search"> <button type="button">New mail</button></header>
            <nav><div role="tree" aria-label="Folders"><div role="treeitem" tabindex="0" aria-selected="true">Inbox</div><div role="treeitem" tabindex="-1" aria-selected="false">Sent Items</div><div role="treeitem" tabindex="-1" aria-selected="false">Archive</div></div></nav>
            <div id="list" role="listbox" aria-label="Message list"><div id="loading">Loading…</div></div>
            <main role="main"><h2 id="subj"></h2><div id="from"></div><p id="body"></p></main>
            <script>
            const mails = MAILS;
            const params = new URLSearchParams(location.search);
            const delay = +(params.get('delay') || 0);
            const list = document.getElementById('list'), q = document.getElementById('q');
            let state = { opened: '', searched: '' };
            function title() { document.title = 'Murmur web mail - Inbox' + (state.searched ? ' - searched: ' + state.searched : '') + (state.opened ? ' - opened: ' + state.opened : ''); }
            function open(m, row) {
              for (const r of list.querySelectorAll('[role=option]')) r.setAttribute('aria-selected', 'false');
              row.setAttribute('aria-selected', 'true');
              document.getElementById('subj').textContent = m.subject; document.getElementById('from').textContent = m.from; document.getElementById('body').textContent = m.preview;
              state.opened = m.subject; title();
            }
            function render(items) {
              list.innerHTML = '';
              if (items.length === 0) { list.innerHTML = '<div>We didn\'t find anything.</div>'; return; }
              for (const m of items) {
                const row = document.createElement('div');
                row.setAttribute('role', 'option'); row.setAttribute('aria-selected', 'false'); row.tabIndex = -1;
                row.setAttribute('aria-label', (m.unread ? 'Unread, ' : '') + m.from + ', ' + m.subject + ', ' + m.time + ', ' + m.preview);
                row.innerHTML = '<span></span><span></span><span class="t"></span>';
                row.children[0].textContent = m.from; row.children[1].textContent = m.subject; row.children[2].textContent = m.time;
                row.addEventListener('click', () => open(m, row));
                row.addEventListener('keydown', e => { if (e.key === 'Enter') open(m, row); });
                list.appendChild(row);
              }
            }
            q.addEventListener('keydown', e => {
              if (e.key !== 'Enter') return;
              const t = q.value.trim().toLowerCase();
              state.searched = t; title();
              render(t ? mails.filter(m => (m.from + ' ' + m.subject + ' ' + m.preview).toLowerCase().includes(t)) : mails.slice(0, 10));
            });
            document.addEventListener('keydown', e => { if (e.altKey && (e.key === 'q' || e.key === 'Q')) { e.preventDefault(); q.focus(); q.select(); } });
            setTimeout(() => render(mails.slice(0, 10)), delay);
            </script></body></html>
            """.Replace("MAILS", json);
    }
}
