namespace PhoneBox.Agent;

/// <summary>Windows build: the Terminal widget's pseudo-terminal exists only on macOS (Unix/Pty.cs), so no shell is ever started here.</summary>
public static class Pty
{
    public static IPtyProcess Start(int cols, int rows) =>
        throw new PlatformNotSupportedException("The terminal's pseudo-terminal is built for macOS only.");
}
