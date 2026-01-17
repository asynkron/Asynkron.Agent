namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// PatchApplier applies parsed patch operations to the filesystem.
/// </summary>
public static class PatchApplier
{
    /// <summary>
    /// ApplyFilesystemAsync applies operations to the OS filesystem.
    /// </summary>
    public static Task<List<PatchResult>> ApplyFilesystemAsync(
        CancellationToken cancellationToken,
        List<Operation> operations,
        FilesystemOptions opts)
    {
        // TODO: Implement patch application
        throw new NotImplementedException("Patch application will be implemented in the next priority");
    }
}
