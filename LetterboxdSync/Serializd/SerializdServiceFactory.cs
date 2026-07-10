using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

public static class SerializdServiceFactory
{
    /// <summary>
    /// Test-only override. When non-null, <see cref="CreateAuthenticatedAsync"/>
    /// returns this instead of constructing a real client. Mirrors
    /// <see cref="LetterboxdServiceFactory.OverrideForTesting"/>. Production never sets it.
    /// </summary>
    internal static Func<string, string, ILogger, Task<ISerializdService>>? OverrideForTesting;

    public static async Task<ISerializdService> CreateAuthenticatedAsync(string email, string password, ILogger logger)
    {
        if (OverrideForTesting != null)
            return await OverrideForTesting(email, password, logger).ConfigureAwait(false);

        var client = new SerializdApiClient(logger);
        await client.AuthenticateAsync(email, password).ConfigureAwait(false);
        return client;
    }
}
