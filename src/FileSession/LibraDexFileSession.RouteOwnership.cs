using LibraDex.Layouts;
using LibraDex.Views;

namespace LibraDex;

internal sealed partial class LibraDexFileSession
{
    /// <summary>
    /// Refines a direct parent before its single-prefix shelf becomes a deeper router.<br/>
    /// The caller holds structural storage publication exclusion and proves the sorted source endpoints, including the incoming tuple, own the selected parent byte.<br/>
    /// Only aliases to the expected source are cleared; unrelated siblings are preserved. No image is allocated or written when ownership is already exact.<br/>
    /// The staged parent and replacement child must be committed together by the caller; this method never commits independently.<br/>
    /// </summary>
    /// <param name="parentOffset">The immediate owning direct router.<br/></param>
    /// <param name="sourceOffset">The shelf offset being replaced in place.<br/></param>
    /// <param name="childDepth">The first byte consumed by the replacement router.<br/></param>
    /// <param name="selectedPrefix">The revalidated incoming parent route.<br/></param>
    /// <param name="firstPrefix">The sorted source's first parent byte.<br/></param>
    /// <param name="lastPrefix">The sorted source's last parent byte.<br/></param>
    private void RefineDirectShelfOwner(long parentOffset, long sourceOffset, ushort childDepth,
        byte selectedPrefix, byte firstPrefix, byte lastPrefix)
    {
        if (childDepth == 0 || firstPrefix != selectedPrefix || lastPrefix != selectedPrefix)
            throw new InvalidDataException("A deeper shelf transform requires one exact parent-prefix owner; mixed prefixes require a parent-level split.");

        if (TryGetDirectRouterView(parentOffset, out DirectRouterView? view))
        {
            if (view!.KeyDepth + 1 != childDepth || view.GetTarget(selectedPrefix) != sourceOffset)
                throw new InvalidDataException("The shelf transform parent route changed before ownership publication.");
            bool shared = false;
            for (int prefix = 0; prefix < RouterLayout.MaxOneByteRouteCount; prefix++)
            {
                if (prefix != selectedPrefix && view.GetTarget((byte)prefix) == sourceOffset)
                {
                    shared = true;
                    break;
                }
            }
            if (!shared) return;
        }

        Span<byte> parentBytes = stackalloc byte[RouterLayout.Size];
        kernel.Read(parentOffset, parentBytes);
        RouterReader reader = new(parentBytes);
        if (!reader.IsValid || !reader.HasDirectIndex || reader.KeyDepth + 1 != childDepth ||
            reader.GetDirectTarget(selectedPrefix) != sourceOffset)
            throw new InvalidDataException("The shelf transform requires its unchanged immediate direct parent.");

        byte[]? rewrite = null;
        for (int prefix = 0; prefix < RouterLayout.MaxOneByteRouteCount; prefix++)
        {
            if (prefix == selectedPrefix || reader.GetDirectTarget((byte)prefix) != sourceOffset) continue;
            rewrite ??= parentBytes.ToArray();
            new RouterWriter(rewrite).WriteRouteTarget(prefix, 0);
        }
        if (rewrite is null) return;
        kernel.StageWriteAt(parentOffset, rewrite);
        InvalidateRouterReadCacheForRouterRewrite(parentOffset);
    }

    /// <summary>
    /// Publishes one populated fixed-scalar shelf into an unset direct route at any depth.<br/>
    /// Called only by structural insertion under the existing publication boundary; the parent is revalidated before any reservation.<br/>
    /// Shelf append and parent linkage share one commit, and parent classification caches are invalidated before reuse.<br/>
    /// </summary>
    /// <param name="parentOffset">The immediate router whose selected route is unset.<br/></param>
    /// <param name="prefix">The exact route byte to populate.<br/></param>
    /// <param name="shelfBytes">The initialized typed shelf containing its first tuple.<br/></param>
    /// <returns>The new shelf offset and its combined publication telemetry.<br/></returns>
    private (long Offset, DataKernelCommitTelemetry Commit) PublishColdFixedShelf(long parentOffset, byte prefix, ReadOnlySpan<byte> shelfBytes)
    {
        Span<byte> parent = stackalloc byte[RouterLayout.Size];
        kernel.Read(parentOffset, parent);
        RouterReader reader = new(parent);
        if (!reader.IsValid || !reader.HasDirectIndex || reader.GetDirectTarget(prefix) != 0)
            throw new InvalidDataException("The cold fixed-scalar parent route changed before publication.");
        var shelf = kernel.Reserve(shelfBytes.Length);
        shelfBytes.CopyTo(shelf.Span);
        var rewrite = kernel.ReserveAt(parentOffset, RouterLayout.Size);
        parent.CopyTo(rewrite.Span);
        new RouterWriter(rewrite.Span).WriteRouteTarget(prefix, shelf.Extent.Offset);
        InvalidateRouterReadCacheForRouterRewrite(parentOffset);
        return (shelf.Extent.Offset, CommitAndDeferRouterReadCacheInvalidation());
    }
}
