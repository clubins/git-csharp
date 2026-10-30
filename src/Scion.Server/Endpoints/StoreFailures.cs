using Scion.Core.Storage;
using Scion.Server.Errors;

namespace Scion.Server.Endpoints;

/// <summary>
/// Boundary translation for store failures on the write path: a store refusing a write (hash
/// mismatch, oversize content, malformed hash) is the client's data problem, so these failures
/// surface as 400 validation errors with the store's own message. Anything else — a genuinely
/// unexpected exception — falls through to the problem-details writer's detail-free 500 rather
/// than being mistaken for client error.
/// </summary>
internal static class StoreFailures
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or BlobStoreException)
        {
            throw new ScionValidationException(failure.Message);
        }
    }

    /// <summary>Overload for store calls that return nothing; same translation.</summary>
    public static async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or BlobStoreException)
        {
            throw new ScionValidationException(failure.Message);
        }
    }
}
