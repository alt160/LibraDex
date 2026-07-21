namespace LibraDex;

/// <summary>
/// Caches one fixed index-directory slot after initialize, open, or directory update.<br/>
/// This is an outer value snapshot and is not used as a hot byte view.<br/>
/// </summary>
/// <param name="SlotIndex">The zero-based slot index in the fixed directory.</param>
/// <param name="State">The persisted slot state.</param>
/// <param name="Flags">Slot-level flags reserved for format and lifecycle behavior.</param>
/// <param name="RootRouterOffset">The direct file offset of the index root router.</param>
/// <param name="MetadataOffset">The direct file offset of optional variable-length index metadata.</param>
/// <param name="ItemCount">The directory-level cached item count for shapes that maintain one; fixed-N shelf-count shapes can leave this value non-authoritative.</param>
/// <param name="Generation">The index generation or version counter.</param>
/// <param name="KeyProfileId">The fixed key profile identifier.</param>
/// <param name="IdentityProfileId">The fixed identity profile identifier.</param>
/// <param name="RouterProfileId">The fixed router profile identifier.</param>
/// <param name="AllocationClassId">The allocation class identifier used for future reuse policy.</param>
/// <param name="Name">The fixed-width index name stored in the directory slot.</param>
internal readonly record struct IndexDirectorySlotSnapshot(
    int SlotIndex,
    byte State,
    byte Flags,
    long RootRouterOffset,
    long MetadataOffset,
    long ItemCount,
    long Generation,
    ushort KeyProfileId,
    ushort IdentityProfileId,
    ushort RouterProfileId,
    ushort AllocationClassId,
    string Name)
{
    /// <summary>
    /// Gets whether this slot is active and should be considered part of the file's index catalog.<br/>
    /// Empty slots may contain zeroed field values and should not be exposed as indexes.<br/>
    /// </summary>
    public bool IsActive => State == Layouts.IndexDirectoryLayout.ActiveState;
}
