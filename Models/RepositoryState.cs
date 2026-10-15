namespace BeaconSetupManager.Models
{
    public enum RepositoryState
    {
        Clean,
        SetupInterrupted,
        LocalModifications,
        SubmodulesUninitialized,
        SubmoduleInitializationFailed,
        MergeRebaseInProgress,
        UnsafeRepository,
        NotARepository,
        Unknown
    }
}
