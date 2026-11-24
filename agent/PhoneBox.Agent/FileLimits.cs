namespace PhoneBox.Agent;

/// <summary>The size and time limits of the file transfer between the phone and this computer.</summary>
internal static class FileLimits
{
    /// <summary>The largest single file, either way.</summary>
    public const long MaxFileBytes = 2L << 30;

    /// <summary>Free space that must be left on the disk after a received file is written.</summary>
    public const long SpaceMarginBytes = 1L << 30;

    /// <summary>All files waiting in the outbox together.</summary>
    public const long OutboxTotalBytes = 4L << 30;

    /// <summary>How long a file waits in the outbox for a phone that is not connected yet.</summary>
    public static readonly TimeSpan OutboxTtl = TimeSpan.FromHours(1);

    /// <summary>Uploads from the phone at the same time; one more is answered 429.</summary>
    public const int MaxConcurrentUploads = 2;
}
