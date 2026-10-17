using System.ComponentModel;
using System.Text.Json;

namespace New_Macro_Recorder_Project;

public partial class Form1 : Form
{
    private readonly BindingList<MacroAction> _actions = [];
    private readonly GlobalInputHook _hook = new();
    private readonly MacroRunner _runner;
    private CancellationTokenSource? _playCts;
    private bool _recording, _paused;
    private DateTime _lastEvent;
    private int _recordInsertIndex = -1, _playStartIndex;
    private int _dragIndex = -1;
    private string? _currentFile;
    private readonly DataGridView _grid = new();
    private readonly ToolStripStatusLabel _status = new("Ready");
    private readonly NumericUpDown _speed = new() { Minimum = 5, Maximum = 400, Value = 100, Width = 68 };
    private readonly NumericUpDown _repeatCount = new() { Minimum = 0, Maximum = 9999, Value = 1, Width = 68 };

    public Form1()
    {
        InitializeComponent();
        _runner = new MacroRunner(this);
        Text = "Macro Studio · Automation Workbench"; Width = 1120; Height = 720; MinimumSize = new Size(860, 520); StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        _hook.Input += CaptureInput;
        _runner.PositionChanged += i => BeginInvoke(() => { i += _playStartIndex; if (i >= 0 && i < _grid.Rows.Count) { _grid.ClearSelection(); _grid.Rows[i].Selected = true; _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, i); } });
        _runner.StatusChanged += s => BeginInvoke(() => _status.Text = s);
        FormClosing += (_, _) => { _hook.Dispose(); _playCts?.Cancel(); };
        RefreshRows();
    }

    private void BuildUi()
    {
        BackColor = Color.FromArgb(18, 22, 29); ForeColor = Color.FromArgb(231, 236, 243); Font = new Font("Segoe UI", 9F);
        var menu = new MenuStrip { BackColor = Color.FromArgb(25, 30, 39), ForeColor = ForeColor };
        var file = new ToolStripMenuItem("File"); file.DropDownItems.Add("New", null, (_, _) => NewMacro()); file.DropDownItems.Add("Open…", null, (_, _) => OpenMacro()); file.DropDownItems.Add("Save", null, (_, _) => SaveMacro(false)); file.DropDownItems.Add("Save as…", null, (_, _) => SaveMacro(true));
        var insert = new ToolStripMenuItem("Insert"); insert.DropDownItems.Add("Custom action…", null, (_, _) => AddCustom()); insert.DropDownItems.Add("IF condition…", null, (_, _) => AddCondition()); insert.DropDownItems.Add("ELSE", null, (_, _) => Add(ActionKind.Else)); insert.DropDownItems.Add("END IF", null, (_, _) => Add(ActionKind.EndIf)); insert.DropDownItems.Add("REPEAT", null, (_, _) => AddRepeat()); insert.DropDownItems.Add("END REPEAT", null, (_, _) => Add(ActionKind.EndRepeat)); insert.DropDownItems.Add("Set variable…", null, (_, _) => AddVariable()); insert.DropDownItems.Add("Label…", null, (_, _) => AddLabel()); insert.DropDownItems.Add("Goto…", null, (_, _) => AddGoto());
        var commands = new ToolStripMenuItem("Commands");
        AddCommandGroup(commands, "Mouse", ActionKind.MouseMove, ActionKind.MouseDown, ActionKind.MouseUp, ActionKind.MouseClick, ActionKind.MouseWheel);
        AddCommandGroup(commands, "Keyboard and text", ActionKind.KeyDown, ActionKind.KeyUp, ActionKind.KeyPress, ActionKind.Hotkey, ActionKind.Text, ActionKind.TypeDate, ActionKind.TypeEnvironmentVariable);
        AddCommandGroup(commands, "Timing and flow", ActionKind.Delay, ActionKind.RandomDelay, ActionKind.WaitForUserInput, ActionKind.WaitWhileHourglass, ActionKind.If, ActionKind.Else, ActionKind.EndIf, ActionKind.Repeat, ActionKind.RepeatUntil, ActionKind.While, ActionKind.For, ActionKind.EndRepeat, ActionKind.EndWhile, ActionKind.EndFor, ActionKind.ExitLoop, ActionKind.Break, ActionKind.Pause, ActionKind.Breakpoint, ActionKind.SetVariable, ActionKind.Label, ActionKind.GoTo);
        AddCommandGroup(commands, "Files and processes", ActionKind.RunProgram, ActionKind.CopyFile, ActionKind.DeleteFile, ActionKind.IfFileExists, ActionKind.IfProcessExists, ActionKind.WaitForProcess, ActionKind.ProcessExists, ActionKind.PlayMacro);
        AddCommandGroup(commands, "Windows", ActionKind.WaitForWindow, ActionKind.WindowExists, ActionKind.WindowActive, ActionKind.IfWindowExists, ActionKind.IfWindowFocused, ActionKind.CloseWindow, ActionKind.SwitchWindow, ActionKind.MoveResizeWindow);
        AddCommandGroup(commands, "Clipboard", ActionKind.SetClipboard, ActionKind.ClearClipboard, ActionKind.IncrementClipboard, ActionKind.PasteClipboard, ActionKind.IfClipboardEquals, ActionKind.IfMessage);
        AddCommandGroup(commands, "Screen matching", ActionKind.PixelColor, ActionKind.FindPixel, ActionKind.ImageFound, ActionKind.FindImage);
        AddCommandGroup(commands, "Other", ActionKind.MessageBox, ActionKind.OpenWebsite, ActionKind.Shutdown, ActionKind.Comment);
        var playback = new ToolStripMenuItem("Playback"); playback.DropDownItems.Add("Play all", null, async (_, _) => await StartPlayback()); playback.DropDownItems.Add("Play selected range", null, async (_, _) => await PlaySelected()); playback.DropDownItems.Add("Play from cursor", null, async (_, _) => await PlayFromCursor()); playback.DropDownItems.Add("Play to cursor", null, async (_, _) => await PlayToCursor()); playback.DropDownItems.Add("Play X times…", null, async (_, _) => await PlayTimes());
        var recording = new ToolStripMenuItem("Recording"); recording.DropDownItems.Add("Record and append", null, (_, _) => StartRecording()); recording.DropDownItems.Add("Record at cursor", null, (_, _) => StartRecording(true));
        menu.Items.Add(file); menu.Items.Add(insert); menu.Items.Add(commands); menu.Items.Add(playback); menu.Items.Add(recording); MainMenuStrip = menu;
        var top = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.FromArgb(25, 30, 39), ForeColor = ForeColor, Padding = new Padding(8, 5, 8, 5), ImageScalingSize = new Size(18, 18) };
        top.Items.Add(Button("●  Record", (_, _) => StartRecording(), Color.FromArgb(193, 63, 69)));
        top.Items.Add(Button("■  Stop", (_, _) => StopAll(), Color.FromArgb(64, 73, 86)));
        top.Items.Add(Button("▶  Play", async (_, _) => await StartPlayback(), Color.FromArgb(37, 127, 103)));
        top.Items.Add(Button("Ⅱ  Pause", (_, _) => TogglePause(), Color.FromArgb(64, 73, 86)));
        top.Items.Add(new ToolStripSeparator()); top.Items.Add(Button("＋  Action", (_, _) => AddCustom(), Color.FromArgb(54, 82, 110)));
        top.Items.Add(new ToolStripLabel("Speed")); var speedHost = new ToolStripControlHost(_speed) { AutoSize = false, Width = 74 }; top.Items.Add(speedHost); top.Items.Add(new ToolStripLabel("%"));
        top.Items.Add(new ToolStripSeparator()); top.Items.Add(new ToolStripLabel("Play count")); top.Items.Add(new ToolStripControlHost(_repeatCount) { AutoSize = false, Width = 74 });
        var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(18, 22, 29) };
        var title = new Label { Text = "MACRO STUDIO", Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Location = new Point(14, 8) };
        var subtitle = new Label { Text = "Record input · compose actions · run conditional automations", Font = new Font("Segoe UI", 9), ForeColor = Color.FromArgb(145, 157, 174), AutoSize = true, Location = new Point(16, 39) };
        header.Controls.Add(title); header.Controls.Add(subtitle);
        _grid.Dock = DockStyle.Fill; _grid.BackgroundColor = Color.FromArgb(22, 27, 35); _grid.BorderStyle = BorderStyle.None; _grid.GridColor = Color.FromArgb(45, 53, 65); _grid.EnableHeadersVisualStyles = false; _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(31, 38, 48); _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(178, 190, 207); _grid.DefaultCellStyle.BackColor = Color.FromArgb(22, 27, 35); _grid.DefaultCellStyle.ForeColor = Color.FromArgb(220, 227, 237); _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(47, 70, 91); _grid.DefaultCellStyle.SelectionForeColor = Color.White; _grid.RowHeadersVisible = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.MultiSelect = true; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", FillWeight = 6, ReadOnly = true }); _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Action", FillWeight = 39, ReadOnly = true }); _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Delay", FillWeight = 11, ReadOnly = true }); _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Parameters", FillWeight = 44, ReadOnly = true });
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) AddCustom(edit: _actions[e.RowIndex], index: e.RowIndex); };
        _grid.AllowDrop = true; _grid.MouseDown += (_, e) => { var hit = _grid.HitTest(e.X, e.Y); _dragIndex = hit.RowIndex; };
        _grid.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left && _dragIndex >= 0) _grid.DoDragDrop(_dragIndex, DragDropEffects.Move); };
        _grid.DragOver += (_, e) => e.Effect = e.Data?.GetDataPresent(typeof(int)) == true ? DragDropEffects.Move : DragDropEffects.None;
        _grid.DragDrop += (_, e) => { var client = _grid.PointToClient(new Point(e.X, e.Y)); var target = _grid.HitTest(client.X, client.Y).RowIndex; if (target >= 0 && e.Data?.GetData(typeof(int)) is int source) MoveAction(source, target); _dragIndex = -1; };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; } else if (e.Control && e.KeyCode == Keys.C) CopySelected(); else if (e.Control && e.KeyCode == Keys.V) PasteCopied(); else if (e.Control && e.KeyCode == Keys.Up) MoveSelected(-1); else if (e.Control && e.KeyCode == Keys.Down) MoveSelected(1); };
        var rowMenu = new ContextMenuStrip(); rowMenu.Items.Add("Edit…", null, (_, _) => { if (_grid.CurrentRow is { Index: >= 0 } r) AddCustom(edit: _actions[r.Index], index: r.Index); }); rowMenu.Items.Add("Duplicate", null, (_, _) => DuplicateSelected()); rowMenu.Items.Add("Move up", null, (_, _) => MoveSelected(-1)); rowMenu.Items.Add("Move down", null, (_, _) => MoveSelected(1)); rowMenu.Items.Add(new ToolStripSeparator()); rowMenu.Items.Add("Delete", null, (_, _) => DeleteSelected()); _grid.ContextMenuStrip = rowMenu;
        var side = new Panel { Dock = DockStyle.Right, Width = 230, BackColor = Color.FromArgb(25, 30, 39), Padding = new Padding(14) };
        var sideTitle = new Label { Text = "WORKFLOW", ForeColor = Color.FromArgb(138, 156, 178), Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold), Dock = DockStyle.Top, Height = 28 };
        side.Controls.Add(sideTitle);
        var sideButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 250, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 5, 0, 0) };
        foreach (var (text, fn) in new (string, Action)[] { ("＋ Custom action", () => AddCustom()), ("◇ IF condition", AddCondition), ("ELSE", () => Add(ActionKind.Else)), ("END IF", () => Add(ActionKind.EndIf)), ("↻ Repeat block", AddRepeat), ("END REPEAT", () => Add(ActionKind.EndRepeat)), ("SET variable", AddVariable), ("LABEL", AddLabel), ("GOTO label", AddGoto), ("Delete selected", DeleteSelected), ("Edit selected", EditSelected), ("Duplicate selected", DuplicateSelected), ("Move selected up", () => MoveSelected(-1)), ("Move selected down", () => MoveSelected(1)), ("Play selected", async () => await PlaySelected()) }) { var b = new Button { Text = text, Width = 195, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(34, 42, 54), ForeColor = Color.FromArgb(222, 229, 238), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), Margin = new Padding(0, 0, 0, 7) }; b.FlatAppearance.BorderColor = Color.FromArgb(49, 59, 73); b.Click += (_, _) => fn(); sideButtons.Controls.Add(b); }
        side.Controls.Add(sideButtons);
        var hint = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(137, 150, 169), Text = "Conditions\n\nNumber:  score >= 10\nVariable: ${score} == 10\nPixel: pixel:X,Y == #RRGGBB\nImage: image:path == true\nWindow: window:title == true\nActive: active:title == true\nProcess: process:name == true\n\nPair IF with END IF. Use REPEAT / END REPEAT for bounded loops.", Padding = new Padding(0, 12, 0, 0), Font = new Font("Segoe UI", 8.5f) }; side.Controls.Add(hint);
        var status = new StatusStrip { BackColor = Color.FromArgb(25, 30, 39), ForeColor = Color.FromArgb(175, 189, 207), SizingGrip = false }; status.Items.Add(_status); var count = new ToolStripStatusLabel("0 actions") { Spring = true, TextAlign = ContentAlignment.MiddleRight }; status.Items.Add(count); _actions.ListChanged += (_, _) => count.Text = $"{_actions.Count} actions";
        Controls.Add(_grid); Controls.Add(side); Controls.Add(status); Controls.Add(top); Controls.Add(header); Controls.Add(menu); menu.Dock = DockStyle.Top; header.Dock = DockStyle.Top; top.Dock = DockStyle.Top;
    }

    private ToolStripButton Button(string text, EventHandler click, Color color) { var b = new ToolStripButton(text) { ForeColor = Color.White, BackColor = color, DisplayStyle = ToolStripItemDisplayStyle.Text, Margin = new Padding(3, 1, 5, 1), Padding = new Padding(8, 4, 8, 4) }; b.Click += click; return b; }
    private void AddCommandGroup(ToolStripMenuItem root, string group, params ActionKind[] kinds)
    {
        var parent = new ToolStripMenuItem(group);
        foreach (var kind in kinds) { var item = new ToolStripMenuItem(kind.ToString()); item.Click += (_, _) => { if (kind == ActionKind.Repeat) AddRepeat(); else if (kind == ActionKind.If) AddCondition(); else AddCustom(kind); }; parent.DropDownItems.Add(item); }
        root.DropDownItems.Add(parent);
    }
    private void RefreshRows() { int current = _grid.CurrentRow?.Index ?? -1; _grid.Rows.Clear(); for (int i = 0; i < _actions.Count; i++) { var a = _actions[i]; _grid.Rows.Add((i + 1).ToString("000"), a.ToString(), a.DelayMs > 0 ? $"{a.DelayMs} ms" : "—", a.Kind is ActionKind.MouseMove or ActionKind.MouseDown or ActionKind.MouseUp or ActionKind.MouseClick ? $"X {a.X} · Y {a.Y}" : a.Value2); } if (current >= 0 && current < _grid.Rows.Count) _grid.CurrentCell = _grid.Rows[current].Cells[0]; }
    private void Add(ActionKind kind) { Insert(new MacroAction { Kind = kind }); }
    private void Insert(MacroAction a) { int at = _grid.CurrentRow?.Index ?? _actions.Count - 1; _actions.Insert(Math.Clamp(at + 1, 0, _actions.Count), a); RefreshRows(); }
    private void CaptureInput(MacroAction a)
    {
        if (!_recording || _paused) return;
        if (a.Kind == ActionKind.MouseMove && DateTime.UtcNow - _lastEvent < TimeSpan.FromMilliseconds(24)) return;
        var now = DateTime.UtcNow; a.DelayMs = _lastEvent == default ? 0 : (int)Math.Clamp((now - _lastEvent).TotalMilliseconds, 0, 3_600_000); _lastEvent = now;
        BeginInvoke(() => { if (_recordInsertIndex >= 0) { _actions.Insert(Math.Min(_recordInsertIndex++, _actions.Count), a); } else _actions.Add(a); RefreshRows(); _status.Text = $"Recording · {_actions.Count} actions"; });
    }
    private void StartRecording(bool insertAtCursor = false) { if (_recording) return; _playCts?.Cancel(); _recording = true; _recordInsertIndex = insertAtCursor ? Math.Max(0, _grid.CurrentRow?.Index ?? _actions.Count) : -1; _lastEvent = default; try { _hook.Start(); _status.Text = insertAtCursor ? "Recording at cursor · press Stop to finish" : "Recording global input · press Stop to finish"; } catch (Exception ex) { _recording = false; MessageBox.Show(this, ex.Message, "Hook error", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    private void StopAll() { _recording = false; _hook.Stop(); _playCts?.Cancel(); _paused = false; _status.Text = "Stopped"; }
    private void TogglePause() { if (!_recording && _playCts == null) return; _paused = !_paused; _status.Text = _paused ? "Paused" : _recording ? "Recording" : "Playing"; }
    private async Task StartPlayback(int start = 0, int? end = null, int? playCount = null) { if (_actions.Count == 0) return; StopAll(); start=Math.Clamp(start,0,_actions.Count); int last=Math.Clamp(end??_actions.Count-1,start,Math.Max(start,_actions.Count-1));_playStartIndex=start;_playCts = new CancellationTokenSource(); var token = _playCts.Token; var actions = _actions.Skip(start).Take(last-start+1).ToList(); var speed = (double)_speed.Value / 100; var count = playCount??(int)_repeatCount.Value; try { await Task.Run(() => _runner.RunAsync(actions, speed, count, token, () => _paused, () => _paused = true), token); } catch (OperationCanceledException) { _status.Text = "Playback stopped"; } catch (Exception ex) { _status.Text = "Playback error"; MessageBox.Show(this, ex.Message, "Playback error", MessageBoxButtons.OK, MessageBoxIcon.Error); } finally { _playCts = null; _paused = false; } }
    private async Task PlaySelected() { if(_grid.SelectedRows.Count==0)return;var rows=_grid.SelectedRows.Cast<DataGridViewRow>().Select(r=>r.Index).OrderBy(i=>i).ToArray();await StartPlayback(rows[0],rows[^1]); }
    private async Task PlayFromCursor() { await StartPlayback(Math.Max(0,_grid.CurrentRow?.Index??0)); }
    private async Task PlayToCursor() { await StartPlayback(0,Math.Max(0,_grid.CurrentRow?.Index??_actions.Count-1)); }
    private async Task PlayTimes() { var value=Prompt("Play X times","Number of playbacks:",_repeatCount.Value.ToString());if(int.TryParse(value,out var count))await StartPlayback(playCount:Math.Clamp(count,1,9999)); }
    private void AddCustom(ActionKind? initialKind = null, MacroAction? edit = null, int index = -1)
    {
        using var f = new Form { Text = edit is null ? "Add action" : "Edit action", Width = 470, Height = 495, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, BackColor = BackColor, ForeColor = ForeColor };
        var names = Enum.GetNames<ActionKind>().Where(x => x is not "Else" and not "EndIf").ToArray();
        var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 126, Top = 18, Width = 270 }; kind.Items.AddRange(names.Cast<object>().ToArray());
        kind.SelectedItem = edit?.Kind.ToString() ?? initialKind?.ToString() ?? ActionKind.Delay.ToString();
        var value = Field(f, "Value / title / key", 58, edit?.Value ?? ""); var value2 = Field(f, "Value 2 / timeout", 98, edit?.Value2 ?? ""); var x = Field(f, "X", 138, edit?.X.ToString() ?? "0"); var y = Field(f, "Y", 178, edit?.Y.ToString() ?? "0"); var delay = Field(f, "Delay (ms)", 218, edit?.DelayMs.ToString() ?? "0"); var count = Field(f, "Count / timeout ms", 258, edit?.Count.ToString() ?? "1");
        f.Controls.Add(new Label { Text = "Compare", Left = 20, Top = 302, Width = 98 }); var compare = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 126, Top = 298, Width = 270 }; compare.Items.AddRange(new object[] { "==", "!=", ">", "<", ">=", "<=" }); compare.SelectedItem = edit?.Compare ?? "=="; if (compare.SelectedIndex < 0) compare.SelectedIndex = 0; f.Controls.Add(compare);
        var note = Field(f, "Error policy / note", 338, edit?.Note ?? "");
        f.Controls.Add(new Label { Text = "Error: leave empty, SKIP, STOP, or ONERROR:label", Left = 126, Top = 368, Width = 330, ForeColor = Color.Silver });
        f.Controls.Add(new Label { Text = "Action type", Left = 20, Top = 22, Width = 96 }); f.Controls.Add(kind);
        var ok = new Button { Text = edit is null ? "Insert" : "Save", DialogResult = DialogResult.OK, Left = 300, Top = 412, Width = 96 }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 198, Top = 412, Width = 90 }; f.Controls.Add(ok); f.Controls.Add(cancel); f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        Enum.TryParse<ActionKind>((string)kind.SelectedItem!, out var k); var result = new MacroAction { Kind=k, Value=value.Text, Value2=value2.Text, X=Int(x.Text), Y=Int(y.Text), DelayMs=Int(delay.Text), Count=Math.Max(0,Int(count.Text)), Compare=(string)compare.SelectedItem!, Note=note.Text };
        if (edit is not null && index >= 0 && index < _actions.Count) { _actions[index] = result; RefreshRows(); _grid.Rows[index].Selected = true; } else Insert(result);
    }
    private TextBox Field(Form f, string label, int y, string initial = "") { f.Controls.Add(new Label { Text=label, Left=20, Top=y+4, Width=98 }); var t=new TextBox { Text=initial, Left=126, Top=y, Width=270 }; f.Controls.Add(t); return t; }
    private void AddCondition()
    {
        using var f = new Form { Text = "IF condition", Width = 480, Height = 265, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = BackColor, ForeColor = ForeColor };
        var left=Field(f,"Left / condition",18); var op=new ComboBox { Left=126,Top=58,Width=90,DropDownStyle=ComboBoxStyle.DropDownList }; op.Items.AddRange(new object[]{"==","!=",">","<",">=","<="});op.SelectedIndex=0; f.Controls.Add(new Label{Text="Operator",Left=20,Top=62,Width=98});f.Controls.Add(op);var right=Field(f,"Compare to",98);
        var tip=new Label{Text="Examples: pixel:120,200  |  image:C:\\ok.png  |  window:Notepad  |  ${count}",Left=20,Top=137,Width=420,Height=35,ForeColor=Color.Silver};f.Controls.Add(tip);
        var ok=new Button{Text="Insert IF",DialogResult=DialogResult.OK,Left=300,Top=184,Width=95}; var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,Left=198,Top=184,Width=90};f.Controls.Add(ok);f.Controls.Add(cancel);f.AcceptButton=ok;f.CancelButton=cancel;
        if(f.ShowDialog(this)==DialogResult.OK)Insert(new MacroAction{Kind=ActionKind.If,Value=left.Text,Compare=(string)op.SelectedItem!,Value2=right.Text});
    }
    private void AddRepeat() { var s=Prompt("Repeat", "Number of repetitions (0 = forever, Stop cancels):", "3"); if(int.TryParse(s,out var n))Insert(new MacroAction{Kind=ActionKind.Repeat,Count=Math.Clamp(n,0,100000)}); }
    private void AddVariable() { var k=Prompt("Set variable", "Variable name:", "counter"); if(k==null)return; var v=Prompt("Set variable", "Value:", "0"); if(v!=null)Insert(new MacroAction{Kind=ActionKind.SetVariable,Value=k,Value2=v}); }
    private void AddLabel() { var s=Prompt("Label", "Label name:", "start"); if(s!=null)Insert(new MacroAction{Kind=ActionKind.Label,Value=s}); }
    private void AddGoto() { var s=Prompt("Goto", "Label name:", "start"); if(s!=null)Insert(new MacroAction{Kind=ActionKind.GoTo,Value=s}); }
    private void EditSelected() { if(_grid.CurrentRow is { Index: >=0 } r) AddCustom(edit:_actions[r.Index],index:r.Index); }
    private string? Prompt(string title,string label,string initial) { using var f=new Form{Text=title,Width=390,Height=160,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,BackColor=BackColor,ForeColor=ForeColor};f.Controls.Add(new Label{Text=label,Left=16,Top=16,Width=340});var t=new TextBox{Text=initial,Left=16,Top=42,Width=340};f.Controls.Add(t);var ok=new Button{Text="OK",DialogResult=DialogResult.OK,Left=266,Top=80,Width=90};var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,Left=166,Top=80,Width=90};f.Controls.Add(ok);f.Controls.Add(cancel);f.AcceptButton=ok;f.CancelButton=cancel;return f.ShowDialog(this)==DialogResult.OK?t.Text:null; }
    private static int Int(string s)=>int.TryParse(s,out var n)?n:0;
    private List<MacroAction> _copiedActions = [];
    private void CopySelected() { _copiedActions = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _actions.Count).OrderBy(i => i).Select(i => Clone(_actions[i])).ToList(); }
    private void PasteCopied() { if (_copiedActions.Count == 0) return; int at = Math.Clamp((_grid.CurrentRow?.Index ?? _actions.Count - 1) + 1, 0, _actions.Count); foreach (var a in _copiedActions) _actions.Insert(at++, Clone(a)); RefreshRows(); }
    private void DuplicateSelected() { CopySelected(); PasteCopied(); }
    private static MacroAction Clone(MacroAction a) => new() { Kind=a.Kind, DelayMs=a.DelayMs, X=a.X, Y=a.Y, Value=a.Value, Value2=a.Value2, Compare=a.Compare, Count=a.Count, Note=a.Note };
    private void MoveSelected(int delta) { var ids = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r=>r.Index).OrderBy(i=>i).ToArray(); if(ids.Length==0)return; if(delta<0)foreach(var i in ids)MoveAction(i,i-1);else foreach(var i in ids.Reverse())MoveAction(i,i+1); }
    private void MoveAction(int source, int target) { if(source<0||source>=_actions.Count)return; target=Math.Clamp(target,0,_actions.Count-1);if(source==target)return;var a=_actions[source];_actions.RemoveAt(source);_actions.Insert(target,a);RefreshRows();if(target<_grid.Rows.Count)_grid.Rows[target].Selected=true; }
    private void DeleteSelected() { var ids=_grid.SelectedRows.Cast<DataGridViewRow>().Select(r=>r.Index).OrderByDescending(i=>i).ToArray();foreach(var i in ids)if(i>=0&&i<_actions.Count)_actions.RemoveAt(i);RefreshRows(); }
    private void NewMacro() { if(_actions.Count>0&&MessageBox.Show(this,"Discard the current macro?","New macro",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;_actions.Clear();_currentFile=null;RefreshRows();_status.Text="New macro"; }
    private void OpenMacro() { using var d=new OpenFileDialog{Filter="Macro Studio JSON (*.json)|*.json|All files (*.*)|*.*"};if(d.ShowDialog(this)!=DialogResult.OK)return;try{var m=JsonSerializer.Deserialize<MacroDocument>(File.ReadAllText(d.FileName),MacroDocument.JsonOptions)??throw new InvalidDataException("Empty macro document");_actions.Clear();foreach(var a in m.Actions)_actions.Add(a);_currentFile=d.FileName;RefreshRows();_status.Text=$"Loaded {Path.GetFileName(d.FileName)}";}catch(Exception e){MessageBox.Show(this,e.Message,"Could not open macro",MessageBoxButtons.OK,MessageBoxIcon.Error);} }
    private void SaveMacro(bool saveAs) { if(saveAs||_currentFile==null){using var d=new SaveFileDialog{Filter="Macro Studio JSON (*.json)|*.json",DefaultExt="json",FileName="macro.json"};if(d.ShowDialog(this)!=DialogResult.OK)return;_currentFile=d.FileName;}try{var m=new MacroDocument{Name=Path.GetFileNameWithoutExtension(_currentFile),Actions=_actions.ToList()};File.WriteAllText(_currentFile,JsonSerializer.Serialize(m,MacroDocument.JsonOptions));_status.Text=$"Saved {Path.GetFileName(_currentFile)}";}catch(Exception e){MessageBox.Show(this,e.Message,"Could not save macro",MessageBoxButtons.OK,MessageBoxIcon.Error);} }
}
