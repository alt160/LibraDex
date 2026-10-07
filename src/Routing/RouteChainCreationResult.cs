namespace LibraDex;

/// <summary>
/// Reports the result of creating a synthetic router chain for validation and stress harnesses.<br/>
/// This is an outer value and is not part of hot routing.<br/>
/// </summary>
/// <param name="RootOffset">The file offset of the root router.</param>
/// <param name="FinalTargetOffset">The final placeholder target reached by the chain.</param>
/// <param name="RouterCount">The number of routers created in the chain.</param>
internal readonly record struct RouteChainCreationResult(
    long RootOffset,
    long FinalTargetOffset,
    int RouterCount);
