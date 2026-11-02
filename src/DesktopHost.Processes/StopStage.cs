namespace DesktopHost.Processes;

/// <summary>The step that ended a supervised child when <see cref="ProcessSupervisor.StopAsync"/> stopped it.</summary>
public enum StopStage
{
    /// <summary>There was no running child to stop.</summary>
    NotRunning,

    /// <summary>The child had already exited when the stop began (nothing was needed).</summary>
    AlreadyExited,

    /// <summary>The child ended in answer to <see cref="StopPolicy.GracefulStop"/> within <see cref="StopPolicy.GracefulTimeout"/>.</summary>
    Graceful,

    /// <summary>The child ended in answer to <see cref="StopPolicy.CloseMainWindow"/> within its timeout.</summary>
    CloseWindow,

    /// <summary>The child was ended by force — the guardian's tree kill, or the tree kill the guardian left to the supervisor.</summary>
    Killed,
}
