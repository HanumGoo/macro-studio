using System.Text.Json;
using System.Text.Json.Serialization;

namespace New_Macro_Recorder_Project;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionKind { Delay, RandomDelay, MouseMove, MouseDown, MouseUp, MouseClick, MouseWheel, KeyDown, KeyUp, KeyPress, Hotkey, Text, TypeDate, TypeEnvironmentVariable, If, Else, EndIf, Repeat, RepeatUntil, While, For, EndRepeat, EndWhile, EndFor, ExitLoop, SetVariable, Label, GoTo, Break, Pause, PixelColor, ImageFound, FindPixel, FindImage, WindowExists, WindowActive, ProcessExists, WaitForWindow, WaitForProcess, WaitForUserInput, WaitWhileHourglass, IfFileExists, IfClipboardEquals, IfMessage, IfWindowExists, IfWindowFocused, IfProcessExists, RunProgram, OpenWebsite, CopyFile, DeleteFile, CloseWindow, SwitchWindow, MoveResizeWindow, MessageBox, SetClipboard, ClearClipboard, IncrementClipboard, PasteClipboard, PlayMacro, ConnectVpn, Shutdown, OnErrorGoto, Comment, Breakpoint }

public sealed class MacroAction
{
    public ActionKind Kind { get; set; }
    public int DelayMs { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string Value { get; set; } = "";
    public string Value2 { get; set; } = "";
    public string Compare { get; set; } = "==";
    public int Count { get; set; } = 1;
    public string Note { get; set; } = "";
    public override string ToString() => Kind switch
    {
        ActionKind.MouseMove => $"Mouse move  ({X}, {Y})",
        ActionKind.MouseDown or ActionKind.MouseUp => $"{Kind} {Value}  ({X}, {Y})",
        ActionKind.KeyDown or ActionKind.KeyUp => $"{Kind} {Value}",
        ActionKind.Delay => $"Delay {DelayMs} ms",
        ActionKind.RandomDelay => $"Random delay {DelayMs}–{Count} ms",
        ActionKind.Text => $"Type text  “{Value}”",
        ActionKind.If => $"IF {Value} {Compare} {Value2}",
        ActionKind.IfMessage => $"IF message “{Value}” = {Value2}",
        ActionKind.Else => "ELSE",
        ActionKind.EndIf => "END IF",
        ActionKind.Repeat => $"REPEAT {Count} times",
        ActionKind.RepeatUntil => $"REPEAT UNTIL {Value} {Compare} {Value2}",
        ActionKind.While => $"WHILE {Value} {Compare} {Value2}",
        ActionKind.For => $"FOR {Value} = {Value2}",
        ActionKind.EndRepeat => "END REPEAT",
        ActionKind.EndWhile => "END WHILE",
        ActionKind.EndFor => "END FOR",
        ActionKind.SetVariable => $"SET {Value} = {Value2}",
        ActionKind.Label => $"LABEL {Value}",
        ActionKind.GoTo => $"GOTO {Value}",
        ActionKind.Break => "BREAK",
        ActionKind.PixelColor => $"Pixel ({X}, {Y}) {Compare} {Value}",
        ActionKind.ImageFound => $"Image found  {Value}",
        ActionKind.FindImage => $"Find image  {Value}",
        ActionKind.FindPixel => $"Find pixel  {Value}",
        ActionKind.ConnectVpn => $"Connect VPN  {Value}",
        ActionKind.OnErrorGoto => $"ON ERROR GOTO {Value}",
        ActionKind.WindowExists or ActionKind.WindowActive or ActionKind.ProcessExists or ActionKind.WaitForWindow => $"{Kind}  {Value}",
        _ => Kind.ToString()
    };
}

public sealed class MacroDocument
{
    public string Name { get; set; } = "Untitled macro";
    public int Version { get; set; } = 1;
    public List<MacroAction> Actions { get; set; } = [];
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}
