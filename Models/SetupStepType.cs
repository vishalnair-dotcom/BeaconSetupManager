namespace BeaconSetupManager.Models
{
    public enum SetupStepType
    {
        RepositoryVerify,
        WorkspaceValidate,
        RepositoryClone,
        BranchCheckout,
        SubmodulesUpdate,
        PackagesRestore,
        SolutionBuild,
        DatabaseSetup,
        ConnectionValidation
    }
}
