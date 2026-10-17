using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace New_Macro_Recorder_Project;

public sealed class MacroRunner(Control ui)
{
    public event Action<int>? PositionChanged;
    public event Action<string>? StatusChanged;

    public async Task RunAsync(IReadOnlyList<MacroAction> actions, double speed, int playCount, CancellationToken token, Func<bool>? paused = null, Action? requestPause = null, int nesting = 0)
    {
        if (nesting > 8) throw new InvalidOperationException("Macro call nesting exceeds 8 levels.");
        var ifMap = new Dictionary<int, (int Else, int End)>();
        var ifStack = new Stack<(int Start, int Else)>();
        var loopMap = new Dictionary<int, int>();
        var loopStack = new Stack<int>();
        var labels = actions.Select((a, i) => (a, i)).Where(x => x.a.Kind == ActionKind.Label)
            .GroupBy(x => x.a.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < actions.Count; i++)
        {
            var k = actions[i].Kind;
            if (IsIfStart(k)) ifStack.Push((i, -1));
            else if (k == ActionKind.Else)
            {
                if (ifStack.Count == 0 || ifStack.Peek().Else >= 0) throw new InvalidDataException($"Unexpected ELSE at action {i + 1}.");
                var f = ifStack.Pop(); ifStack.Push((f.Start, i));
            }
            else if (k == ActionKind.EndIf)
            {
                if (ifStack.Count == 0) throw new InvalidDataException($"Unexpected END IF at action {i + 1}.");
                var f = ifStack.Pop(); ifMap[f.Start] = (f.Else, i); if (f.Else >= 0) ifMap[f.Else] = (f.Else, i);
            }
            else if (IsLoopStart(k)) loopStack.Push(i);
            else if (IsLoopEnd(k))
            {
                if (loopStack.Count == 0) throw new InvalidDataException($"Unexpected {k} at action {i + 1}.");
                int start = loopStack.Pop(); loopMap[start] = i; loopMap[i] = start;
            }
        }
        if (ifStack.Count > 0) throw new InvalidDataException("Every IF block must have END IF.");
        if (loopStack.Count > 0) throw new InvalidDataException("Every loop block must have a matching end statement.");
        if (actions.Any(a => a.Kind == ActionKind.GoTo && !labels.ContainsKey(a.Value))) throw new InvalidDataException("A GOTO references a label that does not exist.");

        for (int run = 0; playCount == 0 || run < playCount; run++)
        {
            token.ThrowIfCancellationRequested();
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["iteration"] = (run + 1).ToString() };
            var repeatRemaining = new Dictionary<int, int>();
            string? errorLabel = null;
            int pc = 0, steps = 0;
            while (pc < actions.Count)
            {
                token.ThrowIfCancellationRequested();
                while (paused?.Invoke() == true) await Task.Delay(100, token);
                if (++steps > 1_000_000) throw new InvalidOperationException("Safety limit reached (1,000,000 executed actions).");
                var a = actions[pc]; PositionChanged?.Invoke(pc); StatusChanged?.Invoke($"Run {run + 1}/{(playCount == 0 ? "∞" : playCount)} · action {pc + 1}/{actions.Count}");
                try
                {
                    switch (a.Kind)
                    {
                        case ActionKind.Comment: case ActionKind.Label: case ActionKind.EndIf: break;
                        case ActionKind.Delay: await Task.Delay(Scaled(a.DelayMs, speed), token); break;
                        case ActionKind.RandomDelay: await Task.Delay(Scaled(Random.Shared.Next(Math.Max(0, a.DelayMs), Math.Max(a.DelayMs + 1, a.Count + 1)), speed), token); break;
                        case ActionKind.MouseMove: InputPlayback.Move(a.X, a.Y); break;
                        case ActionKind.MouseDown: InputPlayback.Mouse(a.Value, true); break;
                        case ActionKind.MouseUp: InputPlayback.Mouse(a.Value, false); break;
                        case ActionKind.MouseClick: InputPlayback.Move(a.X, a.Y); InputPlayback.Mouse(a.Value, true); await Task.Delay(35, token); InputPlayback.Mouse(a.Value, false); break;
                        case ActionKind.MouseWheel: InputPlayback.Move(a.X, a.Y); InputPlayback.Wheel(a.Count == 0 ? 120 : a.Count); break;
                        case ActionKind.KeyDown: InputPlayback.Key(a.Value, true); break;
                        case ActionKind.KeyUp: InputPlayback.Key(a.Value, false); break;
                        case ActionKind.KeyPress: InputPlayback.Key(a.Value, true); await Task.Delay(25, token); InputPlayback.Key(a.Value, false); break;
                        case ActionKind.Hotkey: InputPlayback.Hotkey(a.Value); break;
                        case ActionKind.Text: InputPlayback.TypeText(Expand(a.Value, vars)); break;
                        case ActionKind.TypeDate: InputPlayback.TypeText(DateTime.Now.ToString(string.IsNullOrWhiteSpace(a.Value) ? "yyyy-MM-dd" : a.Value)); break;
                        case ActionKind.TypeEnvironmentVariable: InputPlayback.TypeText(Environment.GetEnvironmentVariable(a.Value) ?? ""); break;
                        case ActionKind.SetVariable: vars[a.Value] = Expand(a.Value2, vars); break;
                        case ActionKind.If: case ActionKind.IfFileExists: case ActionKind.IfClipboardEquals: case ActionKind.IfMessage: case ActionKind.IfWindowExists: case ActionKind.IfWindowFocused: case ActionKind.IfProcessExists:
                            var condition = a.Kind == ActionKind.If ? a : ConvertCondition(a);
                            if (!await Evaluate(condition, vars, run + 1, token)) { pc = ifMap.TryGetValue(pc, out var jump) ? (jump.Else >= 0 ? jump.Else + 1 : jump.End + 1) : pc + 1; continue; } break;
                        case ActionKind.Else: if (ifMap.TryGetValue(pc, out var endIf)) { pc = endIf.End + 1; continue; } break;
                        case ActionKind.Repeat:
                            if (!repeatRemaining.ContainsKey(pc))
                            {
                                int repeatCount = a.Count;
                                if (a.Value2.Equals("clipboard", StringComparison.OrdinalIgnoreCase))
                                {
                                    var clip = Ui(Clipboard.GetText);
                                    if (!int.TryParse(clip, out repeatCount)) throw new InvalidDataException("Clipboard does not contain a repeat count.");
                                }
                                else if (a.Value2.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                                {
                                    var entered = Ui(() => PromptRepeatCount(a.Count));
                                    if (!int.TryParse(entered, out repeatCount)) throw new InvalidDataException("Repeat count prompt did not contain a number.");
                                }
                                else if (a.Value2.Equals("random", StringComparison.OrdinalIgnoreCase)) repeatCount = Random.Shared.Next(1, Math.Max(2, a.Count + 1));
                                if (repeatCount < 0 || repeatCount > 100000) throw new InvalidDataException("Repeat count must be between 0 and 100000.");
                                repeatRemaining[pc] = repeatCount == 0 ? int.MaxValue : repeatCount;
                            }
                            break;
                        case ActionKind.RepeatUntil: break;
                        case ActionKind.While: if (!await Evaluate(a, vars, run + 1, token)) { pc = loopMap[pc] + 1; continue; } break;
                        case ActionKind.For:
                            var range = ParseFor(a, vars); vars[a.Value] = range.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (range.Step > 0 && range.Start > range.End || range.Step < 0 && range.Start < range.End) { pc = loopMap[pc] + 1; continue; }
                            break;
                        case ActionKind.EndRepeat:
                            int repeatStart = loopMap[pc]; var repeat = actions[repeatStart];
                            if (repeat.Kind == ActionKind.RepeatUntil)
                            {
                                if (!await Evaluate(repeat, vars, run + 1, token)) { pc = repeatStart + 1; continue; }
                            }
                            else if (repeatRemaining.TryGetValue(repeatStart, out var left) && left > 1) { repeatRemaining[repeatStart] = left - 1; pc = repeatStart + 1; continue; }
                            repeatRemaining.Remove(repeatStart); break;
                        case ActionKind.EndWhile: pc = loopMap[pc]; continue;
                        case ActionKind.EndFor:
                            int forStart = loopMap[pc]; var forAction = actions[forStart]; var forRange = ParseFor(forAction, vars);
                            double next = double.Parse(vars[forAction.Value], System.Globalization.CultureInfo.InvariantCulture) + forRange.Step;
                            vars[forAction.Value] = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (forRange.Step > 0 ? next <= forRange.End : next >= forRange.End) { pc = forStart + 1; continue; }
                            break;
                        case ActionKind.ExitLoop: case ActionKind.Break:
                            var enclosing = loopMap.Where(pair => pair.Key < pc && pair.Value > pc && pair.Value > pair.Key).OrderByDescending(pair => pair.Key).FirstOrDefault();
                            if (enclosing.Key >= 0) { pc = enclosing.Value + 1; continue; } return;
                        case ActionKind.GoTo: if (labels.TryGetValue(a.Value, out var target)) { pc = target; continue; } break;
                        case ActionKind.OnErrorGoto: errorLabel = a.Value; if (!labels.ContainsKey(errorLabel)) throw new InvalidDataException($"Error label does not exist: {errorLabel}"); break;
                        case ActionKind.Pause: case ActionKind.Breakpoint: requestPause?.Invoke(); while (paused?.Invoke() == true) await Task.Delay(100, token); break;
                        case ActionKind.PixelColor:
                            var color = ScreenColor(a.X, a.Y);
                            if (!CompareValues($"#{color.R:X2}{color.G:X2}{color.B:X2}", a.Compare, a.Value)) throw new InvalidOperationException($"Pixel color check failed at ({a.X},{a.Y}).");
                            break;
                        case ActionKind.ImageFound: if (!FindImage(a.Value, out _, out _)) throw new InvalidOperationException($"Image not found: {a.Value}"); break;
                        case ActionKind.FindImage:
                            bool imageFound = FindImage(a.Value, out int imageX, out int imageY); vars[string.IsNullOrWhiteSpace(a.Value2) ? "imageFound" : a.Value2] = imageFound.ToString(); vars["imageX"] = imageX.ToString(); vars["imageY"] = imageY.ToString(); break;
                        case ActionKind.FindPixel:
                            bool pixelFound = FindPixel(a.Value, out int pixelX, out int pixelY); string pixelVar = string.IsNullOrWhiteSpace(a.Value2) ? "pixel" : a.Value2;
                            vars[pixelVar] = pixelFound.ToString(); vars[pixelVar + "_x"] = pixelX.ToString(); vars[pixelVar + "_y"] = pixelY.ToString(); break;
                        case ActionKind.WindowExists: if (!WindowMatches(a.Value, false)) throw new InvalidOperationException($"Window not found: {a.Value}"); break;
                        case ActionKind.WindowActive: if (!WindowMatches(a.Value, true)) throw new InvalidOperationException($"Window not active: {a.Value}"); break;
                        case ActionKind.ProcessExists: if (!ProcessExists(a.Value)) throw new InvalidOperationException($"Process not found: {a.Value}"); break;
                        case ActionKind.WaitForWindow:
                            bool windowDisappear = a.Value2.Equals("disappear", StringComparison.OrdinalIgnoreCase);
                            await WaitUntil(() => WindowMatches(a.Value, false) != windowDisappear, a.Count, token, windowDisappear ? $"Window did not close: {a.Value}" : $"Window not found: {a.Value}"); break;
                        case ActionKind.WaitForProcess:
                            bool processDisappear = a.Value2.Equals("disappear", StringComparison.OrdinalIgnoreCase);
                            await WaitUntil(() => ProcessExists(a.Value) != processDisappear, a.Count, token, processDisappear ? $"Process did not exit: {a.Value}" : $"Process not found: {a.Value}"); break;
                        case ActionKind.WaitForUserInput: await WaitForUserInput(token); break;
                        case ActionKind.WaitWhileHourglass: while (CursorIsWait()) await Task.Delay(100, token); break;
                        case ActionKind.RunProgram: StartProgram(a.Value, Expand(a.Value2, vars)); break;
                        case ActionKind.OpenWebsite: Process.Start(new ProcessStartInfo(a.Value) { UseShellExecute = true }); break;
                        case ActionKind.CopyFile: File.Copy(a.Value, a.Value2, true); break;
                        case ActionKind.DeleteFile: File.Delete(a.Value); break;
                        case ActionKind.CloseWindow: SendWindowMessage(FindWindow(a.Value), 0x0010); break;
                        case ActionKind.SwitchWindow: SetForegroundWindow(FindWindow(a.Value)); break;
                        case ActionKind.MoveResizeWindow: MoveResize(a); break;
                        case ActionKind.MessageBox: Ui(() => MessageBox.Show(ui, a.Value, "Macro Studio", MessageBoxButtons.OK, MessageBoxIcon.Information)); break;
                        case ActionKind.SetClipboard: Ui(() => Clipboard.SetText(Expand(a.Value, vars))); break;
                        case ActionKind.ClearClipboard: Ui(Clipboard.Clear); break;
                        case ActionKind.IncrementClipboard:
                            var old = Ui(Clipboard.GetText); if (!double.TryParse(old, out var oldNumber)) oldNumber = 0;
                            Ui(() => Clipboard.SetText((oldNumber + (double.IsFinite(a.Count) ? Math.Max(1, a.Count) : 1)).ToString(System.Globalization.CultureInfo.InvariantCulture))); break;
                        case ActionKind.PasteClipboard: InputPlayback.TypeText(Ui(Clipboard.GetText)); break;
                        case ActionKind.PlayMacro:
                            var child = JsonSerializer.Deserialize<MacroDocument>(File.ReadAllText(a.Value), MacroDocument.JsonOptions) ?? throw new InvalidDataException("Macro file is empty.");
                            await RunAsync(child.Actions, speed, 1, token, paused, requestPause, nesting + 1); break;
                        case ActionKind.ConnectVpn: await ConnectVpn(a, token); break;
                        case ActionKind.Shutdown: await ConfirmShutdown(a.Value); break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    string? targetLabel = a.Note.StartsWith("ONERROR:", StringComparison.OrdinalIgnoreCase) ? a.Note[8..] : a.Value2.StartsWith("ONERROR:", StringComparison.OrdinalIgnoreCase) ? a.Value2[8..] : errorLabel;
                    if (!string.IsNullOrWhiteSpace(targetLabel) && labels.TryGetValue(targetLabel, out var errorTarget)) { StatusChanged?.Invoke($"Action error: {ex.Message} · jumping to {targetLabel}"); pc = errorTarget; continue; }
                    if (string.Equals(a.Note, "SKIP", StringComparison.OrdinalIgnoreCase)) { StatusChanged?.Invoke($"Skipped after error: {ex.Message}"); pc++; continue; }
                    if (string.Equals(a.Note, "STOP", StringComparison.OrdinalIgnoreCase)) { StatusChanged?.Invoke($"Stopped after error: {ex.Message}"); return; }
                    throw;
                }
                pc++;
            }
        }
        StatusChanged?.Invoke("Playback complete");
    }

    private static bool IsIfStart(ActionKind k) => k is ActionKind.If or ActionKind.IfFileExists or ActionKind.IfClipboardEquals or ActionKind.IfMessage or ActionKind.IfWindowExists or ActionKind.IfWindowFocused or ActionKind.IfProcessExists;
    private static bool IsLoopStart(ActionKind k) => k is ActionKind.Repeat or ActionKind.RepeatUntil or ActionKind.While or ActionKind.For;
    private static bool IsLoopEnd(ActionKind k) => k is ActionKind.EndRepeat or ActionKind.EndWhile or ActionKind.EndFor;
    private static int Scaled(int ms, double speed) => Math.Clamp((int)(ms / Math.Max(0.05, speed)), 0, 3_600_000);
    private static MacroAction ConvertCondition(MacroAction a) => a.Kind switch { ActionKind.IfClipboardEquals => new MacroAction { Kind = ActionKind.If, Value = "clipboard", Compare = a.Compare, Value2 = a.Value }, ActionKind.IfMessage => new MacroAction { Kind = ActionKind.If, Value = "message:" + a.Value, Compare = a.Compare, Value2 = a.Value2 }, ActionKind.IfWindowExists => new MacroAction { Kind = ActionKind.If, Value = "window:" + a.Value, Compare = "==", Value2 = "true" }, ActionKind.IfWindowFocused => new MacroAction { Kind = ActionKind.If, Value = "active:" + a.Value, Compare = "==", Value2 = "true" }, ActionKind.IfProcessExists => new MacroAction { Kind = ActionKind.If, Value = "process:" + a.Value, Compare = "==", Value2 = "true" }, _ => new MacroAction { Kind = ActionKind.If, Value = "file:" + a.Value, Compare = "==", Value2 = "true" } };
    private static (double Start, double End, double Step) ParseFor(MacroAction a, Dictionary<string, string> vars)
    {
        var parts = Expand(a.Value2, vars).Split("..", StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], out var start) || !double.TryParse(parts[1].Split(':')[0], out var end)) throw new InvalidDataException("FOR range must look like start..end, for example 1..10.");
        double step = a.Count == 0 ? 1 : a.Count; if (a.Value2.Contains(':') && double.TryParse(a.Value2[(a.Value2.IndexOf(':') + 1)..], out var explicitStep)) step = explicitStep;
        if (step == 0) throw new InvalidDataException("FOR step cannot be zero.");
        return (start, end, step);
    }
    private static string Expand(string value, Dictionary<string, string> vars) => Regex.Replace(value ?? "", @"\$\{([^}]+)\}", m => vars.GetValueOrDefault(m.Groups[1].Value, ""));

    private async Task<bool> Evaluate(MacroAction a, Dictionary<string, string> vars, int iteration, CancellationToken token)
    {
        string l = Expand(a.Value, vars), r = Expand(a.Value2, vars);
        if (l.StartsWith("pixel:", StringComparison.OrdinalIgnoreCase))
        {
            var xy = l[6..].Split(','); if (xy.Length == 2 && int.TryParse(xy[0], out int x) && int.TryParse(xy[1], out int y)) { var c = ScreenColor(x, y); l = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
        }
        if (l.StartsWith("image:", StringComparison.OrdinalIgnoreCase)) return CompareValues(FindImage(l[6..], out _, out _).ToString(), a.Compare, r);
        if (l.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return CompareValues(File.Exists(l[5..]).ToString(), a.Compare, r);
        if (l.Equals("clipboard", StringComparison.OrdinalIgnoreCase) || l.StartsWith("clipboard:", StringComparison.OrdinalIgnoreCase)) return CompareValues(Ui(Clipboard.GetText), a.Compare, r);
        if (l.StartsWith("message:", StringComparison.OrdinalIgnoreCase)) return CompareValues(Ui(() => MessageBox.Show(ui, l[8..], "Macro condition", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes ? "Yes" : "No"), a.Compare, r);
        if (l.StartsWith("window:", StringComparison.OrdinalIgnoreCase)) return CompareValues(WindowMatches(l[7..], false).ToString(), a.Compare, r);
        if (l.StartsWith("active:", StringComparison.OrdinalIgnoreCase)) return CompareValues(WindowMatches(l[7..], true).ToString(), a.Compare, r);
        if (l.StartsWith("process:", StringComparison.OrdinalIgnoreCase)) return CompareValues(ProcessExists(l[8..]).ToString(), a.Compare, r);
        if (l.StartsWith("iteration:", StringComparison.OrdinalIgnoreCase)) l = iteration.ToString();
        if (l.StartsWith("memory:", StringComparison.OrdinalIgnoreCase)) l = ReadMemory(l[7..]);
        if (l.StartsWith("windowclosed:", StringComparison.OrdinalIgnoreCase)) return CompareValues((!WindowMatches(l[13..], false)).ToString(), a.Compare, r);
        if (l.StartsWith("waitwindow:", StringComparison.OrdinalIgnoreCase)) { var deadline = DateTime.UtcNow.AddMilliseconds(int.TryParse(r, out var ms) ? Math.Clamp(ms, 1, 300000) : 30000); while (!WindowMatches(l[11..], false) && DateTime.UtcNow < deadline) await Task.Delay(100, token); return CompareValues(WindowMatches(l[11..], false).ToString(), a.Compare, "true"); }
        if (double.TryParse(l, out var x1) && double.TryParse(r, out var x2)) return CompareNumbers(x1, a.Compare, x2);
        return CompareValues(l, a.Compare, r);
    }
    private static bool CompareNumbers(double l, string op, double r) => op switch { "==" => l == r, "!=" => l != r, ">" => l > r, "<" => l < r, ">=" => l >= r, "<=" => l <= r, _ => false };
    private static bool CompareValues(string l, string op, string r)
    {
        if (double.TryParse(l, out var n1) && double.TryParse(r, out var n2)) return CompareNumbers(n1, op, n2);
        if (l.StartsWith('#') || r.StartsWith('#')) return op switch { "==" => string.Equals(l, r, StringComparison.OrdinalIgnoreCase), "!=" => !string.Equals(l, r, StringComparison.OrdinalIgnoreCase), _ => false };
        return op switch { "==" => string.Equals(l, r, StringComparison.OrdinalIgnoreCase), "!=" => !string.Equals(l, r, StringComparison.OrdinalIgnoreCase), _ => false };
    }
    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs, CancellationToken token, string message)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(timeoutMs <= 0 ? 30000 : timeoutMs, 1, 300000));
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(100, token);
        if (!predicate()) throw new TimeoutException(message);
    }
    private static async Task WaitForUserInput(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            for (int key = 1; key < 255; key++) if (GetAsyncKeyState(key) < 0) return;
            await Task.Delay(35, token);
        }
    }
    private static Color ScreenColor(int x, int y) { using var b = new Bitmap(1, 1); using (var g = Graphics.FromImage(b)) g.CopyFromScreen(x, y, 0, 0, new Size(1, 1)); return b.GetPixel(0, 0); }
    private static bool FindPixel(string hex, out int foundX, out int foundY)
    {
        foundX = foundY = -1; var target = ParseColor(hex); if (target.IsEmpty) return false;
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1024, 768); using var screen = new Bitmap(bounds.Width, bounds.Height); using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        for (int y = 0; y < screen.Height; y += 2) for (int x = 0; x < screen.Width; x += 2) { var c = screen.GetPixel(x, y); if (c.R == target.R && c.G == target.G && c.B == target.B) { foundX = x + bounds.Left; foundY = y + bounds.Top; return true; } }
        return false;
    }
    private static bool FindImage(string path, out int foundX, out int foundY)
    {
        foundX = foundY = -1; if (!File.Exists(path)) return false;
        using var template = new Bitmap(path); if (template.Width * template.Height > 250000) throw new InvalidOperationException("Image template is too large (max 250,000 pixels).");
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1024, 768); using var screen = new Bitmap(bounds.Width, bounds.Height); using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        for (int y = 0; y <= screen.Height - template.Height; y += 2) for (int x = 0; x <= screen.Width - template.Width; x += 2)
        { bool match = true; for (int ty = 0; ty < template.Height && match; ty += 2) for (int tx = 0; tx < template.Width; tx += 2) if (screen.GetPixel(x + tx, y + ty).ToArgb() != template.GetPixel(tx, ty).ToArgb()) { match = false; break; } if (match) { foundX = x + bounds.Left; foundY = y + bounds.Top; return true; } }
        return false;
    }
    private static Color ParseColor(string value) { try { return ColorTranslator.FromHtml(value); } catch { return Color.Empty; } }
    private static bool ProcessExists(string name) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)).Length > 0;
    private static string ReadMemory(string specification)
    {
        // memory:<pid>,<address>,<type> supports read-only numeric reads.
        var parts = specification.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], out int pid) || !ulong.TryParse(parts[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out ulong address)) throw new FormatException("Memory condition format is memory:PID,0xADDRESS,TYPE.");
        var type = parts[2].ToLowerInvariant(); int length = type switch { "byte" => 1, "int16" or "uint16" => 2, "int32" or "uint32" or "float" => 4, "int64" or "uint64" or "double" => 8, _ => throw new FormatException("Memory type must be byte, int16, uint16, int32, uint32, int64, uint64, float, or double.") };
        using var process = Process.GetProcessById(pid); var handle = OpenProcess(0x0010 | 0x1000, false, pid);
        if (handle == IntPtr.Zero) throw new UnauthorizedAccessException($"Cannot read process {pid}. Try using a process owned by this user at the same integrity level.");
        try
        {
            if (IntPtr.Size == 4 && address > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(address), "Address does not fit this process architecture.");
            var bytes = new byte[length]; if (!ReadProcessMemory(handle, new IntPtr(unchecked((long)address)), bytes, length, out var read) || read.ToInt64() != length) throw new InvalidOperationException($"Could not read {length} bytes at 0x{address:X}.");
            return type switch { "byte" => bytes[0].ToString(), "int16" => BitConverter.ToInt16(bytes).ToString(), "uint16" => BitConverter.ToUInt16(bytes).ToString(), "int32" => BitConverter.ToInt32(bytes).ToString(), "uint32" => BitConverter.ToUInt32(bytes).ToString(), "int64" => BitConverter.ToInt64(bytes).ToString(), "uint64" => BitConverter.ToUInt64(bytes).ToString(), "float" => BitConverter.ToSingle(bytes).ToString(System.Globalization.CultureInfo.InvariantCulture), "double" => BitConverter.ToDouble(bytes).ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "" };
        }
        finally { CloseHandle(handle); }
    }
    private static bool WindowMatches(string title, bool active) { var h = active ? GetForegroundWindow() : IntPtr.Zero; if (active) return GetWindowText(h).Contains(title, StringComparison.OrdinalIgnoreCase); return FindWindow(title) != IntPtr.Zero; }
    private static IntPtr FindWindow(string title)
    {
        IntPtr found = IntPtr.Zero; EnumWindows((h, _) => { if (IsWindowVisible(h) && GetWindowText(h).Contains(title, StringComparison.OrdinalIgnoreCase)) { found = h; return false; } return true; }, IntPtr.Zero); return found;
    }
    private static string GetWindowText(IntPtr h) { var b = new System.Text.StringBuilder(512); GetWindowTextNative(h, b, b.Capacity); return b.ToString(); }
    private static void SendWindowMessage(IntPtr h, uint msg) { if (h == IntPtr.Zero) throw new InvalidOperationException("Target window not found."); PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero); }
    private static void MoveResize(MacroAction a)
    {
        var h = FindWindow(a.Value); if (h == IntPtr.Zero) throw new InvalidOperationException("Target window not found.");
        var size = (a.Value2 ?? "").Split(','); int width = size.Length > 0 && int.TryParse(size[0], out var w) ? w : 800; int height = size.Length > 1 && int.TryParse(size[1], out var ht) ? ht : 600;
        SetWindowPos(h, IntPtr.Zero, a.X, a.Y, width, height, 0x0040);
    }
    private static void StartProgram(string path, string args) => Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true });
    private T Ui<T>(Func<T> action) => ui.InvokeRequired ? (T)ui.Invoke(action)! : action();
    private void Ui(Action action) { if (ui.InvokeRequired) ui.Invoke(action); else action(); }
    private static string PromptRepeatCount(int initial)
    {
        using var form = new Form { Text = "Repeat count", Width = 340, Height = 145, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false };
        var input = new TextBox { Left = 16, Top = 18, Width = 290, Text = initial.ToString() };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 216, Top = 58, Width = 90 };
        form.Controls.Add(input); form.Controls.Add(ok); form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK ? input.Text : "";
    }
    private async Task ConfirmShutdown(string operation)
    {
        bool accepted = Ui(() => MessageBox.Show(ui, $"The macro requested: {operation}. Continue?", "Confirm system action", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
        if (accepted) { var args = operation.ToLowerInvariant() switch { "restart" or "reboot" => "/r /t 0", "logoff" => "/l", _ => "/s /t 0" }; Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true }); await Task.Delay(250); }
    }
    private static async Task ConnectVpn(MacroAction a, CancellationToken token)
    {
        var processInfo = new ProcessStartInfo("rasdial.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        processInfo.ArgumentList.Add(a.Value);
        var credentialParts = a.Value2.Split('|', 2);
        if (credentialParts.Length == 2) { processInfo.ArgumentList.Add(credentialParts[0]); processInfo.ArgumentList.Add(credentialParts[1]); }
        using var process = Process.Start(processInfo) ?? throw new InvalidOperationException("Could not start rasdial.exe.");
        string output = await process.StandardOutput.ReadToEndAsync(token); await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException($"VPN connection failed ({process.ExitCode}): {output.Trim()}");
    }
    private static bool CursorIsWait() { var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() }; if (!GetCursorInfo(ref ci)) return false; var wait = LoadCursor(IntPtr.Zero, new IntPtr(32514)); return ci.hCursor == wait; }

    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, int size, out IntPtr bytesRead);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowTextNative(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);
}

internal static class InputPlayback
{
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    public static void Move(int x, int y) { var b = Screen.PrimaryScreen!.Bounds; Send(new INPUT { type = 0, U = new INPUTUNION { mi = new MOUSEINPUT { dx = (int)((x - b.Left) * 65535.0 / Math.Max(1, b.Width - 1)), dy = (int)((y - b.Top) * 65535.0 / Math.Max(1, b.Height - 1)), dwFlags = 0x8001 } } }); }
    public static void Mouse(string button, bool down) { uint flag = (button, down) switch { ("Right", true) => 0x0008, ("Right", false) => 0x0010, ("Middle", true) => 0x0020, ("Middle", false) => 0x0040, (_, true) => 0x0002, _ => 0x0004 }; Send(new INPUT { type = 0, U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flag } } }); }
    public static void Wheel(int delta) => Send(new INPUT { type = 0, U = new INPUTUNION { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = 0x0800 } } });
    public static void Key(string key, bool down) { if (!Enum.TryParse<Keys>(NormalizeKey(key), true, out var k)) return; Send(new INPUT { type = 1, U = new INPUTUNION { ki = new KEYBDINPUT { vk = (ushort)k, flags = down ? 0u : 2u } } }); }
    public static void Hotkey(string chord) { var keys = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries); foreach (var key in keys.Take(Math.Max(0, keys.Length - 1))) Key(key, true); if (keys.Length > 0) { Key(keys[^1], true); Key(keys[^1], false); } foreach (var key in keys.Take(Math.Max(0, keys.Length - 1)).Reverse()) Key(key, false); }
    public static void TypeText(string value) { foreach (char c in value) { Send(new INPUT { type = 1, U = new INPUTUNION { ki = new KEYBDINPUT { scan = c, flags = 4 } } }); Send(new INPUT { type = 1, U = new INPUTUNION { ki = new KEYBDINPUT { scan = c, flags = 6 } } }); } }
    private static string NormalizeKey(string key) => key switch { "Control" or "Ctrl" => "ControlKey", "Alt" => "Menu", "Shift" => "ShiftKey", "Return" => "Enter", _ => key };
    private static void Send(INPUT i) { GlobalInputHook.Suppress = true; try { SendInput(1, [i], Marshal.SizeOf<INPUT>()); } finally { Task.Delay(12).ContinueWith(_ => GlobalInputHook.Suppress = false); } }
}
