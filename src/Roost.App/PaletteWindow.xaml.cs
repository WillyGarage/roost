using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Roost.Interop;

// WinForms is enabled in this project for the tray icon, so disambiguate the WPF types
// whose names it also defines.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Roost.App;

/// <summary>
/// The single window the whole app is driven from. One hotkey opens it, and every desktop
/// operation is a key on the highlighted row: move the active window, go there, create,
/// rename, reorder, delete.
///
/// There is no hidden menu. Every key is printed along the bottom, because a shortcut you
/// have to go looking for is a shortcut you will not use.
/// </summary>
public partial class PaletteWindow : Window
{
    /// <summary>What Enter does on a desktop row.</summary>
    public enum Mode
    {
        /// <summary>Opened with a window captured, so Ctrl/Alt+Enter can move it too. Enter still just goes there.</summary>
        MoveWindow,

        /// <summary>Opened with nothing captured, so there is no window to move. Enter just goes there.</summary>
        SwitchDesktop
    }

    private enum Stage
    {
        /// <summary>The desktop list. Everything starts and returns here.</summary>
        Desktops,

        /// <summary>Editing one desktop's name in the search box.</summary>
        Rename,

        /// <summary>Choosing where a doomed desktop's windows should go.</summary>
        DeleteChooseTarget
    }

    private sealed class Row
    {
        public string Display { get; init; } = "";

        /// <summary>Status words: current, recent, window is here.</summary>
        public string Badge { get; init; } = "";

        /// <summary>Window count, bare number, right-aligned in its own column.</summary>
        public string Count { get; init; } = "";

        /// <summary>Set on desktop rows.</summary>
        public Guid? DesktopId { get; init; }

        /// <summary>Set on the create-a-new-desktop row.</summary>
        public string? NewName { get; init; }

        /// <summary>False for informational rows that do nothing when chosen.</summary>
        public bool Selectable { get; init; } = true;

        public int Index { get; init; }
        public int Score { get; set; }
    }

    private readonly DesktopService _desktops;
    private readonly Config _config;
    private readonly AppState _state;
    private readonly Mode _mode;
    private readonly Action<string> _reportError;

    /// <summary>
    /// The window to act on, captured at hotkey time before this palette existed. Doing it
    /// any later would capture the palette itself.
    /// </summary>
    private readonly IntPtr _captured;

    private Stage _stage = Stage.Desktops;
    private bool _closing;

    /// <summary>Suppresses filtering while the search box is being used for a rename.</summary>
    private bool _suppressFilter;

    /// <summary>The desktop being renamed or deleted.</summary>
    private Guid _subject;

    private List<Row> _allDesktops = [];
    private Dictionary<Guid, List<IntPtr>> _windowsByDesktop = [];
    private Dictionary<Guid, int> _windowCounts = [];
    private Guid? _currentDesktop;
    private Guid? _capturedWindowDesktop;

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

        LoadDesktops();
        ShowDesktopStage(preserveQuery: false);

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Cancel();
    }

    // -----------------------------------------------------------------------
    // data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Re-reads desktops, names and window counts. Called on open and after any operation
    /// that changes the list, so what is on screen is never stale.
    /// </summary>
    private void LoadDesktops()
    {
        var desktops = _desktops.List();
        _state.SnapshotNames(desktops);

        // Grouped once, so the counts on each row and the list of windows a delete would
        // relocate are guaranteed to agree with each other.
        _windowsByDesktop = _desktops.GroupWindowsByDesktop();
        _windowCounts = _windowsByDesktop.ToDictionary(kv => kv.Key, kv => kv.Value.Count);

        _currentDesktop = _desktops.GetCurrentDesktopId(_captured);

        CurrentDesktopText.Text = desktops.FirstOrDefault(d => d.Id == _currentDesktop) is { } current
            ? $"You're on “{current.DisplayName}”."
            : "";

        _capturedWindowDesktop = _mode == Mode.MoveWindow && _captured != IntPtr.Zero
            ? _desktops.GetWindowDesktopId(_captured)
            : null;

        var recency = Recency();

        _allDesktops = desktops.Select(d => new Row
        {
            DesktopId = d.Id,
            Index = d.Index,
            Display = $"{d.Index + 1}.  {d.DisplayName}",
            Count = CountOn(d.Id).ToString(),
            Badge = BadgeFor(d, recency)
        }).ToList();
    }

    private Dictionary<Guid, int> Recency() =>
        _state.RecentDestinations
            .Take(Math.Max(_config.RecentDestinationCount, 0))
            .Select((id, i) => (id, i))
            .ToDictionary(x => x.id, x => x.i);

    private string BadgeFor(VirtualDesktopInfo d, Dictionary<Guid, int> recency)
    {
        var parts = new List<string>(3);

        if (d.Id == _currentDesktop)
            parts.Add("current");

        // Moving a window to where it already is does nothing, and without saying so the
        // tool looks broken when you try it.
        if (_capturedWindowDesktop is not null && d.Id == _capturedWindowDesktop && d.Id != _currentDesktop)
            parts.Add("window is here");

        if (recency.ContainsKey(d.Id))
            parts.Add("recent");

        return string.Join("  ·  ", parts);
    }

    private string NameOf(Guid id) =>
        _desktops.List().FirstOrDefault(d => d.Id == id)?.DisplayName ?? "that desktop";

    private int CountOn(Guid id) => _windowCounts.TryGetValue(id, out var n) ? n : 0;

    // -----------------------------------------------------------------------
    // stages
    // -----------------------------------------------------------------------

    private void ShowDesktopStage(bool preserveQuery, Guid? select = null)
    {
        _stage = Stage.Desktops;

        Query.Visibility = Visibility.Visible;

        if (!preserveQuery)
        {
            _suppressFilter = true;
            Query.Text = "";
            _suppressFilter = false;
        }

        var title = Native.GetWindowTitle(_captured);

        HeaderText.Text = _mode switch
        {
            Mode.MoveWindow when title.Length > 0 => $"Go, taking “{Trim(title, 70)}” along if you like:",
            Mode.MoveWindow => "Go, taking the active app along if you like:",
            _ => "Go to desktop:"
        };

        // Every key, spelled out. This is the only place they are documented in the UI, so
        // it has to be complete rather than tidy.
        var keys = new List<string>();

        keys.Add("Enter  go there");

        if (_mode == Mode.MoveWindow)
        {
            keys.Add("Ctrl+Enter  move app and stay");
            keys.Add("Alt+Enter  move app and follow");
        }

        keys.Add("F2  rename");
        keys.Add("Ctrl+↑ Ctrl+↓  reorder");
        keys.Add("Alt+Delete  delete");
        keys.Add("type a new name  create");
        keys.Add("Esc  cancel");

        FooterText.Text = string.Join("      ", keys);

        ApplyFilter(Query.Text);

        if (select is not null)
            SelectDesktop(select.Value);

        Query.Focus();
        Keyboard.Focus(Query);
    }

    private void ShowRenameStage(Guid desktopId)
    {
        _stage = Stage.Rename;
        _subject = desktopId;

        var current = NameOf(desktopId);

        HeaderText.Text = $"Rename  “{current}”  to:";
        FooterText.Text = "Enter  save      Esc  cancel";

        Query.Visibility = Visibility.Visible;

        // Reuse the search box as the edit field: no new UI, and the caret is already where
        // the user expects to type. Pre-selected so typing replaces the old name.
        _suppressFilter = true;
        Query.Text = current;
        _suppressFilter = false;

        Items.ItemsSource = new List<Row>
        {
            new()
            {
                Selectable = false,
                Display = CountOn(desktopId) == 0
                    ? "This desktop is empty."
                    : $"{CountOn(desktopId)} window(s) here. Renaming does not move anything."
            }
        };

        Query.Focus();
        Keyboard.Focus(Query);
        Query.SelectAll();
    }

    /// <summary>
    /// Deleting always relocates windows somewhere, so the destination is a choice rather
    /// than something to be guessed at. Defaults to the first desktop, which is the usual
    /// answer, and the list is filterable like any other.
    /// </summary>
    private void ShowDeleteStage(Guid desktopId)
    {
        var others = _desktops.List().Where(d => d.Id != desktopId).ToList();

        if (others.Count == 0)
        {
            _reportError("That is the only desktop, so it cannot be deleted.");
            return;
        }

        _stage = Stage.DeleteChooseTarget;
        _subject = desktopId;

        var count = CountOn(desktopId);

        HeaderText.Text = count == 0
            ? $"Delete  “{NameOf(desktopId)}”  (empty).  Enter to confirm:"
            : $"Delete  “{NameOf(desktopId)}”  and move its {count} window(s) to:";

        FooterText.Text = "Enter  delete and move the windows there      " +
                          "↑ ↓  choose      type to filter      Esc  cancel";

        Query.Visibility = Visibility.Visible;

        _suppressFilter = true;
        Query.Text = "";
        _suppressFilter = false;

        ApplyDeleteTargetFilter("");

        Query.Focus();
        Keyboard.Focus(Query);
    }

    private void ApplyDeleteTargetFilter(string query)
    {
        query = query.Trim();

        // Positional order, not recency: when choosing where windows should land, "which
        // desktop is where" is the useful mental model, and it keeps the default stable.
        var candidates = _desktops.List().Where(d => d.Id != _subject).ToList();

        var rows = new List<Row>();

        foreach (var d in candidates)
        {
            if (query.Length > 0 && FuzzyScore(d.DisplayName, query) < 0)
                continue;

            rows.Add(new Row
            {
                DesktopId = d.Id,
                Index = d.Index,
                Display = $"{d.Index + 1}.  {d.DisplayName}",
                Count = CountOn(d.Id).ToString(),
                Badge = d.Id == _currentDesktop ? "current" : ""
            });
        }

        Items.ItemsSource = rows;

        if (rows.Count > 0)
            Items.SelectedIndex = 0;
    }

    // -----------------------------------------------------------------------
    // filtering
    // -----------------------------------------------------------------------

    private void ApplyFilter(string query)
    {
        query = query.Trim();

        List<Row> rows;

        if (query.Length == 0)
        {
            var recency = Recency();

            rows = _allDesktops
                .OrderBy(r => recency.TryGetValue(r.DesktopId!.Value, out var rank) ? rank : int.MaxValue)
                .ThenBy(r => r.Index)
                .ToList();
        }
        else
        {
            rows = [];

            foreach (var row in _allDesktops)
            {
                var score = FuzzyScore(NameFromDisplay(row.Display), query);
                if (score < 0)
                    continue;

                row.Score = score;
                rows.Add(row);
            }

            rows = rows.OrderByDescending(r => r.Score).ThenBy(r => r.Index).ToList();

            var exact = _allDesktops.Any(r =>
                string.Equals(NameFromDisplay(r.Display), query, StringComparison.OrdinalIgnoreCase));

            if (!exact)
                rows.Add(new Row
                {
                    NewName = query,
                    Index = int.MaxValue,
                    Display = $"Create desktop  “{query}”",
                    Badge = _config.InsertNewDesktopAfterCurrent ? "new  ·  after current" : "new"
                });
        }

        Items.ItemsSource = rows;

        if (rows.Count > 0)
            Items.SelectedIndex = 0;
    }

    /// <summary>Strips the leading "12.  " position prefix so filtering matches names only.</summary>
    private static string NameFromDisplay(string display)
    {
        var dot = display.IndexOf('.');
        return dot < 0 ? display : display[(dot + 1)..].Trim();
    }

    /// <summary>
    /// Prefix beats substring beats subsequence, with shorter names winning ties so "co"
    /// prefers "Code" over "Client Projects". Returns -1 for no match.
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

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFilter)
            return;

        switch (_stage)
        {
            case Stage.Desktops:
                ApplyFilter(Query.Text);
                break;

            case Stage.DeleteChooseTarget:
                ApplyDeleteTargetFilter(Query.Text);
                break;
        }
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null)
            return;

        if (Items.SelectedItem is not Row { Selectable: true })
            return;

        e.Handled = true;

        if (_stage == Stage.DeleteChooseTarget)
            CommitDelete();
        else if (_stage == Stage.Desktops)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            Confirm(alt ? ConfirmAction.MoveAndFollow : ctrl ? ConfirmAction.MoveAndStay : ConfirmAction.Go);
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // WPF reports any Alt+key combination as Key.System and puts the real key in
        // SystemKey. Comparing against e.Key directly means every Alt binding silently
        // never fires, which is exactly how Alt+Enter and Alt+Delete were both dead.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Escape means "back one step", and only closes from the desktop list. That makes
        // renaming and deleting safe to back out of.
        if (key == Key.Escape)
        {
            e.Handled = true;

            if (_stage == Stage.Desktops)
                Cancel();
            else
                ShowDesktopStage(preserveQuery: false, select: _subject);

            return;
        }

        if (key is Key.Up or Key.Down or Key.PageUp or Key.PageDown && !ctrl)
        {
            // Reordering owns Ctrl+arrows; plain arrows always move the selection.
            e.Handled = true;
            MoveSelection(key switch
            {
                Key.Up => -1,
                Key.Down => 1,
                Key.PageUp => -8,
                _ => 8
            });
            return;
        }

        switch (_stage)
        {
            case Stage.Desktops:
                OnDesktopKey(e, key, ctrl, alt);
                break;

            case Stage.Rename:
                if (key == Key.Enter)
                {
                    e.Handled = true;
                    CommitRename();
                }
                break;

            case Stage.DeleteChooseTarget:
                if (key == Key.Enter)
                {
                    e.Handled = true;
                    CommitDelete();
                }
                break;
        }
    }

    private void OnDesktopKey(KeyEventArgs e, Key key, bool ctrl, bool alt)
    {
        var selected = Items.SelectedItem as Row;

        switch (key)
        {
            case Key.Enter:
                e.Handled = true;
                Confirm(alt ? ConfirmAction.MoveAndFollow : ctrl ? ConfirmAction.MoveAndStay : ConfirmAction.Go);
                break;

            case Key.F2 when selected?.DesktopId is { } toRename:
                e.Handled = true;
                ShowRenameStage(toRename);
                break;

            case Key.Delete when alt && selected?.DesktopId is { } toDelete:
                e.Handled = true;
                ShowDeleteStage(toDelete);
                break;

            case Key.Up when ctrl && selected?.DesktopId is { } up:
                e.Handled = true;
                Reorder(up, -1);
                break;

            case Key.Down when ctrl && selected?.DesktopId is { } down:
                e.Handled = true;
                Reorder(down, +1);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = Items.Items.Count;
        if (count == 0)
            return;

        // Skip past informational rows so the selection never lands somewhere inert.
        var next = Math.Clamp(Items.SelectedIndex + delta, 0, count - 1);

        if (Items.Items[next] is Row { Selectable: false })
            return;

        Items.SelectedIndex = next;
        Items.ScrollIntoView(Items.Items[next]);
    }

    private static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
    {
        while (from is not null and not T)
            from = System.Windows.Media.VisualTreeHelper.GetParent(from);

        return from as T;
    }

    // -----------------------------------------------------------------------
    // actions
    // -----------------------------------------------------------------------

    /// <summary>What Enter/Ctrl+Enter/Alt+Enter does on the desktop list.</summary>
    private enum ConfirmAction
    {
        /// <summary>Just go there, moving nothing. Enter.</summary>
        Go,

        /// <summary>Move the captured window there and stay put. Ctrl+Enter.</summary>
        MoveAndStay,

        /// <summary>Move the captured window there and follow it. Alt+Enter.</summary>
        MoveAndFollow
    }

    /// <summary>Enter/Ctrl+Enter/Alt+Enter on the desktop list: go, move-and-stay, or
    /// move-and-follow. Also handles create when the row is a new name.</summary>
    private void Confirm(ConfirmAction action)
    {
        if (_closing || Items.SelectedItem is not Row row || !row.Selectable)
            return;

        if (row.NewName is not null)
        {
            CreateAndUse(row.NewName, action);
            return;
        }

        if (row.DesktopId is not { } target)
            return;

        if (_mode == Mode.MoveWindow && _captured != IntPtr.Zero && action != ConfirmAction.Go)
            MoveAndClose(target, follow: action == ConfirmAction.MoveAndFollow);
        else
            SwitchAndClose(target);
    }

    /// <summary>
    /// Remembers the desktop we are leaving so it floats to the top of the list next time
    /// the palette opens. That makes the very next "hotkey, Enter" a return to where we
    /// just were, the way Alt+Tab returns to the previous window. Recorded for every action
    /// that lands us on a different desktop: go, move-and-follow, and create-and-go.
    /// </summary>
    private void RecordDeparture(Guid target)
    {
        if (_currentDesktop is { } from && from != target)
            _state.RecordDestination(from, _config.RecentDestinationCount);
    }

    /// <summary>
    /// Keeps the view on the desktop we are on after a move-and-stay. Delegates to
    /// DesktopService.HoldForegroundOn, which must run while the palette still owns the
    /// foreground, i.e. after Hide but before Close.
    /// </summary>
    private void StayPut()
    {
        if (_currentDesktop is { } here)
            _desktops.HoldForegroundOn(here, exclude: _captured);
    }

    /// <summary>
    /// Hides and acts while this window still owns the foreground. Switching depends on
    /// that: DesktopService.SwitchTo has to claim the foreground on the destination to stop
    /// the previously focused window pulling us back, and Windows only honours that from
    /// the process that currently has it. Closing first would surrender the right too soon.
    /// The same ownership is what lets a move-and-stay anchor the source desktop via
    /// <see cref="StayPut"/> before closing.
    /// </summary>
    private void MoveAndClose(Guid target, bool follow)
    {
        _closing = true;
        Hide();

        try
        {
            // Already there: moving would be a no-op, so honour the follow-or-stay intent
            // rather than reporting a success that changed nothing.
            if (_capturedWindowDesktop == target)
            {
                if (follow)
                {
                    if (_currentDesktop != target)
                    {
                        if (_desktops.SwitchTo(target, out var goError))
                            RecordDeparture(target);
                        else
                            _reportError(goError ?? "Could not switch desktops.");
                    }
                }
                else if (target != _currentDesktop)
                {
                    // The window already lives on another desktop and we are staying put;
                    // anchor the view here so closing does not chase the window over.
                    StayPut();
                }

                return;
            }

            if (_desktops.MoveWindow(_captured, target, follow, out var moveError))
            {
                _state.RecordDestination(target, _config.RecentDestinationCount);

                if (follow)
                    RecordDeparture(target);
                else
                    StayPut();
            }
            else
            {
                _reportError(moveError ?? "Could not move the window.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("unexpected failure moving a window", ex);
            _reportError($"Something went wrong: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private void SwitchAndClose(Guid target)
    {
        _closing = true;
        Hide();

        try
        {
            if (_desktops.SwitchTo(target, out var error))
                RecordDeparture(target);
            else
                _reportError(error ?? "Could not switch desktops.");
        }
        catch (Exception ex)
        {
            Log.Error("unexpected failure switching desktops", ex);
            _reportError($"Something went wrong: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private void CreateAndUse(string name, ConfirmAction action)
    {
        _closing = true;
        Hide();

        try
        {
            if (!_desktops.CreateDesktop(
                    name, _config.InsertNewDesktopAfterCurrent, out var created, out var createError))
            {
                _reportError(createError ?? "Could not create the desktop.");
                return;
            }

            _state.RecordCreated(created);
            _state.SnapshotNames(_desktops.List());

            if (_mode == Mode.MoveWindow && _captured != IntPtr.Zero && action != ConfirmAction.Go)
            {
                var follow = action == ConfirmAction.MoveAndFollow;

                if (_desktops.MoveWindow(_captured, created, follow, out var moveError))
                {
                    _state.RecordDestination(created, _config.RecentDestinationCount);

                    if (follow)
                        RecordDeparture(created);
                    else
                        StayPut();
                }
                else
                {
                    _reportError(moveError ?? "Could not move the window.");
                }
            }
            else if (_desktops.SwitchTo(created, out var switchError))
            {
                RecordDeparture(created);
            }
            else
            {
                _reportError(switchError ?? "Could not switch to the new desktop.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("unexpected failure creating a desktop", ex);
            _reportError($"Something went wrong: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    /// <summary>
    /// Reorder in place, staying open. The list rebuilds immediately with the moved desktop
    /// still selected, so repeated presses walk it along visibly.
    /// </summary>
    private void Reorder(Guid desktopId, int delta)
    {
        var position = _desktops.List().FirstOrDefault(d => d.Id == desktopId)?.Index;

        if (position is null)
            return;

        var wanted = position.Value + delta;

        if (wanted < 0 || wanted >= _desktops.List().Count)
            return; // already at the end; silently ignore rather than flash an error

        if (!_desktops.ReorderDesktop(desktopId, wanted, out var error))
        {
            _reportError(error ?? "Could not reorder the desktop.");
            return;
        }

        LoadDesktops();
        ShowDesktopStage(preserveQuery: true, select: desktopId);
    }

    private void CommitRename()
    {
        var name = Query.Text.Trim();

        if (name.Length == 0)
        {
            _reportError("A desktop name cannot be empty.");
            return;
        }

        if (!_desktops.RenameDesktop(_subject, name, out var error))
        {
            _reportError(error ?? "Could not rename the desktop.");
            return;
        }

        LoadDesktops();
        _state.SnapshotNames(_desktops.List());
        ShowDesktopStage(preserveQuery: false, select: _subject);
    }

    private void CommitDelete()
    {
        if (Items.SelectedItem is not Row { DesktopId: { } fallback })
        {
            _reportError("Choose a desktop for the windows to move to.");
            return;
        }

        var deleted = _subject;
        var name = NameOf(deleted);

        if (!_desktops.DeleteDesktop(deleted, fallback, out var error))
        {
            _reportError(error ?? "Could not delete the desktop.");
            ShowDesktopStage(preserveQuery: false, select: deleted);
            return;
        }

        Log.Info($"deleted \"{name}\" from the palette, windows moved to \"{NameOf(fallback)}\"");

        // Drop it from recents so a deleted desktop cannot sit at the top of the list.
        _state.RecentDestinations.Remove(deleted);

        if (_state.LastCreatedDesktop == deleted)
            _state.LastCreatedDesktop = null;

        _state.Save();

        LoadDesktops();
        ShowDesktopStage(preserveQuery: false, select: fallback);
    }

    private void SelectDesktop(Guid id)
    {
        if (Items.ItemsSource is not IEnumerable<Row> rows)
            return;

        var list = rows.ToList();
        var at = list.FindIndex(r => r.DesktopId == id);

        if (at < 0)
            return;

        Items.SelectedIndex = at;
        Items.ScrollIntoView(list[at]);
    }

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

    // -----------------------------------------------------------------------
    // sizing and placement
    // -----------------------------------------------------------------------

    /// <summary>
    /// Lets the list grow to show every desktop, capped only by what the monitor can
    /// actually display. There is no reason to scroll a list that would fit.
    /// </summary>
    private void FitToMonitor()
    {
        var reference = _captured != IntPtr.Zero
            ? _captured
            : new System.Windows.Interop.WindowInteropHelper(this).Handle;

        var screen = reference != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(reference)
            : System.Windows.Forms.Screen.PrimaryScreen;

        var area = screen?.WorkingArea ?? System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;

        // Working area is in physical pixels; the list's MaxHeight is in WPF units. Ask the
        // monitor for its scale rather than reading WPF's, which is only correct once the
        // window is actually on that monitor.
        var scale = Native.GetMonitorScale(reference);
        var budget = area.Height / scale - 40;

        // Whatever the window measures beyond the list is the fixed chrome: header, search
        // box, footer, margins and the drop shadow's margin. Measuring it beats hardcoding.
        Items.MaxHeight = double.PositiveInfinity;
        UpdateLayout();

        var chrome = Math.Max(ActualHeight - Items.ActualHeight, 0);

        Items.MaxHeight = Math.Max(160, budget - chrome);
        UpdateLayout();
    }

    /// <summary>
    /// Centres the palette on the monitor holding <paramref name="reference"/>.
    ///
    /// Done in physical pixels through SetWindowPos rather than WPF's Left/Top, because on a
    /// mixed-DPI setup (laptop panel plus external monitor) WPF's device-independent units
    /// are relative to a different monitor's scaling and the window lands somewhere
    /// unexpected.
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

        // Slightly above centre reads better than dead centre, but a tall list should not
        // be pushed off the bottom of the screen.
        var y = area.Top + (int)Math.Max((area.Height - rect.Height) * 0.32, 0);

        Native.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Shows the palette and puts the caret in the search box.</summary>
    public void ShowPalette()
    {
        Show();
        FitToMonitor();
        CentreOn(_captured);
        Activate();
        Query.Focus();
        Keyboard.Focus(Query);
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
