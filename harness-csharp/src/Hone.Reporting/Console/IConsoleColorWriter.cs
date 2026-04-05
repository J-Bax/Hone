namespace Hone.Reporting.Console;

/// <summary>
/// Abstraction for writing colored text to the console.
/// Enables testing without coupling to <see cref="System.Console"/>.
/// </summary>
public interface IConsoleColorWriter
{
    /// <summary>
    /// Writes text without a trailing newline.
    /// </summary>
    public void Write(string text, ConsoleColor? color = null);

    /// <summary>
    /// Writes text followed by a newline.
    /// </summary>
    public void WriteLine(string text = "", ConsoleColor? color = null);
}
