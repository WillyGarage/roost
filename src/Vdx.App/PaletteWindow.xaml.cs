using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vdx.Interop;

// WinForms is enabled in this project for the tray icon, so disambiguate the WPF types
// whose names it also defines.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Vdx.App;

/// <summary>
/// The single window the whole app is driven from. One hotkey opens it, and every desktop
/// operation is reachable from here: move the active window, switch, create, rename,
/// reorder, delete.
///
/// Design rule: the common case must not pay for the rare ones. Moving the active window
/// stays "hotkey, type three letters, Enter". Everything else is a distinct key on the
/// selected row, with Tab opening a discoverable list of what those keys are, so nothing
/// has to be memorised to be found.
/// </summary>
public partial class PaletteWindow : Window
{
    /// <summary>What Enter does on a desktop row.</summary>
    public enum Mode
    {
        /// <summary>Move the captured window there. The default.</summary>
        MoveWindow,

        /// <summary>Just go there. Used when there is no movable window to act on.</summary>
        SwitchDesktop
    }

    /// <summary>Which screen of the palette is showing.</summary>
    private enum Stage
    {
        /// <summary>The desktop list. Everything starts and returns here.</summary>
        Desktops,

        /// <summary>What-can-I-do-with-this list for one desktop.</summary>
        Actions,

        /// <summary>Editing one desktop's name in the search box.</summary>
        Rename,

        /// <summary>Showing exactly what a delete would affect, awaiting confirmation.</summary>
        ConfirmDelete
    }

    private enum Act
    {
        MoveHere,
        MoveHereAndStay,
        SwitchTo,
        Rename,
        MoveEarlier,
        MoveLater,
        Delete
    }

    private sealed class Row
    {
        public string Display { get; init; } = "";
        public string Badge { get; init; } = "";

        /// <summary>Set on desktop rows.</summary>
        public Guid? DesktopId { get; init; }

        /// <summary>Set on the create-a-new-desktop row.</summary>
        public string? NewName { get; init; }

        /// <summary>Set on rows in the Actions stage.</summary>
        public Act? Action { get; init; }

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

    /// <summary>The desktop the Actions / Rename / ConfirmDelete stages are working on.</summary>
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

        // Grouped once, so the counts on each row and the list shown before a delete are
        // guaranteed to agree with each other.
        _windowsByDesktop = _desktops.GroupWindowsByDesktop();
        _windowCounts = _windowsByDesktop.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        _currentDesktop = _desktops.GetCurrentDesktopId(_captured);
        _capturedWindowDesktop = _mode == Mode.MoveWindow && _captured != IntPtr.Zero
            ? _desktops.GetWindowDesktopId(_captured)
            : null;

        var recency = Recency();

        _allDesktops = desktops.Select(d => new Row
        {
            DesktopId = d.Id,
            Index = d.Index,
            Display = $"{d.Index + 1}.  {d.DisplayName}",
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
        var parts = new List<string>(4);

        // Window count first: it is the most useful thing on the row. It tells you which
        // desktops are live and, just as usefully, which are empty and worth deleting.
        var count = _windowCounts.TryGetValue(d.Id, out var n) ? n : 0;
        parts.Add(count switch
        {
            0 => "empty",
            1 => "1 window",
            _ => $"{count} windows"
        });

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
            Mode.MoveWindow when title.Length > 0 => $"Move  “{Trim(title, 60)}”  to:",
            Mode.MoveWindow => "Move the active window to:",
            _ => "Go to desktop:"
        };

        FooterText.Text = _mode == Mode.MoveWindow
            ? "Enter  move" + (_config.FollowWindowAfterMove ? " and follow" : "") +
              "     Ctrl+Enter  " + (_config.FollowWindowAfterMove ? "move and stay" : "move and follow") +
              "     Alt+Enter  just go there     Tab  more…     Esc  cancel"
            : "Enter  go there     Tab  more…     Esc  cancel";

        ApplyFilter(Query.Text);

        if (select is not null)
            SelectDesktop(select.Value);

        Query.Focus();
        Keyboard.Focus(Query);
    }

    private void ShowActionsStage(Guid desktopId)
    {
        _stage = Stage.Actions;
        _subject = desktopId;

        // The search box has no meaning here, and hiding it makes the change of context
        // obvious rather than leaving a box that silently ignores typing.
        Query.Visibility = Visibility.Collapsed;

        var count = CountOn(desktopId);
        var position = _desktops.List().FirstOrDefault(d => d.Id == desktopId)?.Index ?? 0;
        var total = _desktops.List().Count;

        HeaderText.Text = $"“{NameOf(desktopId)}”  —  position {position + 1} of {total}, " +
                          (count == 1 ? "1 window" : $"{count} windows");

        var rows = new List<Row>();
        var index = 0;

        if (_mode == Mode.MoveWindow && _captured != IntPtr.Zero)
        {
            rows.Add(new Row
            {
                Action = Act.MoveHere, Index = index++,
                Display = "Move the window here and follow it", Badge = "M"
            });
            rows.Add(new Row
            {
                Action = Act.MoveHereAndStay, Index = index++,
                Display = "Move the window here, stay where I am", Badge = "K"
            });
        }

        rows.Add(new Row
        {
            Action = Act.SwitchTo, Index = index++,
            Display = "Go to this desktop", Badge = "G"
        });
        rows.Add(new Row
        {
            Action = Act.Rename, Index = index++,
            Display = "Rename this desktop", Badge = "R  or  F2"
        });
        rows.Add(new Row
        {
            Action = Act.MoveEarlier, Index = index++,
            Display = "Move it one position earlier", Badge = "[  or  Ctrl+↑"
        });
        rows.Add(new Row
        {
            Action = Act.MoveLater, Index = index++,
            Display = "Move it one position later", Badge = "]  or  Ctrl+↓"
        });
        rows.Add(new Row
        {
            Action = Act.Delete, Index = index++,
            Display = "Delete this desktop", Badge = "D  or  Alt+Del"
        });

        Items.ItemsSource = rows;
        Items.SelectedIndex = 0;

        FooterText.Text = "Enter  do it     Esc  back to the list";

        // Focus the list rather than the hidden search box so arrow keys work natively.
        Items.Focus();
        Keyboard.Focus(Items);
    }

    private void ShowRenameStage(Guid desktopId)
    {
        _stage = Stage.Rename;
        _subject = desktopId;

        var current = NameOf(desktopId);

        HeaderText.Text = $"Rename  “{current}”  to:";
        FooterText.Text = "Enter  save     Esc  cancel";

        Query.Visibility = Visibility.Visible;

        // Reuse the search box as the edit field: no new UI, and the caret is already
        // where the user expects to type. Pre-selected so typing replaces the old name.
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
                    : $"{CountOn(desktopId)} window(s) here. Renaming does not move anything.",
                Badge = ""
            }
        };

        Query.Focus();
        Keyboard.Focus(Query);
        Query.SelectAll();
    }

    private void ShowConfirmDeleteStage(Guid desktopId)
    {
        _stage = Stage.ConfirmDelete;
        _subject = desktopId;

        Query.Visibility = Visibility.Collapsed;

        var fallback = _desktops.FallbackFor(desktopId);
        var count = CountOn(desktopId);

        if (fallback is null)
        {
            _reportError("That is the only desktop, so it cannot be deleted.");
            ShowDesktopStage(preserveQuery: true, select: desktopId);
            return;
        }

        HeaderText.Text = count == 0
            ? $"Delete  “{NameOf(desktopId)}”?  It is empty."
            : $"Delete  “{NameOf(desktopId)}”?  Its {count} window(s) will move to " +
              $"“{fallback.DisplayName}”.";

        // Show exactly which windows are affected. A confirmation that names the
        // consequences is worth far more than one that just asks "are you sure".
        var rows = new List<Row>();
        var index = 0;

        if (_windowsByDesktop.TryGetValue(desktopId, out var affected))
            foreach (var hWnd in affected)
                rows.Add(new Row
                {
                    Selectable = false,
                    Index = index++,
                    Display = "→  " + Trim(Native.GetWindowTitle(hWnd), 70),
                    Badge = ""
                });

        if (rows.Count == 0)
            rows.Add(new Row { Selectable = false, Display = "Nothing to move.", Badge = "" });

        Items.ItemsSource = rows;
        Items.SelectedIndex = -1;

        FooterText.Text = "Enter  delete it     Esc  cancel";

        Items.Focus();
        Keyboard.Focus(Items);
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
    /// prefers "Comm" over "Ground Model". Returns -1 for no match.
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
        if (_suppressFilter || _stage != Stage.Desktops)
            return;

        ApplyFilter(Query.Text);
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null)
            return;

        if (Items.SelectedItem is Row { Selectable: false })
            return;

        e.Handled = true;
        Confirm(invertFollow: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // WPF reports any Alt+key combination as Key.System and puts the real key in
        // SystemKey. Comparing against e.Key directly means every Alt binding silently
        // never fires, which is exactly how Alt+Enter and Alt+Delete were both dead.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Escape means "back one step", and only closes from the top level. That makes it
        // safe to explore the deeper stages without losing your place.
        if (key == Key.Escape)
        {
            e.Handled = true;

            if (_stage == Stage.Desktops)
                Cancel();
            else
                ShowDesktopStage(preserveQuery: true, select: _subject);

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

            case Stage.Actions:
                OnActionsKey(e, key);
                break;

            case Stage.Rename:
                if (key == Key.Enter)
                {
                    e.Handled = true;
                    CommitRename();
                }
                break;

            case Stage.ConfirmDelete:
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

                if (alt && selected?.DesktopId is { } goTo)
                    RunAction(Act.SwitchTo, goTo);
                else
                    Confirm(invertFollow: ctrl);
                break;

            // Tab opens the discoverable list of everything else this row can do. This is
            // what stops the extra verbs from being invisible keyboard trivia.
            case Key.Tab when selected?.DesktopId is { } forActions:
                e.Handled = true;
                ShowActionsStage(forActions);
                break;

            case Key.Right when selected?.DesktopId is { } forActions2 && Query.CaretIndex == Query.Text.Length:
                e.Handled = true;
                ShowActionsStage(forActions2);
                break;

            case Key.F2 when selected?.DesktopId is { } toRename:
                e.Handled = true;
                ShowRenameStage(toRename);
                break;

            case Key.Delete when alt && selected?.DesktopId is { } toDelete:
                e.Handled = true;
                ShowConfirmDeleteStage(toDelete);
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

    private void OnActionsKey(KeyEventArgs e, Key key)
    {
        // Single-letter accelerators, shown on each row so they are learned by using them.
        var act = key switch
        {
            Key.Enter => (Items.SelectedItem as Row)?.Action,
            Key.M => Act.MoveHere,
            Key.K => Act.MoveHereAndStay,
            Key.G => Act.SwitchTo,
            Key.R or Key.F2 => Act.Rename,
            Key.OemOpenBrackets => Act.MoveEarlier,
            Key.OemCloseBrackets => Act.MoveLater,
            Key.D or Key.Delete => Act.Delete,
            _ => null
        };

        if (act is null)
            return;

        e.Handled = true;

        // Moving the window is only offered when there is one to move.
        if (act is Act.MoveHere or Act.MoveHereAndStay
            && (_mode != Mode.MoveWindow || _captured == IntPtr.Zero))
            return;

        RunAction(act.Value, _subject);
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

    /// <summary>Enter on the desktop list: the primary action, or create-and-move.</summary>
    private void Confirm(bool invertFollow)
    {
        if (_closing || Items.SelectedItem is not Row row || !row.Selectable)
            return;

        if (row.NewName is not null)
        {
            CreateAndUse(row.NewName, invertFollow);
            return;
        }

        if (row.DesktopId is not { } target)
            return;

        var follow = _config.FollowWindowAfterMove ^ invertFollow;

        if (_mode == Mode.MoveWindow && _captured != IntPtr.Zero)
            RunAction(follow ? Act.MoveHere : Act.MoveHereAndStay, target);
        else
            RunAction(Act.SwitchTo, target);
    }

    private void RunAction(Act act, Guid desktopId)
    {
        switch (act)
        {
            case Act.Rename:
                ShowRenameStage(desktopId);
                return;

            case Act.Delete:
                ShowConfirmDeleteStage(desktopId);
                return;

            case Act.MoveEarlier:
                Reorder(desktopId, -1);
                return;

            case Act.MoveLater:
                Reorder(desktopId, +1);
                return;
        }

        // What remains moves the window or changes desktop, both of which finish the
        // interaction. Hide and act while this window still owns the foreground: switching
        // needs that right to stop the previously focused window pulling us back.
        _closing = true;
        Hide();

        try
        {
            switch (act)
            {
                case Act.MoveHere:
                case Act.MoveHereAndStay:
                    MoveCaptured(desktopId, follow: act == Act.MoveHere);
                    break;

                case Act.SwitchTo:
                    if (!_desktops.SwitchTo(desktopId, out var switchError))
                        _reportError(switchError ?? "Could not switch desktops.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"unexpected failure running {act}", ex);
            _reportError($"Something went wrong: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private void MoveCaptured(Guid target, bool follow)
    {
        // Already there: moving would be a no-op, so just honour the follow intent
        // instead of reporting a success that changed nothing.
        if (_capturedWindowDesktop == target)
        {
            if (follow && _currentDesktop != target
                && !_desktops.SwitchTo(target, out var goError))
                _reportError(goError ?? "Could not switch desktops.");

            return;
        }

        if (_desktops.MoveWindow(_captured, target, follow, out var moveError))
            _state.RecordDestination(target, _config.RecentDestinationCount);
        else
            _reportError(moveError ?? "Could not move the window.");
    }

    private void CreateAndUse(string name, bool invertFollow)
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

            if (_mode == Mode.MoveWindow && _captured != IntPtr.Zero)
            {
                // A brand new desktop is empty, so following it is nearly always right.
                // Ctrl still overrides for "park this somewhere and carry on".
                var follow = !invertFollow;

                if (_desktops.MoveWindow(_captured, created, follow, out var moveError))
                    _state.RecordDestination(created, _config.RecentDestinationCount);
                else
                    _reportError(moveError ?? "Could not move the window.");
            }
            else if (!_desktops.SwitchTo(created, out var switchError))
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
    /// Reorder in place, staying open. The list rebuilds immediately with the moved
    /// desktop still selected, so repeated presses walk it along visibly.
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

        if (_stage == Stage.Actions)
            ShowActionsStage(desktopId);
        else
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
        var deleted = _subject;
        var name = NameOf(deleted);

        if (!_desktops.DeleteDesktop(deleted, out var error))
        {
            _reportError(error ?? "Could not delete the desktop.");
            ShowDesktopStage(preserveQuery: true, select: deleted);
            return;
        }

        Log.Info($"deleted \"{name}\" from the palette");

        // Drop it from recents so a deleted desktop cannot sit at the top of the list.
        _state.RecentDestinations.Remove(deleted);

        if (_state.LastCreatedDesktop == deleted)
            _state.LastCreatedDesktop = null;

        _state.Save();

        LoadDesktops();
        ShowDesktopStage(preserveQuery: false);
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
    // placement
    // -----------------------------------------------------------------------

    /// <summary>
    /// Centres the palette on the monitor holding <paramref name="reference"/>.
    ///
    /// Done in physical pixels through SetWindowPos rather than WPF's Left/Top, because on
    /// a mixed-DPI setup (laptop panel plus external monitor) WPF's device-independent
    /// units are relative to a different monitor's scaling and the window lands somewhere
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

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
