using System;
using System.Runtime.CompilerServices;
using System.Text;

public static class ConsoleEx
{
	/// <summary>When set, everything written through ConsoleEx is also appended to this file (plain text, no colours).</summary>
	public static string? LogPath { get; set; }

	/// <summary>When true, every message starts with the time (HH:mm:ss.fff), on the console and in the log file.</summary>
	public static bool TimeStamp { get; set; }

	private static readonly object _lock = new();

	public static void Write(ConsoleInterpolatedStringHandler handler) => Output(handler, false);
	public static void WriteLine(ConsoleInterpolatedStringHandler handler) => Output(handler, true);

	// a plain string (no colours, no interpolation holes); interpolated strings go to the handler versions above
	public static void Write(string text) => Output(new ConsoleInterpolatedStringHandler(text), false);
	public static void WriteLine(string text) => Output(new ConsoleInterpolatedStringHandler(text), true);

	private static void Output(ConsoleInterpolatedStringHandler handler, bool newLine)
	{
		lock (_lock)   // one message at a time: the colours of messages from different threads do not mix
		{
			handler.WriteToConsole();
			if (newLine) Console.WriteLine();
			if (LogPath != null) File.AppendAllText(LogPath, handler.PlainText + (newLine ? Environment.NewLine : ""));
		}
	}
}


// ============================================================
// Markers
// ============================================================

public readonly record struct ForegroundColor(ConsoleColor Color);
public readonly record struct BackgroundColor(ConsoleColor Color);

public readonly record struct RestoreForeground;
public readonly record struct RestoreBackground;


// ============================================================
// Foreground
// ============================================================

public static class Fg
{
	public static ForegroundColor For(ConsoleColor color) => new(color);

	public static ForegroundColor Black       => new(ConsoleColor.Black);
	public static ForegroundColor DarkBlue    => new(ConsoleColor.DarkBlue);
	public static ForegroundColor DarkGreen   => new(ConsoleColor.DarkGreen);
	public static ForegroundColor DarkCyan    => new(ConsoleColor.DarkCyan);
	public static ForegroundColor DarkRed     => new(ConsoleColor.DarkRed);
	public static ForegroundColor DarkMagenta => new(ConsoleColor.DarkMagenta);
	public static ForegroundColor DarkYellow  => new(ConsoleColor.DarkYellow);
	public static ForegroundColor Gray        => new(ConsoleColor.Gray);
	public static ForegroundColor DarkGray    => new(ConsoleColor.DarkGray);
	public static ForegroundColor Blue        => new(ConsoleColor.Blue);
	public static ForegroundColor Green       => new(ConsoleColor.Green);
	public static ForegroundColor Cyan        => new(ConsoleColor.Cyan);
	public static ForegroundColor Red         => new(ConsoleColor.Red);
	public static ForegroundColor Magenta     => new(ConsoleColor.Magenta);
	public static ForegroundColor Yellow      => new(ConsoleColor.Yellow);
	public static ForegroundColor White       => new(ConsoleColor.White);

	public static RestoreForeground Restore => new();
}


// ============================================================
// Background
// ============================================================

public static class Bg
{
	public static BackgroundColor Black       => new(ConsoleColor.Black);
	public static BackgroundColor DarkBlue    => new(ConsoleColor.DarkBlue);
	public static BackgroundColor DarkGreen   => new(ConsoleColor.DarkGreen);
	public static BackgroundColor DarkCyan    => new(ConsoleColor.DarkCyan);
	public static BackgroundColor DarkRed     => new(ConsoleColor.DarkRed);
	public static BackgroundColor DarkMagenta => new(ConsoleColor.DarkMagenta);
	public static BackgroundColor DarkYellow  => new(ConsoleColor.DarkYellow);
	public static BackgroundColor Gray        => new(ConsoleColor.Gray);
	public static BackgroundColor DarkGray    => new(ConsoleColor.DarkGray);
	public static BackgroundColor Blue        => new(ConsoleColor.Blue);
	public static BackgroundColor Green       => new(ConsoleColor.Green);
	public static BackgroundColor Cyan        => new(ConsoleColor.Cyan);
	public static BackgroundColor Red         => new(ConsoleColor.Red);
	public static BackgroundColor Magenta     => new(ConsoleColor.Magenta);
	public static BackgroundColor Yellow      => new(ConsoleColor.Yellow);
	public static BackgroundColor White       => new(ConsoleColor.White);

	public static RestoreBackground Restore => new();
}


// ============================================================
// Interpolated string handler
// ============================================================

[InterpolatedStringHandler]
public ref struct ConsoleInterpolatedStringHandler
{
	// The pieces are collected here, and ConsoleEx draws them together (so a message is never half-drawn when something throws or another thread writes).
	private readonly List<object> _parts;    // a string = text; ForegroundColor / BackgroundColor / RestoreForeground / RestoreBackground = a colour change
	private readonly StringBuilder _plain;   // the text only, without the colour changes: what goes to the log file

	public ConsoleInterpolatedStringHandler( int literalLength, int formattedCount) {
		_parts = new List<object>(formattedCount * 2 + 1);
		_plain = new StringBuilder(literalLength + 32);
		if (ConsoleEx.TimeStamp) AppendLiteral($"{DateTime.Now:HH:mm:ss.fff} ");
	}

	public ConsoleInterpolatedStringHandler(string text) : this(text.Length, 0) {
		AppendLiteral(text);
	}

	public readonly string PlainText => _plain.ToString();


	// --------------------------------------------------------
	// Text
	// --------------------------------------------------------

	public void AppendLiteral(string value) {
		_parts.Add(value);
		_plain.Append(value);
	}


	// --------------------------------------------------------
	// Normal interpolated values
	// --------------------------------------------------------

	public void AppendFormatted<T>(T value) {
		AppendLiteral(value?.ToString() ?? "");
	}

	public void AppendFormatted<T>(T value, string? format) where T : IFormattable {
		AppendLiteral(value.ToString(format, null));
	}

	// {value,-20} and {value,8:0.0}: pad to a width (negative = pad on the right)
	public void AppendFormatted<T>(T value, int alignment, string? format = null) {
		string text = value is IFormattable f ? f.ToString(format, null) : value?.ToString() ?? "";
		AppendLiteral(alignment >= 0 ? text.PadLeft(alignment) : text.PadRight(-alignment));
	}


	// --------------------------------------------------------
	// Foreground
	// --------------------------------------------------------

	public void AppendFormatted(ForegroundColor color) {
		_parts.Add(color);
	}

	public void AppendFormatted(RestoreForeground restore) {
		_parts.Add(restore);
	}


	// --------------------------------------------------------
	// Background
	// --------------------------------------------------------

	public void AppendFormatted(BackgroundColor color) {
		_parts.Add(color);
	}

	public void AppendFormatted(RestoreBackground restore) {
		_parts.Add(restore);
	}


	// --------------------------------------------------------
	// Draw it (called by ConsoleEx, under its lock). The colours the console had before are put back at the end.
	// --------------------------------------------------------

	internal readonly void WriteToConsole() {
		ConsoleColor originalForeground = Console.ForegroundColor;
		ConsoleColor originalBackground = Console.BackgroundColor;
		try {
			foreach (object part in _parts) {
				switch (part) {
					case string text: Console.Write(text); break;
					case ForegroundColor fg: Console.ForegroundColor = fg.Color; break;
					case BackgroundColor bg: Console.BackgroundColor = bg.Color; break;
					case RestoreForeground: Console.ForegroundColor = originalForeground; break;
					case RestoreBackground: Console.BackgroundColor = originalBackground; break;
				}
			}
		} finally {
			Console.ForegroundColor = originalForeground;
			Console.BackgroundColor = originalBackground;
		}
	}
}
