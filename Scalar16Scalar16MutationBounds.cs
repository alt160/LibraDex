namespace LibraDex;

/// <summary>
/// Describes the conservative byte ranges changed by one `SS16-16` shelf mutation.<br/>
/// The ranges are relative to the start of the shelf extent and are intended for batch-local dirty-region tracking.<br/>
/// </summary>
/// <param name="HeaderOffset">The changed header byte offset.</param>
/// <param name="HeaderLength">The changed header byte length.</param>
/// <param name="SlotOffset">The changed slot-region byte offset.</param>
/// <param name="SlotLength">The changed slot-region byte length.</param>
/// <param name="ItemOffset">The changed item-region byte offset.</param>
/// <param name="ItemLength">The changed item-region byte length.</param>
internal readonly record struct Scalar16Scalar16MutationBounds(
    int HeaderOffset,
    int HeaderLength,
    int SlotOffset,
    int SlotLength,
    int ItemOffset,
    int ItemLength);
