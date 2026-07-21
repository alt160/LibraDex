using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using LibraDex;
using LibraDex.Layouts;
using LibraDex.Views;
using Microsoft.Data.Sqlite;

return RawHarness.Run(args);

internal static partial class RawHarness
{
    private const int DefaultReservedPrefixBytes = 4096;
    private const int DefaultAppendBufferSize = 1024 * 1024;
    private const int DefaultScalar16Scalar8RoutedBulkWriteCommitGapCoalesceBytes = 16 * 1024;
    private const int DefaultScalar8Scalar16RoutedBulkWriteCommitGapCoalesceBytes = 16 * 1024;
    private const int DefaultScalar16Scalar16RoutedBulkWriteCommitGapCoalesceBytes = 16 * 1024;
    private const int ValidationBlockSize = 4 * 1024 * 1024;
    private const int ValidationAppendBufferSize = 16 * 1024 * 1024;
    private const double FileReserveStageBaselineMiBs = 2128.87;
    private const double FileReserveCommitBaselineMiBs = 1071.46;
    private const double FileReserveReadBaselineMiBs = 4530.94;
    private const double MemoryReserveStageBaselineMiBs = 2145.26;
    private const double MemoryReserveReadBaselineMiBs = 2326.61;
    private const double FormatStressBaselineIterationsPerSecond = 54.36;
    private const double ValidationWarnDriftPercent = 10;
    private const double ValidationFailDriftPercent = 20;
    private const double PreviousPublicBulkWriteSortedItemsPerSecond = 36961.88;
    private const double PreviousPublicBulkWriteRandomItemsPerSecond = 36371.78;
    private const double PreviousPublicBulkWriteBytesPerItem = 550.66;
    private const double DataKernel16ByteStreamSortedItemsPerSecond = 36748118.19;
    private const double DataKernel16ByteStreamRandomItemsPerSecond = 35433698.63;
    private const double LogicalScalar8Scalar8PayloadBytesPerItem = 16;
    private const double PreviousPublicGrowthFileBytesPerItem = 32.83;
    private const double PreviousPublicGrowthLiveBytesPerItem = 31.04;
    private const double PreviousPublicGrowthOverheadBytesPerItem = 1.79;
    private const double PreviousPublicGrowthWrittenBytesPerItem = 548.37;
    private const double PreviousPublicGrowthWritesPerBatch = 4.604;
    private const double PreviousPublicReadPrefix0IdentitiesPerSecond = 37658223.38;
    private const double PreviousPublicReadPrefixMiddleIdentitiesPerSecond = 56602705.61;
    private const double PreviousPublicReadPrefixLastIdentitiesPerSecond = 44258803.35;
    private const double PreviousPublicReadPrefix0To2IdentitiesPerSecond = 42875977.66;
    private const double PreviousPublicReadPrefix0To2CoalescedIdentitiesPerSecond = 45899106.11;
    private const double PreviousPublicReadPrefix0NsPerIdentity = 26.55;
    private const double PreviousPublicReadPrefixMiddleNsPerIdentity = 17.67;
    private const double PreviousPublicReadPrefixLastNsPerIdentity = 22.59;
    private const double PreviousPublicReadPrefix0To2NsPerIdentity = 23.32;
    private const double PreviousPublicReadPrefix0To2CoalescedNsPerIdentity = 21.79;
    private const double EncodedMedianPublicBulkWriteSortedItemsPerSecond = 1837965.73;
    private const double EncodedMedianPublicBulkWriteRandomItemsPerSecond = 1771758.11;
    private const double EncodedMedianPublicBulkWriteBytesPerItem = 91.84;
    private const double EncodedMedianPublicReadPrefix0IdentitiesPerSecond = 17907307.30;
    private const double EncodedMedianPublicReadPrefixMiddleIdentitiesPerSecond = 20658822.77;
    private const double EncodedMedianPublicReadPrefixLastIdentitiesPerSecond = 18809919.88;
    private const double EncodedMedianPublicReadPrefix0To2IdentitiesPerSecond = 44049326.07;
    private const double EncodedMedianPublicReadPrefix0To2CoalescedIdentitiesPerSecond = 66621095.99;
    private const double EncodedMedianPublicReadPrefix0NsPerIdentity = 55.84;
    private const double EncodedMedianPublicReadPrefixMiddleNsPerIdentity = 48.41;
    private const double EncodedMedianPublicReadPrefixLastNsPerIdentity = 53.16;
    private const double EncodedMedianPublicReadPrefix0To2NsPerIdentity = 22.70;
    private const double EncodedMedianPublicReadPrefix0To2CoalescedNsPerIdentity = 15.01;
    private const int DefaultVarKeyScalar8ColdCandidateDepth = 3;
    private const int DefaultVarKeyScalar8ColdCandidateShelfItems = 32;
    private const int DefaultVarKeyScalar8ColdCandidateHitCount = 12;

    private enum CommandLane
    {
        Validation,
        Performance
    }

    private readonly record struct HarnessCommand(string Name, CommandLane Lane, Func<string[], int> Handler);

    private static readonly HarnessCommand[] Commands =
    [
        new("sanity-raw", CommandLane.Validation, RunSanity),
        new("commit-coalescing-sanity", CommandLane.Validation, RunCommitCoalescingSanity),
        new("sanity-format", CommandLane.Validation, RunFormatSanity),
        new("validate", CommandLane.Validation, RunValidate),
        new("route-sanity", CommandLane.Validation, RunRouteSanity),
        new("ss8-8-sanity", CommandLane.Validation, RunScalar8Scalar8Sanity),
        new("ss16-8-sanity", CommandLane.Validation, RunScalar16Scalar8Sanity),
        new("fs32-8-sanity", CommandLane.Validation, RunFixed32Scalar8Sanity),
        new("fs32-16-sanity", CommandLane.Validation, RunFixed32Scalar16Sanity),
        new("ss8-16-sanity", CommandLane.Validation, RunScalar8Scalar16Sanity),
        new("ss16-16-sanity", CommandLane.Validation, RunScalar16Scalar16Sanity),
        new("ss8-8-dk-sanity", CommandLane.Validation, RunScalar8Scalar8DataKernelSanity),
        new("ss16-8-dk-sanity", CommandLane.Validation, RunScalar16Scalar8DataKernelSanity),
        new("fs32-8-dk-sanity", CommandLane.Validation, RunFixed32Scalar8DataKernelSanity),
        new("ss8-16-dk-sanity", CommandLane.Validation, RunScalar8Scalar16DataKernelSanity),
        new("ss16-16-dk-sanity", CommandLane.Validation, RunScalar16Scalar16DataKernelSanity),
        new("ss8-8-route-sanity", CommandLane.Validation, RunScalar8Scalar8RouteSanity),
        new("ss16-8-route-sanity", CommandLane.Validation, RunScalar16Scalar8RouteSanity),
        new("fs32-8-route-sanity", CommandLane.Validation, RunFixed32Scalar8RouteSanity),
        new("ss8-16-route-sanity", CommandLane.Validation, RunScalar8Scalar16RouteSanity),
        new("ss16-16-route-sanity", CommandLane.Validation, RunScalar16Scalar16RouteSanity),
        new("ss8-8-route-insert-sanity", CommandLane.Validation, RunScalar8Scalar8RouteInsertSanity),
        new("ss16-8-route-insert-sanity", CommandLane.Validation, RunScalar16Scalar8RouteInsertSanity),
        new("fs32-8-route-insert-sanity", CommandLane.Validation, RunFixed32Scalar8RouteInsertSanity),
        new("ss8-16-route-insert-sanity", CommandLane.Validation, RunScalar8Scalar16RouteInsertSanity),
        new("ss16-16-route-insert-sanity", CommandLane.Validation, RunScalar16Scalar16RouteInsertSanity),
        new("ss8-8-route-split-sanity", CommandLane.Validation, RunScalar8Scalar8RouteSplitSanity),
        new("ss16-8-route-split-sanity", CommandLane.Validation, RunScalar16Scalar8RouteSplitSanity),
        new("fs32-8-route-split-sanity", CommandLane.Validation, RunFixed32Scalar8RouteSplitSanity),
        new("ss8-16-route-split-sanity", CommandLane.Validation, RunScalar8Scalar16RouteSplitSanity),
        new("ss16-16-route-split-sanity", CommandLane.Validation, RunScalar16Scalar16RouteSplitSanity),
        new("identity16-split-preservation-sanity", CommandLane.Validation, RunIdentity16SplitPreservationSanity),
        new("ss8-8-route-transform-sanity", CommandLane.Validation, RunScalar8Scalar8RouteTransformSanity),
        new("ss16-8-route-transform-sanity", CommandLane.Validation, RunScalar16Scalar8RouteTransformSanity),
        new("fs32-8-route-transform-sanity", CommandLane.Validation, RunFixed32Scalar8RouteTransformSanity),
        new("ss8-16-route-transform-sanity", CommandLane.Validation, RunScalar8Scalar16RouteTransformSanity),
        new("ss16-16-route-transform-sanity", CommandLane.Validation, RunScalar16Scalar16RouteTransformSanity),
        new("ss8-8-routed-insert-decision-sanity", CommandLane.Validation, RunScalar8Scalar8RoutedInsertDecisionSanity),
        new("ss8-8-two-level-insert-sanity", CommandLane.Validation, RunScalar8Scalar8TwoLevelInsertSanity),
        new("ss16-8-two-level-insert-sanity", CommandLane.Validation, RunScalar16Scalar8TwoLevelInsertSanity),
        new("fs32-8-two-level-insert-sanity", CommandLane.Validation, RunFixed32Scalar8TwoLevelInsertSanity),
        new("ss8-16-two-level-insert-sanity", CommandLane.Validation, RunScalar8Scalar16TwoLevelInsertSanity),
        new("ss16-16-two-level-insert-sanity", CommandLane.Validation, RunScalar16Scalar16TwoLevelInsertSanity),
        new("ss8-8-route-walker-sanity", CommandLane.Validation, RunScalar8Scalar8RouteWalkerSanity),
        new("ss16-8-route-walker-sanity", CommandLane.Validation, RunScalar16Scalar8RouteWalkerSanity),
        new("fs32-8-route-walker-sanity", CommandLane.Validation, RunFixed32Scalar8RouteWalkerSanity),
        new("ss8-16-route-walker-sanity", CommandLane.Validation, RunScalar8Scalar16RouteWalkerSanity),
        new("ss16-16-route-walker-sanity", CommandLane.Validation, RunScalar16Scalar16RouteWalkerSanity),
        new("ss8-8-walked-insert-sanity", CommandLane.Validation, RunScalar8Scalar8WalkedInsertSanity),
        new("ss16-8-walked-insert-sanity", CommandLane.Validation, RunScalar16Scalar8WalkedInsertSanity),
        new("fs32-8-walked-insert-sanity", CommandLane.Validation, RunFixed32Scalar8WalkedInsertSanity),
        new("ss8-16-walked-insert-sanity", CommandLane.Validation, RunScalar8Scalar16WalkedInsertSanity),
        new("ss16-16-walked-insert-sanity", CommandLane.Validation, RunScalar16Scalar16WalkedInsertSanity),
        new("ss8-8-walked-transform-sanity", CommandLane.Validation, RunScalar8Scalar8WalkedTransformSanity),
        new("ss16-8-walked-transform-sanity", CommandLane.Validation, RunScalar16Scalar8WalkedTransformSanity),
        new("fs32-8-walked-transform-sanity", CommandLane.Validation, RunFixed32Scalar8WalkedTransformSanity),
        new("ss8-16-walked-transform-sanity", CommandLane.Validation, RunScalar8Scalar16WalkedTransformSanity),
        new("ss16-16-walked-transform-sanity", CommandLane.Validation, RunScalar16Scalar16WalkedTransformSanity),
        new("ss8-8-parent-route-split-sanity", CommandLane.Validation, RunScalar8Scalar8ParentRouteSplitSanity),
        new("ss16-8-parent-route-split-sanity", CommandLane.Validation, RunScalar16Scalar8ParentRouteSplitSanity),
        new("fs32-8-parent-route-split-sanity", CommandLane.Validation, RunFixed32Scalar8ParentRouteSplitSanity),
        new("ss8-16-parent-route-split-sanity", CommandLane.Validation, RunScalar8Scalar16ParentRouteSplitSanity),
        new("ss8-16-walked-decision-sanity", CommandLane.Validation, RunScalar8Scalar16WalkedDecisionSanity),
        new("ss16-16-walked-decision-sanity", CommandLane.Validation, RunScalar16Scalar16WalkedDecisionSanity),
        new("ss8-8-router-arena-sanity", CommandLane.Validation, RunScalar8Scalar8RouterArenaSanity),
        new("ss8-8-router-arena-policy-sanity", CommandLane.Validation, RunScalar8Scalar8RouterArenaPolicySanity),
        new("ss8-8-router-arena-read-cache-sanity", CommandLane.Validation, RunScalar8Scalar8RouterArenaReadCacheSanity),
        new("ss8-8-route-batch-policy-sanity", CommandLane.Validation, RunScalar8Scalar8RouteBatchPolicySanity),
        new("ss8-8-batch-lookup-sanity", CommandLane.Validation, RunScalar8Scalar8BatchLookupSanity),
        new("ss8-8-range-scoop-sanity", CommandLane.Validation, RunScalar8Scalar8RangeScoopSanity),
        new("ss8-8-handle-reopen-sanity", CommandLane.Validation, RunScalar8Scalar8HandleReopenSanity),
        new("ss8-8-index-api-sanity", CommandLane.Validation, RunScalar8Scalar8IndexApiSanity),
        new("ss8-8-profile-sanity", CommandLane.Validation, RunScalar8Scalar8ProfileSanity),
        new("ss8-8-typed-api-sanity", CommandLane.Validation, RunUnsignedScalar8Scalar8IndexApiSanity),
        new("generic-index-api-sanity", CommandLane.Validation, RunGenericIndexApiSanity),
        new("bigint-api-sanity", CommandLane.Validation, RunBigIntApiSanity),
        new("fixedn-shelf-sanity", CommandLane.Validation, RunFixedNShelfSanity),
        new("fixedn-varidentity-routed-sanity", CommandLane.Validation, RunFixedNVarIdentityRoutedSanity),
        new("catalog-api-sanity", CommandLane.Validation, RunCatalogApiSanity),
        new("public-surface-api-sanity", CommandLane.Validation, RunPublicSurfaceApiSanity),
        new("public-api-snapshot", CommandLane.Validation, RunPublicApiSnapshot),
        new("solution-quality-sanity", CommandLane.Validation, RunSolutionQualitySanity),
        new("generic-count-fanout-proof", CommandLane.Validation, RunGenericCountFanoutProof),
        new("non-generic-count-fanout-proof", CommandLane.Validation, RunNonGenericCountFanoutProof),
        new("write-intent-sanity", CommandLane.Validation, RunWriteIntentSanity),
        new("concurrency-contract-sanity", CommandLane.Validation, RunConcurrencyContractSanity),
        new("concurrency-admission-sanity", CommandLane.Validation, RunConcurrencyAdmissionSanity),
        new("concurrency-workload-matrix", CommandLane.Validation, RunConcurrencyWorkloadMatrix),
        new("ss8-8-primitive-concurrency-proof", CommandLane.Performance, RunScalar8Scalar8PrimitiveConcurrencyProof),
        new("concurrency-performance-proof", CommandLane.Performance, RunConcurrencyPerformanceProof),
        new("fixedn-concurrency-performance-proof", CommandLane.Performance, RunFixedNConcurrencyPerformanceProof),
        new("fixedn-batch-coalescing-proof", CommandLane.Performance, RunFixedNBatchCoalescingProof),
        new("group-by-execution-proof", CommandLane.Performance, RunGroupByExecutionProof),
        new("group-by-direct-target-proof", CommandLane.Performance, RunGroupByDirectTargetProof),
        new("group-by-composite-proof", CommandLane.Performance, RunGroupByCompositeProof),
        new("group-by-string-proof", CommandLane.Performance, RunGroupByStringProof),
        new("string-prefix-count-proof", CommandLane.Performance, RunStringPrefixCountProof),
        new("generic-scalar8-between-count-proof", CommandLane.Performance, RunGenericScalar8BetweenCountProof),
        new("generic-wide-between-count-proof", CommandLane.Performance, RunGenericScalarWideBetweenCountProof),
        new("group-by-string-key-state-promotion-proof", CommandLane.Performance, RunGroupByStringKeyStatePromotionProof),
        new("scalar16-key-state-promotion-proof", CommandLane.Performance, RunScalar16KeyStatePromotionProof),
        new("fixedn-key-state-promotion-proof", CommandLane.Performance, RunFixedNKeyStatePromotionProof),
        new("group-by-aggregate-proof", CommandLane.Performance, RunGroupByAggregateProof),
        new("group-by-row-reader-proof", CommandLane.Performance, RunGroupByRowReaderProof),
        new("condition-result-latest-reader-sanity", CommandLane.Validation, RunConditionResultLatestReaderSanity),
        new("fixedn-scalar-range-count-perf", CommandLane.Performance, RunFixedNScalarRangeCountPerf),
        new("fixedn-varidentity-range-count-perf", CommandLane.Performance, RunFixedNVarIdentityRangeCountPerf),
        new("duplicate-run-sanity", CommandLane.Validation, RunDuplicateRunSanity),
        new("filesearch-path-index-repro", CommandLane.Validation, RunFileSearchPathIndexRepro),
        new("filesearch-metadata-index-repro", CommandLane.Validation, RunFileSearchMetadataIndexRepro),
        new("filesearch-group-batch-repro", CommandLane.Validation, RunFileSearchGroupBatchRepro),
        new("filesearch-ui-shaped-repro", CommandLane.Validation, RunFileSearchUiShapedRepro),
        new("filesearch-pathidentity-sv8-repro", CommandLane.Validation, RunFileSearchPathIdentityScalar8VarIdentityRepro),
        new("sv8-shelf-sanity", CommandLane.Validation, RunScalar8VarIdentityShelfSanity),
        new("sv8-routed-sanity", CommandLane.Validation, RunScalar8VarIdentityRoutedSanity),
        new("sv8-index-api-sanity", CommandLane.Validation, RunScalar8VarIdentityIndexApiSanity),
        new("sv8-read-cache-sanity", CommandLane.Validation, RunScalar8VarIdentityReadCacheSanity),
        new("sv16-shelf-sanity", CommandLane.Validation, RunScalar16VarIdentityShelfSanity),
        new("sv16-read-cache-sanity", CommandLane.Validation, RunScalar16VarIdentityReadCacheSanity),
        new("sv16-routed-sanity", CommandLane.Validation, RunScalar16VarIdentityRoutedSanity),
        new("sv16-index-api-sanity", CommandLane.Validation, RunScalar16VarIdentityIndexApiSanity),
        new("vs8-shelf-sanity", CommandLane.Validation, RunVarKeyScalar8ShelfSanity),
        new("vs16-shelf-sanity", CommandLane.Validation, RunVarKeyScalar16ShelfSanity),
        new("vs16-routed-sanity", CommandLane.Validation, RunVarKeyScalar16RoutedSanity),
        new("vs8-index-api-sanity", CommandLane.Validation, RunVarKeyScalar8IndexApiSanity),
        new("vs16-index-api-sanity", CommandLane.Validation, RunVarKeyScalar16IndexApiSanity),
        new("vs8-routed-sanity", CommandLane.Validation, RunVarKeyScalar8RoutedSanity),
        new("vs8-mb-router-transform-sanity", CommandLane.Validation, RunVarKeyScalar8MultiByteRouterTransformSanity),
        new("vs8-router-arena-reopen-sanity", CommandLane.Validation, RunVarKeyScalar8RouterArenaReopenSanity),
        new("vs8-router-arena-exhaustion-sanity", CommandLane.Validation, RunVarKeyScalar8RouterArenaExhaustionSanity),
        new("vs8-route-versioned-publish-sanity", CommandLane.Validation, RunVarKeyScalar8RouteVersionedPublishSanity),
        new("vs8-scoop-topology-proof", CommandLane.Validation, RunVarKeyScalar8ScoopTopologyProof),
        new("varlen-promoted-router-view-sanity", CommandLane.Validation, RunVarLenPromotedRouterViewSanity),
        new("varlen-queued-maintenance-sanity", CommandLane.Validation, RunVarLenQueuedMaintenanceSanity),
        new("varlen-optimizer-modes-sanity", CommandLane.Validation, RunVarLenOptimizerModesSanity),
        new("varlen-optimizer-lifecycle-sanity", CommandLane.Validation, RunVarLenOptimizerLifecycleSanity),
        new("varlen-count-all-sanity", CommandLane.Validation, RunVarLenCountAllSanity),
        new("varlen-mb-range-count-sanity", CommandLane.Validation, RunVarLenMultiByteRangeCountSanity),
        new("varlen-mb-range-count-perf", CommandLane.Performance, RunVarLenMultiByteRangeCountPerf),
        new("vv-shelf-sanity", CommandLane.Validation, RunVarKeyVarIdentityShelfSanity),
        new("vv-routed-sanity", CommandLane.Validation, RunVarKeyVarIdentityRoutedSanity),
        new("vv-lazy-root-abort-sanity", CommandLane.Validation, RunVarKeyVarIdentityLazyRootAbortSanity),
        new("vv-index-api-sanity", CommandLane.Validation, RunVarKeyVarIdentityIndexApiSanity),
        new("ss8-8-batch-sanity", CommandLane.Validation, RunScalar8Scalar8BatchSanity),
        new("multi-index-same-identities-sanity", CommandLane.Validation, RunMultiIndexEightSameIdentitiesSanity),
        new("stress-format", CommandLane.Performance, RunFormatStress),
        new("perf-raw", CommandLane.Performance, RunPerf),
        new("small-io-baseline", CommandLane.Performance, RunSmallIoBaseline),
        new("storage-baseline", CommandLane.Performance, RunStorageBaseline),
        new("route-perf", CommandLane.Performance, RunRoutePerf),
        new("route-shapes", CommandLane.Performance, RunRouteShapes),
        new("route-stress", CommandLane.Performance, RunRouteStress),
        new("ss8-8-design-perf", CommandLane.Performance, RunScalar8Scalar8DesignPerf),
        new("ss16-8-design-perf", CommandLane.Performance, RunScalar16Scalar8DesignPerf),
        new("fs32-8-design-perf", CommandLane.Performance, RunFixed32Scalar8DesignPerf),
        new("ss8-16-design-perf", CommandLane.Performance, RunScalar8Scalar16DesignPerf),
        new("ss16-16-design-perf", CommandLane.Performance, RunScalar16Scalar16DesignPerf),
        new("ss16-16-size-sweep", CommandLane.Performance, RunScalar16Scalar16SizeSweep),
        new("ss8-8-growth-slack", CommandLane.Performance, RunScalar8Scalar8GrowthSlack),
        new("ss8-8-router-arena-read-cache-perf", CommandLane.Performance, RunScalar8Scalar8RouterArenaReadCachePerf),
        new("ss8-8-batch-lookup-perf", CommandLane.Performance, RunScalar8Scalar8BatchLookupPerf),
        new("fixedn-shelf-perf", CommandLane.Performance, RunFixedNShelfPerf),
        new("fixed-reader-perf", CommandLane.Performance, RunFixedReaderPerf),
        new("var-identity-buffer-perf", CommandLane.Performance, RunVarIdentityBufferPerf),
        new("sv8-routed-sqlite-comparison", CommandLane.Performance, RunScalar8VarIdentityRoutedSqliteComparison),
        new("sv16-routed-sqlite-comparison", CommandLane.Performance, RunScalar16VarIdentityRoutedSqliteComparison),
        new("vs8-sqlite-comparison", CommandLane.Performance, RunVarKeyScalar8SqliteComparison),
        new("vs8-hierarchical-key-comparison", CommandLane.Performance, RunVarKeyScalar8HierarchicalKeyComparison),
        new("vs16-hierarchical-key-comparison", CommandLane.Performance, RunVarKeyScalar16HierarchicalKeyComparison),
        new("varlen-hierarchical-median-comparison", CommandLane.Performance, RunVarLenHierarchicalMedianComparison),
        new("vs8-write-intent-matrix", CommandLane.Performance, RunVarKeyScalar8WriteIntentMatrix),
        new("vs8-routed-sqlite-comparison", CommandLane.Performance, RunVarKeyScalar8RoutedSqliteComparison),
        new("vs16-routed-sqlite-comparison", CommandLane.Performance, RunVarKeyScalar16RoutedSqliteComparison),
        new("vv-routed-sqlite-comparison", CommandLane.Performance, RunVarKeyVarIdentityRoutedSqliteComparison),
        new("ss8-8-range-scoop-perf", CommandLane.Performance, RunScalar8Scalar8RangeScoopPerf),
        new("ss8-8-range-scoop-wide-perf", CommandLane.Performance, RunScalar8Scalar8RangeScoopWidePerf),
        new("ss8-8-normalized-checkpoint", CommandLane.Performance, RunScalar8Scalar8NormalizedCheckpoint),
        new("ss8-8-write-parity", CommandLane.Performance, RunScalar8Scalar8WriteParity),
        new("ss8-8-routed-bulk-write", CommandLane.Performance, RunScalar8Scalar8RoutedBulkWrite),
        new("ss16-8-routed-bulk-write", CommandLane.Performance, RunScalar16Scalar8RoutedBulkWrite),
        new("ss8-16-routed-bulk-write", CommandLane.Performance, RunScalar8Scalar16RoutedBulkWrite),
        new("ss16-16-routed-bulk-write", CommandLane.Performance, RunScalar16Scalar16RoutedBulkWrite),
        new("ss8-8-routed-cadence-suite", CommandLane.Performance, RunScalar8Scalar8RoutedCadenceSuite),
        new("ss8-8-routed-write-attribution", CommandLane.Performance, RunScalar8Scalar8RoutedWriteAttribution),
        new("ss8-8-public-growth", CommandLane.Performance, RunScalar8Scalar8PublicGrowth),
        new("ss8-8-public-bulk-read", CommandLane.Performance, RunScalar8Scalar8PublicBulkRead),
        new("ss8-8-typed-routed-bulk-write", CommandLane.Performance, RunUnsignedScalar8Scalar8RoutedBulkWrite),
        new("ss8-8-typed-public-bulk-read", CommandLane.Performance, RunUnsignedScalar8Scalar8PublicBulkRead),
        new("all-shape-write-parity", CommandLane.Performance, RunAllShapeWriteParity),
        new("all-shape-read-range-sweep", CommandLane.Performance, RunAllShapeReadRangeSweep),
        new("fs32-8-read-parity", CommandLane.Performance, RunFixed32Scalar8ReadParity),
        new("fs32-16-read-parity", CommandLane.Performance, RunFixed32Scalar16ReadParity),
        new("fs32-8-size-sweep", CommandLane.Performance, RunFixed32Scalar8SizeSweep),
        new("fs32-16-size-sweep", CommandLane.Performance, RunFixed32Scalar16SizeSweep),
        new("sqlite-ss8-8-comparison", CommandLane.Performance, RunSqliteScalar8Scalar8Comparison),
        new("sqlite-ss16-16-comparison", CommandLane.Performance, RunSqliteScalar16Scalar16Comparison),
        new("ss8-8-public-read-targets", CommandLane.Performance, RunScalar8Scalar8PublicReadTargets),
        new("indexset-inverse-perf", CommandLane.Performance, RunIndexSetInversePerf),
        new("ss8-8-perf", CommandLane.Performance, RunScalar8Scalar8Perf),
        new("ss8-8-size-sweep", CommandLane.Performance, RunScalar8Scalar8SizeSweep),
    ];

    public static int Run(string[] args)
    {
        string command = args.Length == 0 ? "validate" : args[0];

        return command switch
        {
            "validate" or "validation" or "correctness" => RunValidation(args),
            "perf" or "performance" => RunPerformance(args),
            _ when TryFindCommand(command, CommandLane.Validation, out _) => RunValidationCommand(args),
            _ when TryFindCommand(command, CommandLane.Performance, out _) => RunPerformanceCommand(args),
            _ => PrintUsage(command)
        };
    }

    private static int RunValidation(string[] args)
    {
        if (args.Length <= 1 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return RunValidate(args);
        }

        return RunValidationCommand(ShiftArgs(args));
    }

    private static int RunPerformance(string[] args)
    {
        if (args.Length <= 1 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return PrintUsage(args.Length == 0 ? "perf" : args[0]);
        }

        return RunPerformanceCommand(ShiftArgs(args));
    }

    private static string[] ShiftArgs(string[] args)
    {
        string[] shifted = new string[args.Length - 1];
        Array.Copy(args, 1, shifted, 0, shifted.Length);
        return shifted;
    }

    private static int RunValidationCommand(string[] args)
    {
        string command = args.Length == 0 ? "validate" : args[0];
        return TryFindCommand(command, CommandLane.Validation, out HarnessCommand spec)
            ? spec.Handler(args)
            : PrintUsage(command);
    }

    private static int RunPerformanceCommand(string[] args)
    {
        string command = args.Length == 0 ? "perf" : args[0];
        return TryFindCommand(command, CommandLane.Performance, out HarnessCommand spec)
            ? spec.Handler(args)
            : PrintUsage(command);
    }

    private static bool TryFindCommand(string command, CommandLane lane, out HarnessCommand spec)
    {
        for (int i = 0; i < Commands.Length; i++)
        {
            HarnessCommand candidate = Commands[i];
            if (candidate.Lane == lane && string.Equals(candidate.Name, command, StringComparison.Ordinal))
            {
                spec = candidate;
                return true;
            }
        }

        spec = default;
        return false;
    }

}
