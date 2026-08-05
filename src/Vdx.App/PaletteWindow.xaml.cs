using System.Windows;
using System.Windows.Input;
using Vdx.Interop;

// WinForms is enabled in this project for the tray icon, so disambiguate the WPF types
// whose names it also defines.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Vdx.App;

/// <summary>
/// The type-to-search palette. This is the primary UI on purpose: with a dozen or more
/// desktops, a popup menu reproduces exactly the scrolling problem the tool exists to
/// remove, whereas typing three characters and pressing Enter does not.
/// </summary>
public partial class PaletteWindow : Window
{
    public enum Mode
    {
        /// <summary>Move the captured window to the chosen desktop.</summary>
        MoveWindow,

        /// <summary>Just go to the chosen desktop. A Win+Tab replacement.</summary>
        SwitchDesktop
    }

    /// <summary>One row. Either an existing desktop, or the create-a-new-one action.</summary>
    private sealed class Row
    {
        public string Display { get; init; } = "";
        public string Badge { get; init; } = "";

        /// <summary>Null means "create a new desktop named <see cref="NewName"/>".</summary>
        public Guid? DesktopId { get; init; }

        public string? NewName { get; init; }

        /// <summary>Sort key from the fuzzy match; higher is better.</summary>
        public int Score { get; set; }

        /// <summary>Position in the desktop order, used to break score ties.</summary>
        public int Index { get; init; }
    }

    private readonly DesktopService _desktops;
    private readonly Config _config;
    private readonly AppState _state;
    private readonly Mode _mode;

    /// <summary>
    /// The window to act on, captured at hotkey time before this palette existed. Doing
    /// it any later would capture the palette itself.
    /// </summary>
    private readonly IntPtr _captured;

    private readonly Action<string> _reportError;
    private List<Row> _all = [];
    private bool _closing;

    public PaletteWindow(
        DesktopService desktops,
        Config config,
        AppState state,
        Mode mode,
        IntPtr capturedWindow,
        Action<string> reportError)
    {
        InitializeComponent();

        _desktops = desktops;
        _config = config;
        _state = state;
        _mode = mode;
        _captured = capturedWindow;
        _reportError = reportError;

        Build();

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Cancel();
    }

    // -----------------------------------------------------------------------
    // building the list
    // -----------------------------------------------------------------------

    private void Build()
    {
        var desktops = _desktops.List();
        _state.SnapshotNames(desktops);

        var current = _desktops.GetCurrentDesktopId(_captured);
        var windowDesktop = _mode == Mode.MoveWindow ? _desktops.GetWindowDesktopId(_captured) : null;

        // Recency rank, so an empty query surfaces where you have been sending things.
        var recency = _state.RecentDestinations
            .Take(Math.Max(_config.RecentDestinationCount, 0))
            .Select((id, i) => (id, i))
            .ToDictionary(x => x.id, x => x.i);

        _all = desktops.Select(d => new Row
        {
            DesktopId = d.Id,
            Index = d.Index,
            Display = $"{d.Index + 1}.  {d.DisplayName}",
            Badge = BadgeFor(d, current, windowDesktop, recency)
        }).ToList();

        var title = Native.GetWindowTitle(_captured);

        HeaderText.Text = _mode switch
        {
            Mode.MoveWindow when title.Length > 0 => $"Move  “{title}”  to:",
            Mode.MoveWindow => "Move the active window to:",
            _ => "Switch to desktop:"
        };

        FooterText.Text = _mode == Mode.MoveWindow
            ? $"Enter  move{(_config.FollowWindowAfterMove ? " and follow" : "")}     " +
              $"Ctrl+Enter  move{(_config.FollowWindowAfterMove ? " and stay" : " and follow")}     " +
              "type a name to create a new desktop     Esc  cancel"
            : "Enter  switch     type a name to create a new desktop     Esc  cancel";

        ApplyFilter("");
    }

    private string BadgeFor(
        VirtualDesktopInfo d, Guid? current, Guid? windowDesktop, Dictionary<Guid, int> recency)
    {
        var parts = new List<string>(2);

        if (d.Id == current)
            parts.Add("current");

        // Worth calling out: moving a window to the desktop it is already on is a no-op,
        // and without this it looks like the tool did nothing.
        if (windowDesktop is not null && d.Id == windowDesktop && d.Id != current)
            parts.Add("window is here");

        if (recency.ContainsKey(d.Id))
            parts.Add("recent");

        return string.Join(" · ", parts);
    }

    private void ApplyFilter(string query)
    {
        query = query.Trim();

        List<Row> rows;

        if (query.Length == 0)
        {
            // Recents first, then everything else in desktop order.
            var recency = _state.RecentDestinations
                .Take(Math.Max(_config.RecentDestinationCount, 0))
                .Select((id, i) => (id, i))
                .ToDictionary(x => x.id, x => x.i);

            rows = _all
                .OrderBy(r => recency.TryGetValue(r.DesktopId!.Value, out var rank) ? rank : int.MaxValue)
                .ThenBy(r => r.Index)
                .ToList();
        }
        else
        {
            rows = [];

            foreach (var row in _all)
            {
                // Match against the name only, not the leading position number, so
                // typing "3" does not fight with a desktop called "GLP-1".
                var name = row.Display[(row.Display.IndexOf('.') + 1)..].Trim();
                var score = FuzzyScore(name, query);

                if (score < 0)
                    continue;

                row.Score = score;
                rows.Add(row);
            }

            rows = rows.OrderByDescending(r => r.Score).ThenBy(r => r.Index).ToList();

            // Offer creation unless the query already names an existing desktop exactly.
            var exact = _all.Any(r =>
                string.Equals(
                    r.Display[(r.Display.IndexOf('.') + 1)..].Trim(),
                    query,
                    StringComparison.OrdinalIgnoreCase));

            if (!exact)
                rows.Add(new Row
                {
                    DesktopId = null,
                    NewName = query,
                    Display = $"Create desktop  “{query}”",
                    Badge = _config.InsertNewDesktopAfterCurrent ? "new · after current" : "new",
                    Index = int.MaxValue
                });
        }

        Items.ItemsSource = rows;

        if (rows.Count > 0)
            Items.SelectedIndex = 0;
    }

    /// <summary>
    /// Prefix beats substring beats subsequence, with shorter names winning ties so
    /// "co" prefers "Comm" over "Ground Model". Returns -1 for no match.
    /// </summary>
    private static int FuzzyScore(string text, string query)
    {
        if (query.Length == 0)
            return 0;

        var t = text.ToLowerInvariant();
        var q = query.ToLowerInvariant();

        if (t == q)
            return 10_000;

        if (t.StartsWith(q, StringComparison.Ordinal))
            return 9_000 - t.Length;

        var at = t.IndexOf(q, StringComparison.Ordinal);
        if (at >= 0)
            return 7_000 - at * 10 - t.Length;

        // Subsequence: every query character appears in order. Tighter runs score higher.
        var cursor = 0;
        var first = -1;

        foreach (var c in q)
        {
            var found = t.IndexOf(c, cursor);
            if (found < 0)
                return -1;

            if (first < 0)
                first = found;

            cursor = found + 1;
        }

        return 4_000 - (cursor - first - q.Length) * 10 - first;
    }

    // -----------------------------------------------------------------------
    // input
    // -----------------------------------------------------------------------

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyFilter(Query.Text);

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e) =>
        Commit(invertFollow: false);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Cancel();
                break;

            case Key.Enter:
                e.Handled = true;
                Commit(invertFollow: ctrl);
                break;

            case Key.Down:
                e.Handled = true;
                Move(1);
                break;

            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;

            case Key.PageDown:
                e.Handled = true;
                Move(8);
                break;

            case Key.PageUp:
                e.Handled = true;
                Move(-8);
                break;
        }
    }

    private void Move(int delta)
    {
        var count = Items.Items.Count;
        if (count == 0)
            return;

        var next = Math.Clamp(Items.SelectedIndex + delta, 0, count - 1);
        Items.SelectedIndex = next;
        Items.ScrollIntoView(Items.Items[next]);
    }

    // -----------------------------------------------------------------------
    // actions
    // -----------------------------------------------------------------------

    private void Cancel()
    {
        if (_closing)
            return;

        _closing = true;

        // Hand focus back to where the user was, so cancelling is a true no-op.
        if (_captured != IntPtr.Zero)
            Native.SetForegroundWindow(_captured);

        Close();
    }

    private void Commit(bool invertFollow)
    {
        if (_closing || Items.SelectedItem is not Row row)
            return;

        _closing = true;
        Hide();

        var follow = _config.FollowWindowAfterMove ^ invertFollow;

        try
        {
            var target = row.DesktopId;

            if (target is null)
            {
                if (!_desktops.CreateDesktop(
                        row.NewName ?? "", _config.InsertNewDesktopAfterCurrent,
                        out var created, out var createError))
                {
                    _reportError(createError ?? "Could not create the desktop.");
                    Close();
                    return;
                }

                _state.RecordCreated(created);
                target = created;

                // A brand new desktop is empty, so following it is almost always what
                // you want even in switch mode.
                follow = true;
            }

            switch (_mode)
            {
                case Mode.MoveWindow:
                    if (_captured == IntPtr.Zero)
                    {
                        _reportError("There was no active window to move.");
                        break;
                    }

                    if (_desktops.MoveWindow(_captured, target.Value, follow, out var moveError))
                        _state.RecordDestination(target.Value, _config.RecentDestinationCount);
                    else
                        _reportError(moveError ?? "Could not move the window.");
                    break;

                case Mode.SwitchDesktop:
                    if (!_desktops.SwitchTo(target.Value, out var switchError))
                        _reportError(switchError ?? "Could not switch desktops.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("unexpected failure committing palette action", ex);
            _reportError($"Something went wrong: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    // -----------------------------------------------------------------------
    // placement
    // -----------------------------------------------------------------------

    /// <summary>
    /// Centres the palette on the monitor holding <paramref name="reference"/>.
    ///
    /// Done in physical pixels through SetWindowPos rather than WPF's Left/Top, because
    /// on a mixed-DPI setup (laptop panel plus external monitor) WPF's device-independent
    /// units are relative to a different monitor's scaling and the window lands
    /// somewhere unexpected.
    /// </summary>
    public void CentreOn(IntPtr reference)
    {
        var screen = reference != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(reference)
            : System.Windows.Forms.Screen.PrimaryScreen;

        var area = screen?.WorkingArea ?? System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !Native.GetWindowRect(handle, out var rect))
            return;

        var x = area.Left + (area.Width - rect.Width) / 2;

        // Slightly above centre reads better than dead centre for a palette.
        var y = area.Top + (int)((area.Height - rect.Height) * 0.32);

        Native.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Shows the palette and puts the caret in the search box.</summary>
    public void ShowPalette()
    {
        Show();
        CentreOn(_captured);
        Activate();
        Query.Focus();
        Keyboard.Focus(Query);
    }
}
