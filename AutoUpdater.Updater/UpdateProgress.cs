namespace AutoUpdater.Updater;

internal enum UpdateStage
{
    Preparing,
    WaitingForHost,
    ReadingManifest,
    AcquiringPackage,
    Verifying,
    Extracting,
    BackingUp,
    Installing,
    Restarting,
    Completed,
    Failed
}

internal sealed record UpdateProgress(
    UpdateStage Stage,
    int Percentage,
    string Message,
    string? Detail = null);
