namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Gets the current session-owned `SV8` immutable-shelf cache footprint for focused validation and benchmark attribution.<br/>
    /// Values are runtime-only estimates and do not expose or change durable index state.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical root of the `SV8` index to inspect.<br/></param>
    /// <returns>The retained entry count, estimated retained bytes, and configured limit for that index.<br/></returns>
    internal (int EntryCount, long CachedBytes, long MaxCachedBytes) GetScalar8VarIdentityReadCacheStatsForValidation(long indexRootOffset)
    {
        return scalar8VarIdentityReadCache.GetStats(indexRootOffset);
    }

    /// <summary>
    /// Configures the optional runtime read-cache ceiling for one physical `SV8` index.<br/>
    /// The setting is session-local and is not persisted because it describes the current application's memory preference rather than index topology.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical index root router offset.<br/></param>
    /// <param name="maxCachedBytes">The positive retained-byte limit, or zero for no limit.<br/></param>
    internal void ConfigureScalar8VarIdentityReadCache(long indexRootOffset, long maxCachedBytes)
    {
        scalar8VarIdentityReadCache.Configure(indexRootOffset, maxCachedBytes);
    }

    /// <summary>
    /// Gets the current session-owned `SV16` immutable-shelf cache footprint for focused validation and benchmark attribution.<br/>
    /// Values are runtime-only estimates and do not expose or change durable index state.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical root of the `SV16` index to inspect.<br/></param>
    /// <returns>The retained entry count, estimated retained bytes, and configured limit for that index.<br/></returns>
    internal (int EntryCount, long CachedBytes, long MaxCachedBytes) GetScalar16VarIdentityReadCacheStatsForValidation(long indexRootOffset)
    {
        return scalar16VarIdentityReadCache.GetStats(indexRootOffset);
    }

    /// <summary>
    /// Configures the optional runtime read-cache ceiling for one physical `SV16` index.<br/>
    /// The setting is session-local and is not persisted because it describes the current application's memory preference rather than index topology.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical index root router offset.<br/></param>
    /// <param name="maxCachedBytes">The positive retained-byte limit, or zero for no limit.<br/></param>
    internal void ConfigureScalar16VarIdentityReadCache(long indexRootOffset, long maxCachedBytes)
    {
        scalar16VarIdentityReadCache.Configure(indexRootOffset, maxCachedBytes);
    }

    /// <summary>
    /// Gets the current session-owned `FS32-8` immutable-shelf cache footprint for focused validation and benchmark attribution.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical root of the `FS32-8` index to inspect.<br/></param>
    /// <returns>The retained entry count, estimated retained bytes, and configured limit for that index.<br/></returns>
    internal (int EntryCount, long CachedBytes, long MaxCachedBytes) GetFixed32Scalar8ReadCacheStatsForValidation(long indexRootOffset)
    {
        return fixed32Scalar8ReadCache.GetStats(indexRootOffset);
    }

    /// <summary>
    /// Configures the optional runtime read-cache ceiling for one physical `FS32-8` index.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical index root router offset.<br/></param>
    /// <param name="maxCachedBytes">The positive retained-byte limit, or zero for no limit.<br/></param>
    internal void ConfigureFixed32Scalar8ReadCache(long indexRootOffset, long maxCachedBytes)
    {
        fixed32Scalar8ReadCache.Configure(indexRootOffset, maxCachedBytes);
    }

    /// <summary>
    /// Gets the current session-owned `FS32-16` immutable-shelf cache footprint for focused validation and benchmark attribution.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical root of the `FS32-16` index to inspect.<br/></param>
    /// <returns>The retained entry count, estimated retained bytes, and configured limit for that index.<br/></returns>
    internal (int EntryCount, long CachedBytes, long MaxCachedBytes) GetFixed32Scalar16ReadCacheStatsForValidation(long indexRootOffset)
    {
        return fixed32Scalar16ReadCache.GetStats(indexRootOffset);
    }

    /// <summary>
    /// Configures the optional runtime read-cache ceiling for one physical `FS32-16` index.<br/>
    /// </summary>
    /// <param name="indexRootOffset">The physical index root router offset.<br/></param>
    /// <param name="maxCachedBytes">The positive retained-byte limit, or zero for no limit.<br/></param>
    internal void ConfigureFixed32Scalar16ReadCache(long indexRootOffset, long maxCachedBytes)
    {
        fixed32Scalar16ReadCache.Configure(indexRootOffset, maxCachedBytes);
    }
}
