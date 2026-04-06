namespace Hone.Reporting.Console;

/// <summary>
/// <see cref="IConsoleColorWriter"/> implementation that writes to <see cref="System.Console"/>.
/// </summary>
public sealed class SystemConsoleColorWriter : IConsoleColorWriter
{
    /// <inheritdoc />
    public void Write(string text, ConsoleColor? color = null)
    {
        if (color is { } c)
        {
            ConsoleColor previous = System.Console.ForegroundColor;
            System.Console.ForegroundColor = c;
            System.Console.Write(text);
            System.Console.ForegroundColor = previous;
        }
        else
        {
            System.Console.Write(text);
        }
    }

    /// <inheritdoc />
    public void WriteLine(string text = "", ConsoleColor? color = null)
    {
        if (color is { } c)
        {
            ConsoleColor previous = System.Console.ForegroundColor;
            System.Console.ForegroundColor = c;
            System.Console.WriteLine(text);
            System.Console.ForegroundColor = previous;
        }
        else
        {
            System.Console.WriteLine(text);
        }
    }
}
