using LibraDex.Layouts;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Walks one active file-backed `FS32-8` generation and returns exact reachable router, shelf, tuple, and byte counts.<br/>
    /// Router aliases are deduplicated by physical offset because one fixed-key shelf may be reachable through multiple adjacent route bytes without occupying multiple extents.<br/>
    /// This method performs no mutation and rejects invalid headers, unsupported allocation classes, or unexpected reachable target kinds.<br/>
    /// </summary>
    /// <param name="slotIndex">The active fixed-directory slot whose current root generation should be assessed.<br/></param>
    /// <returns>Exact current-generation topology and tuple accounting.<br/></returns>
    internal Fixed32Scalar8TopologyAssessment AssessFixed32Scalar8Topology(int slotIndex)
    {
        Fixed32Scalar8Profile profile = Fixed32Scalar8Profile.Default40KiB;
        OrdinaryFixedScalarTopologyAssessment topology = AssessOrdinaryFixedScalarTopology(
            slotIndex,
            Fixed32Scalar8Layout.Magic,
            Fixed32Scalar8Layout.FormatVersion,
            Fixed32Scalar8Layout.HeaderSize,
            profile.MaxItemCount,
            profile.ShelfExtentSize,
            "FS32-8",
            terminalShape: TerminalIdentityRootLayout.ShapeFixedKeyScalar8Identity,
            terminalKeyLength: Fixed32Scalar8Layout.KeySize);

        return new Fixed32Scalar8TopologyAssessment(
            topology.TupleCount,
            topology.ReachableBytes,
            topology.RouterCount,
            topology.ShelfCount,
            topology.ShelfExtentSize,
            topology.ClaimedOffsets);
    }

    /// <summary>
    /// Maps a persisted `FS32-8` directory profile to the exact file-backed shelf extent used by the current format.<br/>
    /// Allocation-class identifiers are shape-relative; class one therefore selects the tuned 40 KiB fixed-32 shelf rather than the 32 KiB scalar-8 shelf used by `SS8-8`.<br/>
    /// </summary>
    /// <param name="slot">The active directory slot whose profile identifiers are validated together.<br/></param>
    /// <returns>The supported file-backed `FS32-8` profile.<br/></returns>
    private static Fixed32Scalar8Profile GetFixed32Scalar8AssessmentProfile(IndexDirectorySlotSnapshot slot)
    {
        if (slot.KeyProfileId == 1 &&
            slot.IdentityProfileId == 1 &&
            slot.RouterProfileId == 1 &&
            slot.AllocationClassId == 1)
        {
            return Fixed32Scalar8Profile.Default40KiB;
        }

        throw new ArgumentException("The index-directory slot does not describe the supported file-backed FS32-8 profile.", nameof(slot));
    }
}

/// <summary>
/// Carries exact current-generation physical accounting for one active file-backed `FS32-8` topology.<br/>
/// </summary>
internal readonly record struct Fixed32Scalar8TopologyAssessment(
    long TupleCount,
    long ReachableBytes,
    int RouterCount,
    int ShelfCount,
    int ShelfExtentSize,
    long[] ClaimedOffsets);
