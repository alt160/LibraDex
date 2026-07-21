using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LibraDex.ShapeBench;

internal static class Program
{
    private const int DefaultItems = 250_000;
    private const int CurrentScenarioSchemaVersion = 2;
    private const string ReadCsvHeader = "id,shape,workload,workload_kind,batch_size,threads,dataset_items,repetitions,libradex_mean_operations,sqlite_mean_operations,result_items,libradex_mean_operations_per_second,libradex_median_operations_per_second,libradex_minimum_operations_per_second,libradex_maximum_operations_per_second,libradex_spread_percent,libradex_cold_operations_per_second,sqlite_mean_operations_per_second,sqlite_median_operations_per_second,sqlite_minimum_operations_per_second,sqlite_maximum_operations_per_second,sqlite_spread_percent,sqlite_cold_operations_per_second,libradex_first_count_latency_milliseconds,sqlite_first_count_latency_milliseconds,libradex_over_sqlite,campaign_id,binary_sha256,machine_name,runtime_version,measurement_utc,notes";
    private const string ScalingCsvHeader = "shape,workload,batch_size,threads,dataset_items,repetitions,median_operations_per_second,minimum_operations_per_second,maximum_operations_per_second,spread_percent,result_items,t1_median_operations_per_second,aggregate_over_t1,parallel_efficiency_percent,campaign_id,binary_sha256,measurement_utc,notes";
    private const string CsvHeader = "id,shelf_shape,workload,workload_kind,batch_size,threads,operations,libradex_operations_per_second,sqlite_operations_per_second,result_items,libradex_items_per_second,sqlite_items_per_second,libradex_ram_mb,sqlite_ram_mb,libradex_disk_mb,sqlite_disk_mb,notes,libradex_active_threads,sqlite_active_threads,libradex_thread_avg_items_per_second,sqlite_thread_avg_items_per_second,libradex_thread_median_items_per_second,sqlite_thread_median_items_per_second,libradex_thread_max_items_per_second,sqlite_thread_max_items_per_second,libradex_thread_min_items,sqlite_thread_min_items,libradex_thread_max_items,sqlite_thread_max_items,libradex_chunks,sqlite_chunks,libradex_publications,sqlite_transactions,libradex_items_per_publication,sqlite_items_per_transaction,libradex_thread_min_items_per_second,sqlite_thread_min_items_per_second,concurrency_locality,concurrency_plan_workers,libradex_distinct_shelves,libradex_min_shelves_per_worker,libradex_max_shelves_per_worker,libradex_shared_shelves,libradex_max_workers_per_shelf,libradex_shared_shelf_item_percent,libradex_distinct_parent_routers,libradex_min_parent_routers_per_worker,libradex_max_parent_routers_per_worker,libradex_shared_parent_routers,libradex_max_workers_per_parent_router,libradex_shared_parent_router_item_percent,dataset_items,dataset_kind,scenario_schema_version,measurement_utc,campaign_id,binary_sha256,machine_name,runtime_version,libradex_batch_samples,libradex_batch_latency_p50_ms,libradex_batch_latency_p95_ms,libradex_batch_latency_p99_ms,libradex_batch_latency_max_ms,libradex_publish_latency_p50_ms,libradex_publish_latency_p95_ms,libradex_publish_latency_p99_ms,libradex_publish_latency_max_ms,libradex_conflicted_batches,libradex_conflicted_batch_latency_p50_ms,libradex_conflicted_batch_latency_p95_ms,libradex_conflicted_batch_latency_p99_ms,libradex_unconflicted_batch_latency_p50_ms,libradex_unconflicted_batch_latency_p95_ms,libradex_unconflicted_batch_latency_p99_ms";
    private const string InteractiveTableCss = """
.interactive-table-host{overflow-x:auto}.table-tools{display:flex;align-items:center;justify-content:flex-end;gap:8px;padding:6px 8px;background:inherit;color:inherit;font-size:12px}.table-tools button,.table-tools summary{border:1px solid #52627a;border-radius:5px;background:#202b3c;color:inherit;padding:4px 8px;cursor:pointer;font:inherit}.column-picker{position:relative}.column-picker summary{list-style:none}.column-picker summary::-webkit-details-marker{display:none}.column-picker-menu{position:absolute;right:0;z-index:20;min-width:220px;max-height:360px;overflow:auto;padding:8px;background:#202b3c;border:1px solid #52627a;border-radius:6px;box-shadow:0 10px 30px #0007}.column-picker-menu label{display:flex;gap:7px;align-items:center;padding:4px;white-space:nowrap;text-transform:none;font-weight:400}.interactive-table{table-layout:fixed;width:max-content;min-width:100%}.interactive-table th,.interactive-table td{overflow:hidden;text-overflow:ellipsis}.interactive-table th{position:relative;cursor:pointer;user-select:none}.interactive-table th[data-sort-direction=asc]::after{content:' ▲'}.interactive-table th[data-sort-direction=desc]::after{content:' ▼'}.column-resizer{position:absolute;top:0;right:-3px;width:7px;height:100%;cursor:col-resize;touch-action:none;z-index:2}.column-resizer:hover{background:#67e8f955}.table-tools-spacer{flex:1}.table-tools-hint{opacity:.7}
""";
    private const string InteractiveTableScript = """
(()=>{
const storageKey='LibraDex.ShapeBench.tables:'+location.pathname;
let states={};try{states=JSON.parse(localStorage.getItem(storageKey)||'{}')}catch{}
let scheduled=false;
const save=()=>{try{localStorage.setItem(storageKey,JSON.stringify(states))}catch{}};
const value=text=>{const raw=text.trim().replaceAll(',',''),match=/^(-?(?:\d+\.?\d*|\.\d+))(?:x|%)?$/.exec(raw);return match?Number(match[1]):text.trim().toLocaleLowerCase()};
const compare=(a,b)=>typeof a==='number'&&typeof b==='number'?a-b:String(a).localeCompare(String(b),undefined,{numeric:true,sensitivity:'base'});
function apply(table,key,state){
 const cols=table.querySelectorAll('colgroup col'),headers=[...table.querySelectorAll('thead tr:first-child th')];
 let width=0;
 headers.forEach((header,index)=>{
  const hidden=!!state.hidden?.[index],column=cols[index],columnWidth=Math.max(56,Number(state.widths?.[index])||Math.ceil(header.getBoundingClientRect().width)||96);
  if(column){column.style.display=hidden?'none':'';column.style.width=columnWidth+'px'}
  for(const row of table.rows){if(row.cells[index])row.cells[index].style.display=hidden?'none':''}
  if(!hidden)width+=columnWidth;
 });
 table.style.width=Math.max(width,table.parentElement?.clientWidth||0)+'px';
 headers.forEach(header=>delete header.dataset.sortDirection);
 if(Number.isInteger(state.sortIndex)&&headers[state.sortIndex])headers[state.sortIndex].dataset.sortDirection=state.sortDirection;
}
function sort(table,key,state,index,direction){
 const body=table.tBodies[0];if(!body)return;
 const rows=[...body.rows].map((row,position)=>({row,position,v:value(row.cells[index]?.textContent||'')}));
 rows.sort((a,b)=>{const result=compare(a.v,b.v);return(direction==='asc'?result:-result)||a.position-b.position});
 for(const item of rows)body.appendChild(item.row);
 state.sortIndex=index;state.sortDirection=direction;save();apply(table,key,state);
}
function enhance(table,index){
 const headers=[...table.querySelectorAll('thead tr:first-child th')];if(!headers.length)return;
 const signature=headers.map(x=>x.textContent.trim()).join('|'),key=table.id||'table-'+index+'-'+signature;
 if(table.dataset.enhancedSignature===signature&&table.querySelector('colgroup'))return;
 table.dataset.enhancedSignature=signature;table.classList.add('interactive-table');table.parentElement?.classList.add('interactive-table-host');
 const previous=table.previousElementSibling;if(previous?.classList.contains('table-tools'))previous.remove();
 table.querySelector('colgroup')?.remove();const colgroup=document.createElement('colgroup');
 for(let i=0;i<headers.length;i++)colgroup.appendChild(document.createElement('col'));table.insertBefore(colgroup,table.firstChild);
 const state=states[key]??={hidden:{},widths:{},sortIndex:null,sortDirection:'asc'};
 const tools=document.createElement('div');tools.className='table-tools';tools.innerHTML='<span class="table-tools-hint">Click a heading to sort; drag its edge to resize.</span><span class="table-tools-spacer"></span><button type="button" class="show-columns">Show all</button><button type="button" class="reset-widths">Reset widths</button>';
 const picker=document.createElement('details');picker.className='column-picker';picker.innerHTML='<summary>Columns</summary><div class="column-picker-menu"></div>';const menu=picker.lastElementChild;
 headers.forEach((header,columnIndex)=>{
  for(const oldHandle of header.querySelectorAll('.column-resizer'))oldHandle.remove();
  const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';check.checked=!state.hidden?.[columnIndex];label.append(check,document.createTextNode(header.textContent.trim()));menu.appendChild(label);
  check.onchange=()=>{state.hidden[columnIndex]=!check.checked;save();apply(table,key,state)};
  header.title='Sort by '+header.textContent.trim();header.onclick=event=>{if(event.target.closest('.column-resizer'))return;const direction=state.sortIndex===columnIndex&&state.sortDirection==='asc'?'desc':'asc';sort(table,key,state,columnIndex,direction)};
  const handle=document.createElement('span');handle.className='column-resizer';handle.onpointerdown=event=>{
   event.preventDefault();event.stopPropagation();const startX=event.clientX,startWidth=Math.max(56,Number(state.widths?.[columnIndex])||header.getBoundingClientRect().width);
   const move=moveEvent=>{state.widths[columnIndex]=Math.max(56,Math.round(startWidth+moveEvent.clientX-startX));apply(table,key,state)};
   const up=()=>{document.removeEventListener('pointermove',move);document.removeEventListener('pointerup',up);save()};document.addEventListener('pointermove',move);document.addEventListener('pointerup',up);
  };header.appendChild(handle);
 });
 tools.querySelector('.show-columns').onclick=()=>{state.hidden={};for(const check of menu.querySelectorAll('input'))check.checked=true;save();apply(table,key,state)};
 tools.querySelector('.reset-widths').onclick=()=>{state.widths={};save();table.style.width='';table.dataset.enhancedSignature='';enhance(table,index)};
 tools.appendChild(picker);table.before(tools);apply(table,key,state);
 if(Number.isInteger(state.sortIndex))sort(table,key,state,state.sortIndex,state.sortDirection||'asc');
}
function enhanceAll(){scheduled=false;[...document.querySelectorAll('table')].forEach(enhance)}
function schedule(){if(scheduled)return;scheduled=true;requestAnimationFrame(enhanceAll)}
new MutationObserver(schedule).observe(document.body,{childList:true,subtree:true});window.enhanceShapeBenchTables=enhanceAll;schedule();
})();
""";
    private const int RangeWidth = 32;
    private const int PrefixByteLength = 4;
    private const int PrefixGroupSize = 32;
    private const int Scalar8RootPrefixGroupSize = 2_048;
    private const int KeyListWidth = 32;
    private const int PointLookupOperations = 50_000;
    private const int LookupListOperations = 5_000;
    private const int RangeReadOperations = 25_000;
    private const int PrefixReadOperations = 10_000;
    private const int RangeCountOperations = 25_000;
    private const int PrefixCountOperations = 10_000;
    private const int CountAllHotMeasurementMilliseconds = 500;
    private const int CountAllHotClockCheckInterval = 16;
    private const int ChildPollMilliseconds = 1_000;
    private const int DefaultChildTimeoutSeconds = 900;
    private const int DefaultChildNoProgressSeconds = 180;
    private static readonly int[] DefaultBatchSizes = [250, 1000, 5000];
    private static readonly int[] DefaultThreads = [1, 8, 16];
    private static readonly ShapeSpec[] Shapes =
    [
        new("ss8-8", "scalar", 8, 8),
        new("ss16-8", "scalar", 16, 8),
        new("ss8-16", "scalar", 8, 16),
        new("ss16-16", "scalar", 16, 16),
        new("fs32-8", "fixlen", 32, 8),
        new("fs32-16", "fixlen", 32, 16),
        new("sv8", "varlen", 8, -24),
        new("sv16", "varlen", 16, -24),
        new("vs8", "varlen", -24, 8),
        new("vs16", "varlen", -24, 16),
        new("vv", "varlen", -24, -24)
    ];

    private static readonly WorkloadSpec[] Workloads =
    [
        new("insert-sorted-batch", "write", "sorted insert batches", true, false, "batch"),
        new("insert-random-batch", "write", "deterministic random-ish insert batches", true, false, "batch"),
        new("insert-random-direct", "write", "deterministic random-ish public direct inserts; one thread only", true, false, "direct"),
        new("insert-random-concurrent-writer", "write", "deterministic random-ish inserts through public concurrent writer", true, false, "concurrent-writer"),
        new("insert-random-concurrent-batch", "write", "deterministic random-ish inserts through public concurrent batch", true, false, "concurrent-batch"),
        new("insert-concurrent-writer-disjoint", "write", "equal fixed worker partitions over disjoint key ranges through public concurrent writer", true, false, "concurrent-writer", "disjoint"),
        new("insert-concurrent-writer-mixed", "write", "equal fixed worker partitions combining private and interleaved key ranges through public concurrent writer", true, false, "concurrent-writer", "mixed"),
        new("insert-concurrent-writer-overlap", "write", "equal fixed worker partitions interleaved across the same key range through public concurrent writer", true, false, "concurrent-writer", "overlap"),
        new("insert-concurrent-writer-disjoint-single8", "write", "one-thread replay of the 8-worker disjoint concurrent-writer plan", true, false, "concurrent-writer", "disjoint", 8),
        new("insert-concurrent-writer-disjoint-single16", "write", "one-thread replay of the 16-worker disjoint concurrent-writer plan", true, false, "concurrent-writer", "disjoint", 16),
        new("insert-concurrent-writer-mixed-single8", "write", "one-thread replay of the 8-worker mixed concurrent-writer plan", true, false, "concurrent-writer", "mixed", 8),
        new("insert-concurrent-writer-mixed-single16", "write", "one-thread replay of the 16-worker mixed concurrent-writer plan", true, false, "concurrent-writer", "mixed", 16),
        new("insert-concurrent-writer-overlap-single8", "write", "one-thread replay of the 8-worker overlap concurrent-writer plan", true, false, "concurrent-writer", "overlap", 8),
        new("insert-concurrent-writer-overlap-single16", "write", "one-thread replay of the 16-worker overlap concurrent-writer plan", true, false, "concurrent-writer", "overlap", 16),
        new("insert-concurrent-batch-disjoint", "write", "equal fixed worker partitions over disjoint key ranges through public concurrent batches", true, false, "concurrent-batch", "disjoint"),
        new("insert-concurrent-batch-mixed", "write", "equal fixed worker partitions combining private and interleaved key ranges through public concurrent batches", true, false, "concurrent-batch", "mixed"),
        new("insert-concurrent-batch-overlap", "write", "equal fixed worker partitions interleaved across the same key range through public concurrent batches", true, false, "concurrent-batch", "overlap"),
        new("insert-concurrent-batch-disjoint-single8", "write", "one-thread replay of the 8-worker disjoint concurrent-batch plan", true, false, "concurrent-batch", "disjoint", 8),
        new("insert-concurrent-batch-disjoint-single16", "write", "one-thread replay of the 16-worker disjoint concurrent-batch plan", true, false, "concurrent-batch", "disjoint", 16),
        new("insert-concurrent-batch-mixed-single8", "write", "one-thread replay of the 8-worker mixed concurrent-batch plan", true, false, "concurrent-batch", "mixed", 8),
        new("insert-concurrent-batch-mixed-single16", "write", "one-thread replay of the 16-worker mixed concurrent-batch plan", true, false, "concurrent-batch", "mixed", 16),
        new("insert-concurrent-batch-overlap-single8", "write", "one-thread replay of the 8-worker overlap concurrent-batch plan", true, false, "concurrent-batch", "overlap", 8),
        new("insert-concurrent-batch-overlap-single16", "write", "one-thread replay of the 16-worker overlap concurrent-batch plan", true, false, "concurrent-batch", "overlap", 16),
        new("lookup-one-identities", "read", "get identities for one key", false, true),
        new("lookup-list-identities", "read", "get identities for a list of keys", false, true),
        new("range-identities", "read", "get identities between key bounds", false, true),
        new("prefix-identities", "read", "get identities for keys matching a byte prefix", false, true),
        new("range-keys", "read", "get keys between key bounds", false, true),
        new("prefix-keys", "read", "get keys matching a byte prefix", false, true),
        new("range-pairs", "read", "get key/identity pairs between key bounds", false, true),
        new("prefix-pairs", "read", "get key/identity pairs matching a byte prefix", false, true),
        new("count-all-api", "count", "count all entries through the public Count API where the shape exposes one", false, false),
        new("count-range-api", "count", "count range entries through the public Count(condition) API where the shape exposes one", false, false),
        new("count-prefix-api", "count", "count byte-prefix entries through the public Count(condition) API where the shape exposes one", false, false)
    ];

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || Has(args, "--help"))
            {
                PrintHelp();
                return 0;
            }

            return args[0] switch
            {
                "list" => ListScenarios(args),
                "run-one" => RunOne(args),
                "run-all" => RunAll(args),
                "run-reads" => RunReads(args),
                "run-scaling" => RunScaling(args),
                "report" => Report(args),
                "report-reads" => ReportReads(args),
                "diagnose-vs8-exact" => DiagnoseVs8Exact(args),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int ListScenarios(string[] args)
    {
        int items = GetInt(args, "--items", DefaultItems);
        foreach (RunScenario scenario in EnumerateScenarios(args, items))
        {
            Console.WriteLine($"{scenario.Shape.Id},{scenario.Workload.Id},{scenario.BatchSize},{scenario.Threads},{scenario.Items}");
        }

        return 0;
    }

    private static int RunAll(string[] args)
    {
        int requestedThreads = GetInt(args, "--threads", 0);
        if (requestedThreads > 1)
        {
            return Fail("run-all is the LibraDex-versus-SQLite parity campaign and only permits --threads 1. Use run-scaling for LibraDex T1/T8/T16 read scaling.");
        }

        int items = GetInt(args, "--items", DefaultItems);
        string root = Path.GetFullPath(GetString(args, "--root", Path.Combine("artifacts", "shape-bench", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))));
        string csvPath = Path.GetFullPath(GetString(args, "--out", Path.Combine(root, "results.csv")));
        string reportPath = Path.GetFullPath(GetString(args, "--report", Path.Combine(root, "report.html")));
        bool quiet = Has(args, "--quiet");
        bool resume = Has(args, "--resume");
        int progressEvery = Math.Max(1, GetInt(args, "--progress-every", 1));
        int childTimeoutSeconds = Math.Max(30, GetInt(args, "--child-timeout-seconds", DefaultChildTimeoutSeconds));
        int noProgressSeconds = Math.Max(30, GetInt(args, "--child-no-progress-seconds", DefaultChildNoProgressSeconds));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        RunProvenance provenance = CreateRunProvenance(args, root, items);
        if (resume && File.Exists(csvPath)) ValidateResumeCsvSchema(csvPath);
        HashSet<string> completed = resume ? ReadCompletedScenarioKeys(csvPath) : [];
        bool append = resume && File.Exists(csvPath);
        int id = append ? GetNextResultId(csvPath) : 1;
        using (StreamWriter writer = new(csvPath, append, Encoding.UTF8))
        {
            if (!append)
            {
                writer.WriteLine(CsvHeader);
            }

            foreach (RunScenario scenario in EnumerateScenarios(args, items))
            {
                if (scenario.Threads != 1)
                {
                    continue;
                }

                string scenarioKey = GetScenarioKey(
                    scenario.Shape.Id,
                    scenario.Workload.Id,
                    scenario.BatchSize,
                    scenario.Threads,
                    scenario.Items,
                    provenance.DatasetKind,
                    CurrentScenarioSchemaVersion,
                    provenance.BinarySha256);
                if (completed.Contains(scenarioKey))
                {
                    continue;
                }

                RunResult libra = RunEnginePairChild(root, scenario, "libradex", childTimeoutSeconds, noProgressSeconds);
                RunResult sqlite = RunEnginePairChild(root, scenario, "sqlite", childTimeoutSeconds, noProgressSeconds);
                if (libra.ResultItems != sqlite.ResultItems)
                {
                    throw new InvalidDataException(
                        $"Result item mismatch for shape={scenario.Shape.Id} workload={scenario.Workload.Id} batch={scenario.BatchSize} threads={scenario.Threads}: libradex={libra.ResultItems} sqlite={sqlite.ResultItems}. " +
                        "The scenario is not comparable until the LibraDex and SQLite predicates return the same result cardinality.");
                }

                long resultItems = Math.Max(libra.ResultItems, sqlite.ResultItems);
                string notes = JoinNotes(libra.Notes, sqlite.Notes);
                writer.WriteLine(string.Join(",",
                    id.ToString(CultureInfo.InvariantCulture),
                    Csv(scenario.Shape.Id),
                    Csv(scenario.Workload.Id),
                    Csv(scenario.Workload.Kind),
                    scenario.BatchSize.ToString(CultureInfo.InvariantCulture),
                    scenario.Threads.ToString(CultureInfo.InvariantCulture),
                    libra.Operations.ToString(CultureInfo.InvariantCulture),
                    libra.OperationsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    sqlite.OperationsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    resultItems.ToString(CultureInfo.InvariantCulture),
                    libra.ItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    sqlite.ItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    ToMiB(libra.RamBytes).ToString("0.###", CultureInfo.InvariantCulture),
                    ToMiB(sqlite.RamBytes).ToString("0.###", CultureInfo.InvariantCulture),
                    ToMiB(libra.DiskBytes).ToString("0.###", CultureInfo.InvariantCulture),
                    ToMiB(sqlite.DiskBytes).ToString("0.###", CultureInfo.InvariantCulture),
                    Csv(notes),
                    libra.ThreadActiveCount.ToString(CultureInfo.InvariantCulture),
                    sqlite.ThreadActiveCount.ToString(CultureInfo.InvariantCulture),
                    libra.ThreadAverageItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    sqlite.ThreadAverageItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.ThreadMedianItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    sqlite.ThreadMedianItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.ThreadMaximumItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    sqlite.ThreadMaximumItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.ThreadMinimumItems.ToString(CultureInfo.InvariantCulture),
                    sqlite.ThreadMinimumItems.ToString(CultureInfo.InvariantCulture),
                    libra.ThreadMaximumItems.ToString(CultureInfo.InvariantCulture),
                    sqlite.ThreadMaximumItems.ToString(CultureInfo.InvariantCulture),
                    libra.ThreadChunkCount.ToString(CultureInfo.InvariantCulture),
                    sqlite.ThreadChunkCount.ToString(CultureInfo.InvariantCulture),
                    libra.PublicationCount.ToString(CultureInfo.InvariantCulture),
                    sqlite.PublicationCount.ToString(CultureInfo.InvariantCulture),
                    libra.ItemsPerPublication.ToString("0.###", CultureInfo.InvariantCulture),
                    sqlite.ItemsPerPublication.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.ThreadMinimumItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    sqlite.ThreadMinimumItemsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                    Csv(scenario.Workload.ConcurrencyLocality),
                    (scenario.Workload.ConcurrencyPlanWorkers > 0
                        ? scenario.Workload.ConcurrencyPlanWorkers
                        : scenario.Threads).ToString(CultureInfo.InvariantCulture),
                    libra.DistinctShelfCount.ToString(CultureInfo.InvariantCulture),
                    libra.MinimumShelvesPerWorker.ToString(CultureInfo.InvariantCulture),
                    libra.MaximumShelvesPerWorker.ToString(CultureInfo.InvariantCulture),
                    libra.SharedShelfCount.ToString(CultureInfo.InvariantCulture),
                    libra.MaximumWorkersPerShelf.ToString(CultureInfo.InvariantCulture),
                    libra.SharedShelfItemPercent.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.DistinctParentRouterCount.ToString(CultureInfo.InvariantCulture),
                    libra.MinimumParentRoutersPerWorker.ToString(CultureInfo.InvariantCulture),
                    libra.MaximumParentRoutersPerWorker.ToString(CultureInfo.InvariantCulture),
                    libra.SharedParentRouterCount.ToString(CultureInfo.InvariantCulture),
                    libra.MaximumWorkersPerParentRouter.ToString(CultureInfo.InvariantCulture),
                    libra.SharedParentRouterItemPercent.ToString("0.###", CultureInfo.InvariantCulture),
                    scenario.Items.ToString(CultureInfo.InvariantCulture),
                    Csv(provenance.DatasetKind),
                    CurrentScenarioSchemaVersion.ToString(CultureInfo.InvariantCulture),
                    Csv(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                    Csv(provenance.CampaignId),
                    Csv(provenance.BinarySha256),
                    Csv(provenance.MachineName),
                    Csv(provenance.RuntimeVersion),
                    libra.BatchSampleCount.ToString(CultureInfo.InvariantCulture),
                    libra.BatchLatencyP50Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.BatchLatencyP95Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.BatchLatencyP99Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.BatchLatencyMaximumMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.PublishLatencyP50Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.PublishLatencyP95Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.PublishLatencyP99Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.PublishLatencyMaximumMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.ConflictedBatchCount.ToString(CultureInfo.InvariantCulture),
                    libra.ConflictedBatchLatencyP50Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.ConflictedBatchLatencyP95Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.ConflictedBatchLatencyP99Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.UnconflictedBatchLatencyP50Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.UnconflictedBatchLatencyP95Milliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    libra.UnconflictedBatchLatencyP99Milliseconds.ToString("0.###", CultureInfo.InvariantCulture)));
                writer.Flush();
                if (!quiet && (id == 1 || id % progressEvery == 0))
                {
                    Console.WriteLine($"{id}: {scenario.Shape.Id} {scenario.Workload.Id} batch={scenario.BatchSize} threads={scenario.Threads} libraOps={libra.OperationsPerSecond:0.###}/s sqliteOps={sqlite.OperationsPerSecond:0.###}/s");
                }

                id++;
            }
        }

        DeleteDirectory(Path.Combine(root, "work"));
        WriteHtmlReport(csvPath, reportPath);
        Console.WriteLine(csvPath);
        Console.WriteLine(reportPath);
        return 0;
    }

    /// <summary>
    /// Runs process-isolated LibraDex-only read measurements at T1, T8, and T16 and reports every aggregate against its own T1 baseline.<br/>
    /// Keeping this campaign separate prevents caller parallelism from being presented as a cross-engine SQLite ratio while retaining the exact same deterministic corpus and child runner.<br/>
    /// </summary>
    /// <param name="args">Shape, workload, batch, corpus, repetition, timeout, output, and provenance options.<br/></param>
    /// <returns>Zero when every requested cohort completes with stable result cardinality; otherwise the command throws or returns a validation failure.<br/></returns>
    private static int RunScaling(string[] args)
    {
        if (Has(args, "--threads"))
        {
            return Fail("run-scaling owns the T1/T8/T16 thread set; omit --threads.");
        }

        int items = GetInt(args, "--items", DefaultItems);
        int repetitions = Math.Max(1, GetInt(args, "--repetitions", 3));
        int childTimeoutSeconds = Math.Max(30, GetInt(args, "--child-timeout-seconds", DefaultChildTimeoutSeconds));
        int noProgressSeconds = Math.Max(30, GetInt(args, "--child-no-progress-seconds", DefaultChildNoProgressSeconds));
        string root = Path.GetFullPath(GetString(args, "--root", Path.Combine("artifacts", "shape-bench", "scaling-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))));
        string csvPath = Path.GetFullPath(GetString(args, "--out", Path.Combine(root, "results.csv")));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        RunProvenance provenance = CreateRunProvenance(args, root, items);
        int[] threadCounts = [1, 8, 16];
        using StreamWriter writer = new(csvPath, append: false, Encoding.UTF8);
        writer.WriteLine(ScalingCsvHeader);

        foreach (RunScenario baseScenario in EnumerateScenarios(args, items))
        {
            if (baseScenario.Threads != 1 || baseScenario.Workload.IsWrite)
            {
                continue;
            }

            double[][] ratesByThread = new double[threadCounts.Length][];
            long expectedResultItems = -1;
            for (int threadIndex = 0; threadIndex < threadCounts.Length; threadIndex++)
            {
                int threads = threadCounts[threadIndex];
                RunScenario scenario = baseScenario with { Threads = threads };
                double[] rates = new double[repetitions];
                for (int repetition = 0; repetition < repetitions; repetition++)
                {
                    RunResult result = RunChild(root, scenario, "libradex", $"scaling-r{repetition + 1}", childTimeoutSeconds, noProgressSeconds);
                    if (expectedResultItems < 0)
                    {
                        expectedResultItems = result.ResultItems;
                    }
                    else if (result.ResultItems != expectedResultItems)
                    {
                        throw new InvalidDataException($"Scaling result cardinality changed for {scenario.Shape.Id} {scenario.Workload.Id} b{scenario.BatchSize}: expected={expectedResultItems}, actual={result.ResultItems}, threads={threads}, repetition={repetition + 1}.");
                    }

                    rates[repetition] = result.OperationsPerSecond;
                }

                Array.Sort(rates);
                ratesByThread[threadIndex] = rates;
            }

            double t1Median = MedianSorted(ratesByThread[0]);
            for (int threadIndex = 0; threadIndex < threadCounts.Length; threadIndex++)
            {
                double[] rates = ratesByThread[threadIndex];
                int threads = threadCounts[threadIndex];
                double median = MedianSorted(rates);
                double aggregateOverT1 = t1Median == 0D ? 0D : median / t1Median;
                double spreadPercent = median == 0D ? 0D : (rates[^1] - rates[0]) * 100D / median;
                double parallelEfficiencyPercent = aggregateOverT1 * 100D / threads;
                writer.WriteLine(string.Join(",",
                    Csv(baseScenario.Shape.Id),
                    Csv(baseScenario.Workload.Id),
                    baseScenario.BatchSize.ToString(CultureInfo.InvariantCulture),
                    threads.ToString(CultureInfo.InvariantCulture),
                    items.ToString(CultureInfo.InvariantCulture),
                    repetitions.ToString(CultureInfo.InvariantCulture),
                    median.ToString("0.###", CultureInfo.InvariantCulture),
                    rates[0].ToString("0.###", CultureInfo.InvariantCulture),
                    rates[^1].ToString("0.###", CultureInfo.InvariantCulture),
                    spreadPercent.ToString("0.###", CultureInfo.InvariantCulture),
                    expectedResultItems.ToString(CultureInfo.InvariantCulture),
                    t1Median.ToString("0.###", CultureInfo.InvariantCulture),
                    aggregateOverT1.ToString("0.###", CultureInfo.InvariantCulture),
                    parallelEfficiencyPercent.ToString("0.###", CultureInfo.InvariantCulture),
                    Csv(provenance.CampaignId),
                    Csv(provenance.BinarySha256),
                    Csv(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                    Csv("LibraDex-only caller scaling; no SQLite ratio")));
                writer.Flush();
                Console.WriteLine($"{baseScenario.Shape.Id} {baseScenario.Workload.Id} b{baseScenario.BatchSize} T{threads}: {median:0.###} ops/s, {aggregateOverT1:0.###}x T1");
            }
        }

        DeleteDirectory(Path.Combine(root, "work"));
        Console.WriteLine(csvPath);
        return 0;
    }

    /// <summary>
    /// Runs the canonical single-thread read matrix through the existing LibraDex and SQLite child kernels.<br/>
    /// Every cell is repeated in fresh processes, parity-checked per repetition, aggregated into one CSV row, and published immediately to the incremental HTML report.<br/>
    /// </summary>
    /// <param name="args">Optional shape/workload/batch filters plus corpus, repetition, timeout, resume, output, and provenance settings.<br/></param>
    /// <returns>Zero after every supported read cell completes with exact result parity.<br/></returns>
    private static int RunReads(string[] args)
    {
        if (Has(args, "--threads"))
        {
            return Fail("run-reads owns the single-thread comparison contract; omit --threads.");
        }

        int items = GetInt(args, "--items", DefaultItems);
        int repetitions = Math.Max(1, GetInt(args, "--repetitions", 3));
        int childTimeoutSeconds = Math.Max(30, GetInt(args, "--child-timeout-seconds", DefaultChildTimeoutSeconds));
        int noProgressSeconds = Math.Max(30, GetInt(args, "--child-no-progress-seconds", DefaultChildNoProgressSeconds));
        string root = Path.GetFullPath(GetString(args, "--root", Path.Combine("artifacts", "shape-bench", "reads-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))));
        string csvPath = Path.GetFullPath(GetString(args, "--out", Path.Combine(root, "results.csv")));
        string reportPath = Path.GetFullPath(GetString(args, "--report", Path.Combine(root, "report.html")));
        bool resume = Has(args, "--resume");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        RunProvenance provenance = CreateRunProvenance(args, root, items);
        RunScenario[] scenarios = EnumerateScenarios(args, items)
            .Where(static scenario => scenario.Threads == 1 && !scenario.Workload.IsWrite)
            .ToArray();
        if (scenarios.Length == 0)
        {
            return Fail("run-reads found no supported read scenarios for the requested filters.");
        }

        if (resume && File.Exists(csvPath))
        {
            ValidateReadResumeCsvSchema(csvPath);
        }

        HashSet<string> completed = resume && File.Exists(csvPath)
            ? ReadCompletedReadScenarioKeys(csvPath)
            : [];
        bool append = resume && File.Exists(csvPath);
        int id = append ? GetNextResultId(csvPath) : 1;
        using FileStream csvStream = new(
            csvPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        using StreamWriter writer = new(csvStream, Encoding.UTF8);
        if (!append)
        {
            writer.WriteLine(ReadCsvHeader);
            writer.Flush();
        }

        WriteReadHtmlReport(csvPath, reportPath, scenarios.Length, repetitions);
        foreach (RunScenario scenario in scenarios)
        {
            string scenarioKey = GetReadScenarioKey(
                scenario.Shape.Id,
                scenario.Workload.Id,
                scenario.BatchSize,
                scenario.Items,
                repetitions,
                provenance.BinarySha256);
            if (completed.Contains(scenarioKey))
            {
                continue;
            }

            RunResult libra = RunChild(root, scenario, "libradex", "read-warm-samples", childTimeoutSeconds, noProgressSeconds, repetitions);
            RunResult sqlite = RunChild(root, scenario, "sqlite", "read-warm-samples", childTimeoutSeconds, noProgressSeconds, repetitions);
            double[] libraRates = RequireWarmDoubleSamples(libra.WarmOperationsPerSecondSamples, repetitions, "LibraDex operations/sec", scenario);
            double[] sqliteRates = RequireWarmDoubleSamples(sqlite.WarmOperationsPerSecondSamples, repetitions, "SQLite operations/sec", scenario);
            long[] libraOperationCounts = RequireWarmLongSamples(libra.WarmOperationSamples, repetitions, "LibraDex operations", scenario);
            long[] sqliteOperationCounts = RequireWarmLongSamples(sqlite.WarmOperationSamples, repetitions, "SQLite operations", scenario);
            double[] libraFirstCountLatencies = RequireWarmDoubleSamples(libra.WarmFirstCountLatencyMillisecondsSamples, repetitions, "LibraDex first-count latency", scenario);
            double[] sqliteFirstCountLatencies = RequireWarmDoubleSamples(sqlite.WarmFirstCountLatencyMillisecondsSamples, repetitions, "SQLite first-count latency", scenario);
            if (libra.ResultItems != sqlite.ResultItems || libra.ColdResultItems != sqlite.ColdResultItems)
            {
                throw new InvalidDataException(
                    $"Read parity mismatch for {scenario.Shape.Id} {scenario.Workload.Id} b{scenario.BatchSize}: " +
                    $"warm LibraDex={libra.ResultItems}, warm SQLite={sqlite.ResultItems}, cold LibraDex={libra.ColdResultItems}, cold SQLite={sqlite.ColdResultItems}.");
            }

            long expectedResultItems = libra.ResultItems;

            Array.Sort(libraRates);
            Array.Sort(sqliteRates);
            Array.Sort(libraFirstCountLatencies);
            Array.Sort(sqliteFirstCountLatencies);
            Array.Sort(libraOperationCounts);
            Array.Sort(sqliteOperationCounts);
            double libraMean = Mean(libraRates);
            double sqliteMean = Mean(sqliteRates);
            double libraMedian = MedianSorted(libraRates);
            double sqliteMedian = MedianSorted(sqliteRates);
            double libraSpread = CalculateSpreadPercent(libraRates, libraMedian);
            double sqliteSpread = CalculateSpreadPercent(sqliteRates, sqliteMedian);
            double ratio = sqliteMean == 0D ? 0D : libraMean / sqliteMean;
            writer.WriteLine(string.Join(",",
                id.ToString(CultureInfo.InvariantCulture),
                Csv(scenario.Shape.Id),
                Csv(scenario.Workload.Id),
                Csv(scenario.Workload.Kind),
                scenario.BatchSize.ToString(CultureInfo.InvariantCulture),
                "1",
                scenario.Items.ToString(CultureInfo.InvariantCulture),
                repetitions.ToString(CultureInfo.InvariantCulture),
                Mean(libraOperationCounts).ToString("0.###", CultureInfo.InvariantCulture),
                Mean(sqliteOperationCounts).ToString("0.###", CultureInfo.InvariantCulture),
                expectedResultItems.ToString(CultureInfo.InvariantCulture),
                libraMean.ToString("0.###", CultureInfo.InvariantCulture),
                libraMedian.ToString("0.###", CultureInfo.InvariantCulture),
                libraRates[0].ToString("0.###", CultureInfo.InvariantCulture),
                libraRates[^1].ToString("0.###", CultureInfo.InvariantCulture),
                libraSpread.ToString("0.###", CultureInfo.InvariantCulture),
                libra.ColdOperationsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                sqliteMean.ToString("0.###", CultureInfo.InvariantCulture),
                sqliteMedian.ToString("0.###", CultureInfo.InvariantCulture),
                sqliteRates[0].ToString("0.###", CultureInfo.InvariantCulture),
                sqliteRates[^1].ToString("0.###", CultureInfo.InvariantCulture),
                sqliteSpread.ToString("0.###", CultureInfo.InvariantCulture),
                sqlite.ColdOperationsPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
                Mean(libraFirstCountLatencies).ToString("0.###", CultureInfo.InvariantCulture),
                Mean(sqliteFirstCountLatencies).ToString("0.###", CultureInfo.InvariantCulture),
                ratio.ToString("0.###", CultureInfo.InvariantCulture),
                Csv(provenance.CampaignId),
                Csv(provenance.BinarySha256),
                Csv(provenance.MachineName),
                Csv(provenance.RuntimeVersion),
                Csv(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                Csv("T1 covering-index parity; one process per engine; one discarded cold pass; warm mean from same-session passes with fresh per-pass execution objects")));
            writer.Flush();
            completed.Add(scenarioKey);
            WriteReadHtmlReport(csvPath, reportPath, scenarios.Length, repetitions);
            Console.WriteLine($"{id}/{scenarios.Length}: {scenario.Shape.Id} {scenario.Workload.Id} b{scenario.BatchSize} LibraDex warm mean={libraMean:0.###}/s SQLite warm mean={sqliteMean:0.###}/s ratio={ratio:0.###}x");
            id++;
        }

        DeleteDirectory(Path.Combine(root, "work"));
        WriteReadHtmlReport(csvPath, reportPath, scenarios.Length, repetitions);
        Console.WriteLine(csvPath);
        Console.WriteLine(reportPath);
        return 0;
    }

    /// <summary>
    /// Returns the median of one non-empty ascending sample array without allocating another projection.<br/>
    /// Odd sample counts select the center value; even counts average the two center values.<br/>
    /// </summary>
    /// <param name="sortedValues">The non-empty ascending sample array.<br/></param>
    /// <returns>The arithmetic median.<br/></returns>
    private static double MedianSorted(double[] sortedValues)
    {
        if (sortedValues.Length == 0)
        {
            throw new ArgumentException("A scaling median requires at least one sample.", nameof(sortedValues));
        }

        int middle = sortedValues.Length >> 1;
        return (sortedValues.Length & 1) != 0
            ? sortedValues[middle]
            : (sortedValues[middle - 1] + sortedValues[middle]) * 0.5D;
    }

    /// <summary>
    /// Calculates the arithmetic mean of one non-empty warm rate or latency sample set.<br/>
    /// The warm-read comparison uses this value as its primary statistic while retaining median and observed bounds for stability review.<br/>
    /// </summary>
    /// <param name="values">The populated sample values.<br/></param>
    /// <returns>The arithmetic mean.<br/></returns>
    private static double Mean(double[] values)
    {
        if (values.Length == 0) throw new ArgumentException("A warm mean requires at least one sample.", nameof(values));
        double total = 0D;
        for (int i = 0; i < values.Length; i++) total += values[i];
        return total / values.Length;
    }

    /// <summary>
    /// Calculates the arithmetic mean of one non-empty warm operation-count sample set.<br/>
    /// Operation counts should normally be identical, but preserving their mean keeps the aggregate contract explicit.<br/>
    /// </summary>
    /// <param name="values">The populated operation counts.<br/></param>
    /// <returns>The arithmetic mean.<br/></returns>
    private static double Mean(long[] values)
    {
        if (values.Length == 0) throw new ArgumentException("A warm mean requires at least one sample.", nameof(values));
        double total = 0D;
        for (int i = 0; i < values.Length; i++) total += values[i];
        return total / values.Length;
    }

    /// <summary>
    /// Requires the exact number of same-process warm floating-point samples returned by one engine child.<br/>
    /// Returning a defensive copy lets report aggregation sort samples without mutating the deserialized result object.<br/>
    /// </summary>
    /// <param name="samples">The child-returned warm samples.<br/></param>
    /// <param name="expectedCount">The required warm-pass count.<br/></param>
    /// <param name="label">The diagnostic sample label.<br/></param>
    /// <param name="scenario">The scenario that owns the samples.<br/></param>
    /// <returns>An independent sample array with the required length.<br/></returns>
    private static double[] RequireWarmDoubleSamples(double[]? samples, int expectedCount, string label, RunScenario scenario)
    {
        if (samples is null || samples.Length != expectedCount)
        {
            throw new InvalidDataException(
                $"{label} returned {samples?.Length ?? 0} warm samples for {scenario.Shape.Id} {scenario.Workload.Id} b{scenario.BatchSize}; expected {expectedCount}.");
        }

        return (double[])samples.Clone();
    }

    /// <summary>
    /// Requires the exact number of same-process warm integer samples returned by one engine child.<br/>
    /// Returning a defensive copy keeps child provenance immutable during parent aggregation.<br/>
    /// </summary>
    /// <param name="samples">The child-returned warm samples.<br/></param>
    /// <param name="expectedCount">The required warm-pass count.<br/></param>
    /// <param name="label">The diagnostic sample label.<br/></param>
    /// <param name="scenario">The scenario that owns the samples.<br/></param>
    /// <returns>An independent sample array with the required length.<br/></returns>
    private static long[] RequireWarmLongSamples(long[]? samples, int expectedCount, string label, RunScenario scenario)
    {
        if (samples is null || samples.Length != expectedCount)
        {
            throw new InvalidDataException(
                $"{label} returned {samples?.Length ?? 0} warm samples for {scenario.Shape.Id} {scenario.Workload.Id} b{scenario.BatchSize}; expected {expectedCount}.");
        }

        return (long[])samples.Clone();
    }

    /// <summary>
    /// Calculates full observed sample width as a percentage of the sample median.<br/>
    /// The input remains sorted by the caller so minimum and maximum lookup require no additional scan.<br/>
    /// </summary>
    /// <param name="sortedValues">The non-empty ascending sample array.<br/></param>
    /// <param name="median">The already calculated sample median.<br/></param>
    /// <returns>The minimum-to-maximum width as a percentage of the median, or zero when the median is zero.<br/></returns>
    private static double CalculateSpreadPercent(double[] sortedValues, double median)
    {
        return median == 0D ? 0D : (sortedValues[^1] - sortedValues[0]) * 100D / median;
    }

    private static int Report(string[] args)
    {
        string csvPath = Path.GetFullPath(GetString(args, "--in", GetString(args, "--out", "")));
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            return Fail("report requires --in <results.csv>.");
        }

        string reportPath = Path.GetFullPath(GetString(args, "--report", Path.Combine(Path.GetDirectoryName(csvPath)!, "report.html")));
        WriteHtmlReport(csvPath, reportPath);
        Console.WriteLine(reportPath);
        return 0;
    }

    /// <summary>
    /// Regenerates the canonical read-campaign HTML from an existing aggregated read CSV.<br/>
    /// This supports focused row replacement without rerunning unaffected shapes or misrepresenting the campaign's expected-cell denominator.<br/>
    /// </summary>
    /// <param name="args">The read CSV, report path, corpus size, expected-cell override, and repetition count.<br/></param>
    /// <returns>Zero after the read HTML has been refreshed.<br/></returns>
    private static int ReportReads(string[] args)
    {
        string csvPath = Path.GetFullPath(GetString(args, "--in", GetString(args, "--out", "")));
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            return Fail("report-reads requires --in <results.csv>.");
        }

        int items = GetInt(args, "--items", DefaultItems);
        int repetitions = Math.Max(1, GetInt(args, "--repetitions", 3));
        int expectedCells = GetInt(args, "--expected-cells", 0);
        if (expectedCells <= 0)
        {
            expectedCells = EnumerateScenarios(args, items).Count(static scenario => scenario.Threads == 1 && !scenario.Workload.IsWrite);
        }

        string reportPath = Path.GetFullPath(GetString(args, "--report", Path.Combine(Path.GetDirectoryName(csvPath)!, "report.html")));
        WriteReadHtmlReport(csvPath, reportPath, expectedCells, repetitions);
        Console.WriteLine(reportPath);
        return 0;
    }

    private static RunResult RunEnginePairChild(string root, RunScenario scenario, string engine, int timeoutSeconds, int noProgressSeconds)
    {
        _ = RunChild(root, scenario, engine, "warmup", timeoutSeconds, noProgressSeconds);
        return RunChild(root, scenario, engine, "measured", timeoutSeconds, noProgressSeconds);
    }

    private static RunResult RunChild(
        string root,
        RunScenario scenario,
        string engine,
        string pass,
        int timeoutSeconds,
        int noProgressSeconds,
        int warmRepetitions = 0)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current executable path.");
        string workRoot = Path.Combine(root, "work", scenario.Shape.Id, scenario.Workload.Id, $"b{scenario.BatchSize}", $"t{scenario.Threads}", engine, pass);
        DeleteDirectory(workRoot);
        Directory.CreateDirectory(workRoot);
        string stdoutPath = Path.Combine(workRoot, "child.stdout.log");
        string stderrPath = Path.Combine(workRoot, "child.stderr.log");
        ProcessStartInfo psi = new()
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory
        };
        psi.ArgumentList.Add("run-one");
        psi.ArgumentList.Add("--engine");
        psi.ArgumentList.Add(engine);
        psi.ArgumentList.Add("--shape");
        psi.ArgumentList.Add(scenario.Shape.Id);
        psi.ArgumentList.Add("--workload");
        psi.ArgumentList.Add(scenario.Workload.Id);
        psi.ArgumentList.Add("--batch");
        psi.ArgumentList.Add(scenario.BatchSize.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--threads");
        psi.ArgumentList.Add(scenario.Threads.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--items");
        psi.ArgumentList.Add(scenario.Items.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(workRoot);
        psi.ArgumentList.Add("--pass");
        psi.ArgumentList.Add(pass);
        if (warmRepetitions > 0)
        {
            psi.ArgumentList.Add("--warm-repetitions");
            psi.ArgumentList.Add(warmRepetitions.ToString(CultureInfo.InvariantCulture));
        }
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start benchmark child process.");
        using StreamReader stdoutReader = process.StandardOutput;
        using StreamReader stderrReader = process.StandardError;
        Stopwatch wall = Stopwatch.StartNew();
        DirectoryActivity lastActivity = default;
        DateTime lastProgressUtc = DateTime.UtcNow;
        while (!process.WaitForExit(ChildPollMilliseconds))
        {
            DirectoryActivity activity = GetDirectoryActivity(workRoot);
            if (activity != lastActivity)
            {
                lastActivity = activity;
                lastProgressUtc = DateTime.UtcNow;
            }

            if (wall.Elapsed.TotalSeconds > timeoutSeconds)
            {
                KillChild(process);
                string guardStdout = stdoutReader.ReadToEnd();
                string guardStderr = stderrReader.ReadToEnd();
                File.WriteAllText(stdoutPath, guardStdout, Encoding.UTF8);
                File.WriteAllText(stderrPath, guardStderr, Encoding.UTF8);
                throw new TimeoutException(BuildChildGuardMessage("timeout", scenario, engine, pass, process.Id, workRoot, guardStdout, guardStderr, wall.Elapsed, timeoutSeconds, noProgressSeconds));
            }

            if (scenario.Workload.IsWrite && (DateTime.UtcNow - lastProgressUtc).TotalSeconds > noProgressSeconds)
            {
                KillChild(process);
                string guardStdout = stdoutReader.ReadToEnd();
                string guardStderr = stderrReader.ReadToEnd();
                File.WriteAllText(stdoutPath, guardStdout, Encoding.UTF8);
                File.WriteAllText(stderrPath, guardStderr, Encoding.UTF8);
                throw new TimeoutException(BuildChildGuardMessage("no-progress", scenario, engine, pass, process.Id, workRoot, guardStdout, guardStderr, wall.Elapsed, timeoutSeconds, noProgressSeconds));
            }
        }

        string stdout = stdoutReader.ReadToEnd();
        string stderr = stderrReader.ReadToEnd();
        File.WriteAllText(stdoutPath, stdout, Encoding.UTF8);
        File.WriteAllText(stderrPath, stderr, Encoding.UTF8);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Child failed: {engine} {scenario.Shape.Id} {scenario.Workload.Id} pass={pass}{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}");
        }

        string json = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Last();
        RunResult? result = JsonSerializer.Deserialize<RunResult>(json);
        if (result is null) throw new InvalidOperationException("Child did not return a result.");
        DeleteDirectory(workRoot);
        return result;
    }

    private static void KillChild(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(5_000);
        }
        catch
        {
        }
    }

    private static string BuildChildGuardMessage(
        string guard,
        RunScenario scenario,
        string engine,
        string pass,
        int pid,
        string workRoot,
        string stdout,
        string stderr,
        TimeSpan elapsed,
        int timeoutSeconds,
        int noProgressSeconds)
    {
        return $"Child guard tripped: {guard} pid={pid} engine={engine} shape={scenario.Shape.Id} workload={scenario.Workload.Id} batch={scenario.BatchSize} threads={scenario.Threads} pass={pass} elapsed={elapsed.TotalSeconds:0.###}s timeout={timeoutSeconds}s noProgress={noProgressSeconds}s workRoot={workRoot}{Environment.NewLine}stdout tail:{Environment.NewLine}{TailText(stdout, 4000)}{Environment.NewLine}stderr tail:{Environment.NewLine}{TailText(stderr, 4000)}";
    }

    private static string TailText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[^maxChars..];
    }

    private static int RunOne(string[] args)
    {
        string engine = GetString(args, "--engine", "");
        ShapeSpec shape = FindShape(GetString(args, "--shape", ""));
        WorkloadSpec workload = FindWorkload(GetString(args, "--workload", GetString(args, "--test", "")));
        int batchSize = GetInt(args, "--batch", 250);
        int threads = GetInt(args, "--threads", 1);
        int items = GetInt(args, "--items", DefaultItems);
        long sv16ReadCacheMaxBytes = GetLong(args, "--sv16-read-cache-max-bytes", 0);
        if (sv16ReadCacheMaxBytes < 0)
        {
            return Fail("--sv16-read-cache-max-bytes must be zero or positive.");
        }

        string attributionText = GetString(args, "--fs32-attribution", "false");
        bool fs32Attribution = string.Equals(attributionText, "true", StringComparison.OrdinalIgnoreCase) || attributionText == "1";
        bool sessionWarmup = Has(args, "--session-warmup");
        int warmRepetitions = Math.Max(0, GetInt(args, "--warm-repetitions", 0));
        if (warmRepetitions > 0 && workload.IsWrite)
        {
            return Fail("--warm-repetitions is available only for read/count workloads.");
        }

        string root = Path.GetFullPath(GetString(args, "--root", Path.Combine("artifacts", "shape-bench", "one")));
        string pass = GetString(args, "--pass", "measured");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, engine == "sqlite" ? "index.sqlite" : "index.lbdx");
        DeleteFile(path);
        if (fs32Attribution)
            Fixed32BatchAttributionDiagnostics.Reset();

        RunResult result;
        try
        {
            result = engine switch
            {
                "libradex" => RunLibraDex(shape, workload, batchSize, threads, items, path, sessionWarmup, sv16ReadCacheMaxBytes, warmRepetitions),
                "sqlite" => RunSqlite(shape, workload, batchSize, threads, items, path, warmRepetitions),
                _ => throw new ArgumentException("Engine must be libradex or sqlite.")
            };
            if (engine == "libradex" &&
                shape.Id == "ss8-8" &&
                workload.IsWrite &&
                workload.ConcurrencyLocality.Length != 0)
            {
                int[][] validationOrders = BuildWriteOrders(workload, items, threads, batchSize);
                result = result with { Notes = JoinNotes(result.Notes, ValidateScalar8Scalar8ExactFinalState(path, validationOrders)) };
            }

            if (fs32Attribution)
                result = result with { Notes = JoinNotes(result.Notes, Fixed32BatchAttributionDiagnostics.CreateSummary()) };
        }
        finally
        {
            if (fs32Attribution)
                Fixed32BatchAttributionDiagnostics.Disable();
        }
        result = result with
        {
            Engine = engine,
            Shape = shape.Id,
            Workload = workload.Id,
            WorkloadKind = workload.Kind,
            BatchSize = batchSize,
            Threads = threads,
            ConcurrencyLocality = workload.ConcurrencyLocality,
            ConcurrencyPlanWorkers = workload.ConcurrencyPlanWorkers > 0 ? workload.ConcurrencyPlanWorkers : threads,
            Pass = pass,
            DiskBytes = GetDirectoryBytes(root),
            RamBytes = Process.GetCurrentProcess().PrivateMemorySize64
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }

    private static RunResult RunLibraDex(
        ShapeSpec shape,
        WorkloadSpec workload,
        int batchSize,
        int threads,
        int items,
        string path,
        bool sessionWarmup = false,
        long sv16ReadCacheMaxBytes = 0,
        int warmRepetitions = 0)
    {
        if (workload.IsWrite)
        {
            int[][] orders = BuildWriteOrders(workload, items, threads, batchSize);
            using IDisposable index = CreateLibraIndex(shape, path, sv16ReadCacheMaxBytes);
            return Measure(() =>
            {
                if (workload.WritePath == "concurrent-batch")
                {
                    WriteMeasurement measured = WriteLibraConcurrentBatch(index, shape, batchSize, orders);
                    PhysicalLocalityMetrics locality = MeasurePhysicalLocality(index, shape, orders);
                    return new MeasureResult(measured.Inserted, measured.Inserted, measured.Notes, measured.Workers, measured.ElapsedTimestampTicks, locality, measured.BatchMetrics);
                }

                if (workload.WritePath == "concurrent-writer")
                {
                    WriteMeasurement measured = WriteLibraConcurrentWriter(index, shape, batchSize, orders);
                    PhysicalLocalityMetrics locality = MeasurePhysicalLocality(index, shape, orders);
                    return new MeasureResult(measured.Inserted, measured.Inserted, measured.Notes, measured.Workers, measured.ElapsedTimestampTicks, locality);
                }

                long inserted = workload.WritePath switch
                {
                    "batch" => WriteLibraSingle(index, shape, batchSize, orders[0]),
                    "direct" => WriteLibraConcurrent(index, shape, batchSize, threads, orders[0]),
                    _ => throw new NotSupportedException($"Unsupported LibraDex write path '{workload.WritePath}'.")
                };
                return new MeasureResult(inserted, inserted);
            }, NoteForLibraWrite(workload, threads));
        }

        SeedLibra(shape, batchSize, items, path, sv16ReadCacheMaxBytes);
        using IDisposable readIndex = OpenLibraIndex(shape, path, sv16ReadCacheMaxBytes);
        if (warmRepetitions > 0)
        {
            return RunWarmReadPasses(
                () => Measure(() => ReadLibra(readIndex, shape, workload, threads, items), NoteForRead(workload)),
                warmRepetitions,
                "LibraDex");
        }

        if (sessionWarmup)
        {
            _ = ReadLibra(readIndex, shape, workload, threads, items);
        }

        RunResult result = Measure(() =>
        {
            return ReadLibra(readIndex, shape, workload, threads, items);
        }, NoteForRead(workload));
        if (sessionWarmup)
        {
            result = result with { Notes = JoinNotes(result.Notes, "LibraDex used one untimed same-session read pass before measurement.") };
        }
        if (shape.Id == "sv16")
        {
            (int entryCount, long cachedBytes, long maxCachedBytes) =
                ((Scalar16VarIdentityIndex)readIndex).Session.GetScalar16VarIdentityReadCacheStatsForValidation(
                    ((Scalar16VarIdentityIndex)readIndex).RootRouterOffset);
            string cacheLimit = maxCachedBytes == 0
                ? "unlimited"
                : FormattableString.Invariant($"{maxCachedBytes / 1048576D:F3} MiB");
            result = result with
            {
                Notes = JoinNotes(
                    result.Notes,
                    FormattableString.Invariant($"SV16 session read cache retained {entryCount} shelves and {cachedBytes / 1048576D:F3} MiB after timing; per-index limit={cacheLimit}."))
            };
        }

        return result;
    }

    private static RunResult RunSqlite(ShapeSpec shape, WorkloadSpec workload, int batchSize, int threads, int items, string path, int warmRepetitions = 0)
    {
        if (workload.IsWrite)
        {
            int[][] orders = BuildWriteOrders(workload, items, threads, batchSize);
            using SqliteConnection cn = OpenSqlite(path);
            CreateSqliteSchema(cn, shape);
            if (threads == 1)
            {
                return Measure(() =>
                {
                    long started = Stopwatch.GetTimestamp();
                    long inserted = WriteSqliteSingle(cn, shape, batchSize, orders[0]);
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    long chunks = GetChunkCount(inserted, batchSize);
                    ThreadWorkerMeasurement[] workers =
                    [
                        new ThreadWorkerMeasurement(inserted, chunks, chunks, elapsed)
                    ];
                    return new MeasureResult(inserted, inserted, "", workers, elapsed);
                }, NoteForSqliteWrite(workload, threads));
            }

            using ConnectionSet workerConnections = OpenSqliteConnections(path, threads);
            return Measure(() =>
            {
                WriteMeasurement measured = WriteSqliteThreaded(workerConnections.Connections, shape, batchSize, orders);
                return new MeasureResult(measured.Inserted, measured.Inserted, "", measured.Workers, measured.ElapsedTimestampTicks);
            }, NoteForSqliteWrite(workload, threads));
        }

        SeedSqlite(shape, batchSize, items, path);
        using ConnectionSet readConnections = OpenSqliteConnections(path, Math.Max(1, threads));
        if (warmRepetitions > 0)
        {
            return RunWarmReadPasses(
                () => Measure(() => ReadSqlite(readConnections.Connections, shape, workload, threads, items), NoteForRead(workload)),
                warmRepetitions,
                "SQLite");
        }

        return Measure(() =>
        {
            return ReadSqlite(readConnections.Connections, shape, workload, threads, items);
        }, NoteForRead(workload));
    }

    /// <summary>
    /// Runs one discarded cold read pass followed by repeated measured warm passes in the same open engine session.<br/>
    /// The supplied pass factory creates and disposes transient reader or command state on every invocation while the caller retains the catalog/session or SQLite connection.<br/>
    /// Exact operation and result cardinality must remain unchanged across cold and warm passes before any samples are returned.<br/>
    /// </summary>
    /// <param name="runPass">The complete workload pass executed against the already-open measurement session.<br/></param>
    /// <param name="warmRepetitions">The number of measured same-session warm passes.<br/></param>
    /// <param name="engineLabel">The engine label used in contract failures and notes.<br/></param>
    /// <returns>One aggregate result carrying the discarded cold diagnostic and every measured warm sample.<br/></returns>
    private static RunResult RunWarmReadPasses(Func<RunResult> runPass, int warmRepetitions, string engineLabel)
    {
        if (warmRepetitions <= 0) throw new ArgumentOutOfRangeException(nameof(warmRepetitions));
        RunResult cold = runPass();
        if (cold.Operations <= 0) throw new InvalidDataException($"{engineLabel} cold read pass returned no measured operations.");

        double[] rates = new double[warmRepetitions];
        double[] itemRates = new double[warmRepetitions];
        double[] firstCountLatencies = new double[warmRepetitions];
        long[] operations = new long[warmRepetitions];
        RunResult? firstWarm = null;
        for (int repetition = 0; repetition < warmRepetitions; repetition++)
        {
            RunResult warm = runPass();
            if (warm.Operations <= 0 || warm.ResultItems != cold.ResultItems)
            {
                throw new InvalidDataException(
                    $"{engineLabel} warm read contract changed at repetition {repetition + 1}: " +
                    $"cold operations/results={cold.Operations}/{cold.ResultItems}, warm={warm.Operations}/{warm.ResultItems}.");
            }

            firstWarm ??= warm;
            rates[repetition] = warm.OperationsPerSecond;
            itemRates[repetition] = warm.ItemsPerSecond;
            firstCountLatencies[repetition] = warm.Notes.Contains("count-first-ms=", StringComparison.Ordinal)
                ? ReadNoteMetric(warm.Notes, "count-first-ms")
                : 0D;
            operations[repetition] = warm.Operations;
        }

        RunResult aggregate = firstWarm!;
        return aggregate with
        {
            Operations = (long)Math.Round(Mean(operations), MidpointRounding.AwayFromZero),
            OperationsPerSecond = Mean(rates),
            ItemsPerSecond = Mean(itemRates),
            ColdOperationsPerSecond = cold.OperationsPerSecond,
            ColdResultItems = cold.ResultItems,
            WarmOperationsPerSecondSamples = rates,
            WarmOperationSamples = operations,
            WarmFirstCountLatencyMillisecondsSamples = firstCountLatencies,
            Notes = JoinNotes(
                aggregate.Notes,
                $"{engineLabel} used one discarded cold pass and {warmRepetitions} measured warm passes in one open session with fresh per-pass execution state.")
        };
    }

    private static bool IsRandomInsert(WorkloadSpec workload)
    {
        return workload.Id.Contains("random", StringComparison.OrdinalIgnoreCase) ||
            workload.ConcurrencyLocality.Length != 0;
    }

    private static string NoteForLibraWrite(WorkloadSpec workload, int threads)
    {
        if (workload.WritePath == "concurrent-writer")
        {
            if (workload.ConcurrencyPlanWorkers > 0)
            {
                return $"LibraDex one-thread concurrent-writer control replays the {workload.ConcurrencyPlanWorkers}-worker {workload.ConcurrencyLocality} tuple plan and caller chunk schedule serially.";
            }

            return threads > 1
                ? $"LibraDex threaded write uses public BeginConcurrentWriter with equal fixed worker partitions, synchronized start, and {workload.ConcurrencyLocality} key locality."
                : "LibraDex single-thread write uses public BeginConcurrentWriter for direct-vs-concurrent-writer overhead comparison.";
        }

        if (workload.WritePath == "concurrent-batch")
        {
            if (workload.ConcurrencyPlanWorkers > 0)
            {
                return $"LibraDex one-thread concurrent-batch control replays the {workload.ConcurrencyPlanWorkers}-worker {workload.ConcurrencyLocality} tuple plan and batch boundaries serially.";
            }

            return threads > 1
                ? $"LibraDex threaded write uses one public BeginConcurrentBatch per fixed worker chunk, synchronized start, and {workload.ConcurrencyLocality} key locality."
                : "LibraDex single-thread write uses public BeginConcurrentBatch for direct-vs-concurrent-batch overhead comparison.";
        }

        if (workload.WritePath == "direct")
        {
            return "LibraDex direct write uses public immediate Insert path with one worker.";
        }

        return "LibraDex batch write uses explicit batch publication; threaded variants are skipped.";
    }

    private static string NoteForSqliteWrite(WorkloadSpec workload, int threads)
    {
        if ((workload.WritePath == "concurrent-writer" || workload.WritePath == "concurrent-batch") && threads > 1)
        {
            return $"SQLite receives the same equal fixed worker partitions and {workload.ConcurrencyLocality} key locality, but transaction execution is serialized with a runtime lock because SQLite is single-writer.";
        }

        return "";
    }

    private static string NoteForRead(WorkloadSpec workload)
    {
        return workload.Id switch
        {
            "count-all-api" => "Count-all reports same-thread hot throughput from the public Count API versus SQLite COUNT(*) and records first-session call latency separately.",
            "count-range-api" or "count-prefix-api" => "Range/prefix count uses the public Count(condition) API so criteria-count behavior is measured instead of reader materialization.",
            _ => ""
        };
    }

    private static long WriteLibraConcurrent(IDisposable index, ShapeSpec shape, int batchSize, int threads, int[] order)
    {
        int workers = Math.Max(1, threads);
        int next = 0;
        long inserted = 0;
        object claimSync = new();
        Thread[] active = new Thread[workers];
        for (int t = 0; t < active.Length; t++)
        {
            active[t] = new Thread(() =>
            {
                while (true)
                {
                    int start;
                    int end;
                    lock (claimSync)
                    {
                        if (next >= order.Length) return;
                        start = next;
                        end = Math.Min(order.Length, start + batchSize);
                        next = end;
                    }

                    long local = 0;
                    for (int i = start; i < end; i++)
                    {
                        InsertLibraDirect(index, shape, order[i]);
                        local++;
                    }

                    Interlocked.Add(ref inserted, local);
                }
            });
            active[t].Start();
        }

        for (int i = 0; i < active.Length; i++) active[i].Join();
        return inserted;
    }

    private static WriteMeasurement WriteLibraConcurrentWriter(IDisposable index, ShapeSpec shape, int batchSize, int[][] orders)
    {
        return shape.Id switch
        {
            "ss8-8" => WriteLibraConcurrentWriterGeneric(
                (LibraDexIndex<ulong, ulong>)index,
                batchSize,
                orders,
                static i => Key8(i),
                static i => Identity8(i)),
            "ss16-8" => WriteLibraConcurrentWriterGeneric(
                (LibraDexIndex<Guid, ulong>)index,
                batchSize,
                orders,
                static i => KeyGuid(i),
                static i => Identity8(i)),
            "ss8-16" => WriteLibraConcurrentWriterGeneric(
                (LibraDexIndex<ulong, Guid>)index,
                batchSize,
                orders,
                static i => Key8(i),
                static i => IdentityGuid(i)),
            "ss16-16" => WriteLibraConcurrentWriterGeneric(
                (LibraDexIndex<Guid, Guid>)index,
                batchSize,
                orders,
                static i => KeyGuid(i),
                static i => IdentityGuid(i)),
            "fs32-8" => WriteLibraConcurrentWriterGeneric(
                (LibraDexIndex<byte[], ulong>)index,
                batchSize,
                orders,
                static i => KeyBytes(i, 32),
                static i => Identity8(i)),
            "fs32-16" => WriteLibraConcurrentWriterGeneric(
                (LibraDexIndex<byte[], Guid>)index,
                batchSize,
                orders,
                static i => KeyBytes(i, 32),
                static i => IdentityGuid(i)),
            _ => throw new NotSupportedException($"Public concurrent writer is only available for generic fixed-scalar shapes in this harness, not {shape.Id}.")
        };
    }

    private static WriteMeasurement WriteLibraConcurrentWriterGeneric<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> index,
        int batchSize,
        int[][] orders,
        Func<int, TKey> keyFactory,
        Func<int, TIdentity> identityFactory)
    {
        LibraDexQueuedWriter<TKey, TIdentity> writer = index.BeginConcurrentWriter();
        long inserted = 0;
        Thread[] active = new Thread[orders.Length];
        ThreadWorkerMeasurement[] measurements = new ThreadWorkerMeasurement[orders.Length];
        using CountdownEvent ready = new(orders.Length);
        using ManualResetEventSlim start = new(false);
        ExceptionDispatchInfo? failure = null;
        long startTimestamp = 0;
        for (int t = 0; t < active.Length; t++)
        {
            int worker = t;
            active[t] = new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                try
                {
                    int[] order = orders[worker];
                    long workerItems = 0;
                    long workerChunks = 0;
                    for (int chunkStart = 0; chunkStart < order.Length; chunkStart += batchSize)
                    {
                        using LibraDexConcurrentWriteAction<TKey, TIdentity> action = writer.BeginAction();
                        int end = Math.Min(order.Length, chunkStart + batchSize);
                        for (int i = chunkStart; i < end; i++)
                        {
                            int ordinal = order[i];
                            LibraDexGenericInsertResult result = action.Insert(keyFactory(ordinal), identityFactory(ordinal));
                            if (result.Inserted)
                            {
                                workerItems++;
                            }
                        }

                        workerChunks++;
                    }

                    long elapsed = Math.Max(1, Stopwatch.GetTimestamp() - startTimestamp);
                    measurements[worker] = new ThreadWorkerMeasurement(workerItems, workerChunks, 0, elapsed);
                    Interlocked.Add(ref inserted, workerItems);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                }
            });
            active[t].Start();
        }

        ready.Wait();
        startTimestamp = Stopwatch.GetTimestamp();
        start.Set();
        for (int i = 0; i < active.Length; i++) active[i].Join();
        long elapsedTimestampTicks = Math.Max(1, Stopwatch.GetTimestamp() - startTimestamp);
        failure?.Throw();
        return new WriteMeasurement(
            inserted,
            measurements,
            elapsedTimestampTicks,
            CreateAdmissionSummary(writer.GetAdmissionDiagnostics()));
    }

    private static WriteMeasurement WriteLibraConcurrentBatch(IDisposable index, ShapeSpec shape, int batchSize, int[][] orders)
    {
        return shape.Id switch
        {
            "ss8-8" => WriteLibraConcurrentBatchGeneric(
                (LibraDexIndex<ulong, ulong>)index,
                batchSize,
                orders,
                static i => Key8(i),
                static i => Identity8(i)),
            "fs32-8" => WriteLibraConcurrentBatchGeneric(
                (LibraDexIndex<byte[], ulong>)index,
                batchSize,
                orders,
                static i => KeyBytes(i, 32),
                static i => Identity8(i)),
            _ => throw new NotSupportedException($"Public concurrent batch is only available for SS8-8 and FS32-8 in this harness, not {shape.Id}.")
        };
    }

    private static WriteMeasurement WriteLibraConcurrentBatchGeneric<TKey, TIdentity>(
        LibraDexIndex<TKey, TIdentity> index,
        int batchSize,
        int[][] orders,
        Func<int, TKey> keyFactory,
        Func<int, TIdentity> identityFactory)
    {
        long inserted = 0;
        Thread[] active = new Thread[orders.Length];
        ThreadWorkerMeasurement[] measurements = new ThreadWorkerMeasurement[orders.Length];
        ConcurrentBatchAttributionMeasurement[] attributions = new ConcurrentBatchAttributionMeasurement[orders.Length];
        BatchTimingMeasurement[][] batchTimings = new BatchTimingMeasurement[orders.Length][];
        for (int i = 0; i < orders.Length; i++)
        {
            batchTimings[i] = new BatchTimingMeasurement[checked((int)GetChunkCount(orders[i].Length, batchSize))];
        }

        using CountdownEvent ready = new(orders.Length);
        using ManualResetEventSlim start = new(false);
        ExceptionDispatchInfo? failure = null;
        long startTimestamp = 0;
        for (int t = 0; t < active.Length; t++)
        {
            int worker = t;
            active[t] = new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                try
                {
                    int[] order = orders[worker];
                    long workerItems = 0;
                    long workerChunks = 0;
                    long workerPublications = 0;
                    long workerOwnershipConflicts = 0;
                    long workerConflictPublications = 0;
                    long workerEmptyContextAborts = 0;
                    long workerTopologyFallbacks = 0;
                    long workerMaximumStagedMutations = 0;
                    long workerStagedMutationsBeforeConflict = 0;
                    int workerBatch = 0;
                    for (int chunkStart = 0; chunkStart < order.Length; chunkStart += batchSize)
                    {
                        long batchStarted = Stopwatch.GetTimestamp();
                        int end = Math.Min(order.Length, chunkStart + batchSize);
                        long local = 0;
                        using LibraDexConcurrentBatch<TKey, TIdentity> batch = index.BeginConcurrentBatch();
                        for (int i = chunkStart; i < end; i++)
                        {
                            int ordinal = order[i];
                            LibraDexGenericInsertResult result = batch.Insert(keyFactory(ordinal), identityFactory(ordinal));
                            if (result.Inserted)
                            {
                                local++;
                            }
                        }
                        long publishStarted = Stopwatch.GetTimestamp();
                        LibraDexConcurrentBatchPublishResult publish = batch.Publish();
                        long publishElapsed = Math.Max(1, Stopwatch.GetTimestamp() - publishStarted);
                        if (publish.InsertedCount != local)
                        {
                            throw new InvalidDataException($"Concurrent batch publish inserted {publish.InsertedCount} rows but worker counted {local}.");
                        }

                        workerItems += local;
                        workerChunks++;
                        workerPublications += publish.PublishedContextCount;
                        workerOwnershipConflicts += publish.OwnershipConflictCount;
                        workerConflictPublications += publish.ConflictPublicationCount;
                        workerEmptyContextAborts += publish.EmptyContextAbortCount;
                        workerTopologyFallbacks += publish.TopologyFallbackCount;
                        workerMaximumStagedMutations = Math.Max(workerMaximumStagedMutations, publish.MaximumStagedMutationCount);
                        workerStagedMutationsBeforeConflict += publish.StagedMutationCountBeforeConflictPublication;
                        batchTimings[worker][workerBatch++] = new BatchTimingMeasurement(
                            Math.Max(1, Stopwatch.GetTimestamp() - batchStarted),
                            publishElapsed,
                            publish.OwnershipConflictCount > 0);
                    }

                    long elapsed = Math.Max(1, Stopwatch.GetTimestamp() - startTimestamp);
                    measurements[worker] = new ThreadWorkerMeasurement(workerItems, workerChunks, workerPublications, elapsed);
                    attributions[worker] = new ConcurrentBatchAttributionMeasurement(
                        workerOwnershipConflicts,
                        workerConflictPublications,
                        workerEmptyContextAborts,
                        workerTopologyFallbacks,
                        workerMaximumStagedMutations,
                        workerStagedMutationsBeforeConflict);
                    Interlocked.Add(ref inserted, workerItems);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                }
            });
            active[t].Start();
        }

        ready.Wait();
        startTimestamp = Stopwatch.GetTimestamp();
        start.Set();
        for (int i = 0; i < active.Length; i++) active[i].Join();
        long elapsedTimestampTicks = Math.Max(1, Stopwatch.GetTimestamp() - startTimestamp);
        failure?.Throw();
        LibraDexWriteAdmissionDiagnostics admission = index.BeginConcurrentWriter().GetAdmissionDiagnostics();
        return new WriteMeasurement(
            inserted,
            measurements,
            elapsedTimestampTicks,
            JoinNotes(
                CreateConcurrentBatchAttributionSummary(attributions),
                CreateAdmissionSummary(admission)),
            BatchMetrics.Create(batchTimings));
    }

    /// <summary>
    /// Formats one post-measurement admission snapshot for durable CSV and HTML reporting without adding diagnostic work to the timed write interval.<br/>
    /// Queue averages use only requests that actually entered the pending queue; zero queued requests therefore report a zero average rather than an undefined value.<br/>
    /// </summary>
    /// <param name="diagnostics">The file-session counters captured after all measured workers have completed.<br/></param>
    /// <returns>A compact stable key/value note consumed by the report's concurrency diagnostics columns.<br/></returns>
    private static string CreateAdmissionSummary(LibraDexWriteAdmissionDiagnostics diagnostics)
    {
        double totalWaitMilliseconds = diagnostics.TotalQueueWaitTicks * 1000D / Stopwatch.Frequency;
        double averageWaitMilliseconds = diagnostics.TotalQueuedWriters <= 0
            ? 0D
            : totalWaitMilliseconds / diagnostics.TotalQueuedWriters;
        double maximumWaitMilliseconds = diagnostics.MaximumQueueWaitTicks * 1000D / Stopwatch.Frequency;
        double shelfWaitMilliseconds = diagnostics.ShelfWaitTicks * 1000D / Stopwatch.Frequency;
        return FormattableString.Invariant(
            $"admissionQueued={diagnostics.TotalQueuedWriters}; admissionGranted={diagnostics.TotalGrantedWriters}; admissionMaxPending={diagnostics.MaximumQueuedWriters}; admissionCanceled={diagnostics.CanceledWriters}; admissionTimedOut={diagnostics.TimedOutWriters}; admissionRejected={diagnostics.RejectedWriters}; admissionAvgWaitMs={averageWaitMilliseconds:F3}; admissionMaxWaitMs={maximumWaitMilliseconds:F3}; shelfWaits={diagnostics.ShelfWaitCount}; shelfWaitMs={shelfWaitMilliseconds:F3}");
    }

    private static string CreateConcurrentBatchAttributionSummary(ConcurrentBatchAttributionMeasurement[] attributions)
    {
        long ownershipConflicts = 0;
        long conflictPublications = 0;
        long emptyContextAborts = 0;
        long topologyFallbacks = 0;
        long maximumStagedMutations = 0;
        long stagedMutationsBeforeConflict = 0;
        for (int i = 0; i < attributions.Length; i++)
        {
            ConcurrentBatchAttributionMeasurement attribution = attributions[i];
            ownershipConflicts += attribution.OwnershipConflicts;
            conflictPublications += attribution.ConflictPublications;
            emptyContextAborts += attribution.EmptyContextAborts;
            topologyFallbacks += attribution.TopologyFallbacks;
            maximumStagedMutations = Math.Max(maximumStagedMutations, attribution.MaximumStagedMutations);
            stagedMutationsBeforeConflict += attribution.StagedMutationsBeforeConflict;
        }

        double averageStagedBeforeConflict = conflictPublications == 0
            ? 0D
            : stagedMutationsBeforeConflict / (double)conflictPublications;
        return FormattableString.Invariant(
            $"concurrentBatch ownershipConflicts={ownershipConflicts}; conflictPublications={conflictPublications}; emptyContextAborts={emptyContextAborts}; topologyFallbacks={topologyFallbacks}; maximumStagedMutations={maximumStagedMutations}; averageStagedBeforeConflict={averageStagedBeforeConflict:F3}");
    }

    private static void InsertLibraDirect(IDisposable index, ShapeSpec shape, int i)
    {
        switch (shape.Id)
        {
            case "ss8-8":
                _ = ((LibraDexIndex<ulong, ulong>)index).Insert(Key8(i), Identity8(i));
                break;
            case "ss16-8":
                _ = ((LibraDexIndex<Guid, ulong>)index).Insert(KeyGuid(i), Identity8(i));
                break;
            case "ss8-16":
                _ = ((LibraDexIndex<ulong, Guid>)index).Insert(Key8(i), IdentityGuid(i));
                break;
            case "ss16-16":
                _ = ((LibraDexIndex<Guid, Guid>)index).Insert(KeyGuid(i), IdentityGuid(i));
                break;
            case "fs32-8":
                _ = ((LibraDexIndex<byte[], ulong>)index).Insert(KeyBytes(i, 32), Identity8(i));
                break;
            case "fs32-16":
                _ = ((LibraDexIndex<byte[], Guid>)index).Insert(KeyBytes(i, 32), IdentityGuid(i));
                break;
            case "sv8":
                _ = ((Scalar8VarIdentityIndex)index).Insert(Key8(i), IdentityBytes(i, 24));
                break;
            case "sv16":
                SplitGuid(KeyGuid(i), out ulong keyHigh, out ulong keyLow);
                _ = ((Scalar16VarIdentityIndex)index).Insert(keyHigh, keyLow, IdentityBytes(i, 24));
                break;
            case "vs8":
                _ = ((VarKeyScalar8Index)index).Insert(KeyBytes(i, 24), Identity8(i));
                break;
            case "vs16":
                SplitGuid(IdentityGuid(i), out ulong identityHigh, out ulong identityLow);
                _ = ((VarKeyScalar16Index)index).Insert(KeyBytes(i, 24), identityHigh, identityLow);
                break;
            case "vv":
                _ = ((VarKeyVarIdentityIndex)index).Insert(KeyBytes(i, 24), IdentityBytes(i, 24));
                break;
        }
    }

    private static RunResult Measure(Func<MeasureResult> action, string notes = "")
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Stopwatch sw = Stopwatch.StartNew();
        MeasureResult measured = action();
        sw.Stop();
        double seconds = measured.ElapsedTimestampTicks > 0
            ? Math.Max(measured.ElapsedTimestampTicks / (double)Stopwatch.Frequency, 0.000001D)
            : Math.Max(sw.Elapsed.TotalSeconds, 0.000001D);
        ThreadMetrics threadMetrics = ThreadMetrics.Create(measured.Workers);
        if (threadMetrics.ActiveThreadCount == 1 &&
            threadMetrics.AverageItemsPerSecond <= 0D)
        {
            double rate = measured.ResultItems / seconds;
            threadMetrics = threadMetrics with
            {
                AverageItemsPerSecond = rate,
                MedianItemsPerSecond = rate,
                MaximumItemsPerSecond = rate,
                MinimumItemsPerSecond = rate
            };
        }

        return new RunResult(
            "", "", "", "", 0, 0, "", 0, "",
            measured.Operations,
            measured.Operations / seconds,
            measured.ResultItems,
            measured.ResultItems / seconds,
            0,
            0,
            JoinNotes(notes, measured.Notes),
            threadMetrics.ActiveThreadCount,
            threadMetrics.AverageItemsPerSecond,
            threadMetrics.MedianItemsPerSecond,
            threadMetrics.MaximumItemsPerSecond,
            threadMetrics.MinimumItemsPerSecond,
            threadMetrics.MinimumItems,
            threadMetrics.MaximumItems,
            threadMetrics.ChunkCount,
            threadMetrics.PublicationCount,
            threadMetrics.ItemsPerPublication,
            measured.Locality.DistinctShelfCount,
            measured.Locality.MinimumShelvesPerWorker,
            measured.Locality.MaximumShelvesPerWorker,
            measured.Locality.SharedShelfCount,
            measured.Locality.MaximumWorkersPerShelf,
            measured.Locality.SharedShelfItemPercent,
            measured.Locality.DistinctParentRouterCount,
            measured.Locality.MinimumParentRoutersPerWorker,
            measured.Locality.MaximumParentRoutersPerWorker,
            measured.Locality.SharedParentRouterCount,
            measured.Locality.MaximumWorkersPerParentRouter,
            measured.Locality.SharedParentRouterItemPercent,
            measured.BatchMetrics.SampleCount,
            measured.BatchMetrics.BatchLatencyP50Milliseconds,
            measured.BatchMetrics.BatchLatencyP95Milliseconds,
            measured.BatchMetrics.BatchLatencyP99Milliseconds,
            measured.BatchMetrics.BatchLatencyMaximumMilliseconds,
            measured.BatchMetrics.PublishLatencyP50Milliseconds,
            measured.BatchMetrics.PublishLatencyP95Milliseconds,
            measured.BatchMetrics.PublishLatencyP99Milliseconds,
            measured.BatchMetrics.PublishLatencyMaximumMilliseconds,
            measured.BatchMetrics.ConflictedBatchCount,
            measured.BatchMetrics.ConflictedBatchLatencyP50Milliseconds,
            measured.BatchMetrics.ConflictedBatchLatencyP95Milliseconds,
            measured.BatchMetrics.ConflictedBatchLatencyP99Milliseconds,
            measured.BatchMetrics.UnconflictedBatchLatencyP50Milliseconds,
            measured.BatchMetrics.UnconflictedBatchLatencyP95Milliseconds,
            measured.BatchMetrics.UnconflictedBatchLatencyP99Milliseconds);
    }

    /// <summary>
    /// Calculates the number of caller-visible chunks required to process an item count at the requested batch size.<br/>
    /// The value is used only after measured work completes so reporting does not add allocation or aggregation overhead to the timed operation.<br/>
    /// </summary>
    /// <param name="items">The number of successfully processed items.<br/></param>
    /// <param name="batchSize">The maximum number of items assigned to one claimed chunk.<br/></param>
    /// <returns>The ceiling of items divided by batch size, or zero when either input is not positive.<br/></returns>
    private static long GetChunkCount(long items, int batchSize)
    {
        if (items <= 0 || batchSize <= 0)
        {
            return 0;
        }

        return (items + batchSize - 1L) / batchSize;
    }

    private static IDisposable CreateLibraIndex(ShapeSpec shape, string path, long sv16ReadCacheMaxBytes = 0)
    {
        return shape.Id switch
        {
            "ss8-8" => Indexes.Create<ulong, ulong>(path),
            "ss16-8" => Indexes.Create<Guid, ulong>(path),
            "ss8-16" => Indexes.Create<ulong, Guid>(path),
            "ss16-16" => Indexes.Create<Guid, Guid>(path),
            "fs32-8" => Indexes.Create<byte[], ulong>(path, keyWidth: LibraDexScalarWidth.Bytes32),
            "fs32-16" => Indexes.Create<byte[], Guid>(path, keyWidth: LibraDexScalarWidth.Bytes32),
            "sv8" => Indexes.SV8.Create(path, maxIdentityLength: 64),
            "sv16" => Indexes.SV16.Create(path, maxIdentityLength: 64, readCacheMaxBytes: sv16ReadCacheMaxBytes),
            "vs8" => Indexes.VS8.Create(path, maxKeyLength: 64),
            "vs16" => Indexes.VS16.Create(path, maxKeyLength: 64),
            "vv" => Indexes.VV.Create(path, maxKeyLength: 64, maxIdentityLength: 64),
            _ => throw new NotSupportedException(shape.Id)
        };
    }

    private static IDisposable OpenLibraIndex(ShapeSpec shape, string path, long sv16ReadCacheMaxBytes = 0)
    {
        return shape.Id switch
        {
            "ss8-8" => Indexes.Open<ulong, ulong>(path),
            "ss16-8" => Indexes.Open<Guid, ulong>(path),
            "ss8-16" => Indexes.Open<ulong, Guid>(path),
            "ss16-16" => Indexes.Open<Guid, Guid>(path),
            "fs32-8" => Indexes.Open<byte[], ulong>(path, keyWidth: LibraDexScalarWidth.Bytes32),
            "fs32-16" => Indexes.Open<byte[], Guid>(path, keyWidth: LibraDexScalarWidth.Bytes32),
            "sv8" => Indexes.SV8.Open(path, maxIdentityLength: 64),
            "sv16" => Indexes.SV16.Open(path, maxIdentityLength: 64, readCacheMaxBytes: sv16ReadCacheMaxBytes),
            "vs8" => Indexes.VS8.Open(path, maxKeyLength: 64),
            "vs16" => Indexes.VS16.Open(path, maxKeyLength: 64),
            "vv" => Indexes.VV.Open(path, maxKeyLength: 64, maxIdentityLength: 64),
            _ => throw new NotSupportedException(shape.Id)
        };
    }

    private static long WriteLibraSingle(IDisposable index, ShapeSpec shape, int batchSize, int[] order)
    {
        long inserted = 0;
        int i = 0;
        while (i < order.Length)
        {
            int end = Math.Min(order.Length, i + batchSize);
            InsertLibraBatch(index, shape, order, i, end);
            inserted += end - i;
            i = end;
        }

        return inserted;
    }

    private static void InsertLibraBatch(IDisposable index, ShapeSpec shape, int[] order, int start, int end)
    {
        switch (shape.Id)
        {
            case "ss8-8":
                {
                    var ix = (LibraDexIndex<ulong, ulong>)index;
                    ix.Batch.Enable();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = ix.Insert(Key8(i), Identity8(i)); }
                    _ = ix.Batch.CommitAndDisable();
                    break;
                }
            case "ss16-8":
                {
                    var ix = (LibraDexIndex<Guid, ulong>)index;
                    ix.Batch.Enable();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = ix.Insert(KeyGuid(i), Identity8(i)); }
                    _ = ix.Batch.CommitAndDisable();
                    break;
                }
            case "ss8-16":
                {
                    var ix = (LibraDexIndex<ulong, Guid>)index;
                    ix.Batch.Enable();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = ix.Insert(Key8(i), IdentityGuid(i)); }
                    _ = ix.Batch.CommitAndDisable();
                    break;
                }
            case "ss16-16":
                {
                    var ix = (LibraDexIndex<Guid, Guid>)index;
                    ix.Batch.Enable();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = ix.Insert(KeyGuid(i), IdentityGuid(i)); }
                    _ = ix.Batch.CommitAndDisable();
                    break;
                }
            case "fs32-8":
                {
                    var ix = (LibraDexIndex<byte[], ulong>)index;
                    ix.Batch.Enable();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = ix.Insert(KeyBytes(i, 32), Identity8(i)); }
                    _ = ix.Batch.CommitAndDisable();
                    break;
                }
            case "fs32-16":
                {
                    var ix = (LibraDexIndex<byte[], Guid>)index;
                    ix.Batch.Enable();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = ix.Insert(KeyBytes(i, 32), IdentityGuid(i)); }
                    _ = ix.Batch.CommitAndDisable();
                    break;
                }
            case "sv8":
                {
                    var ix = (Scalar8VarIdentityIndex)index;
                    using Scalar8VarIdentityBatch batch = ix.BeginBatch();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = batch.Insert(Key8(i), IdentityBytes(i, 24)); }
                    _ = batch.Commit();
                    break;
                }
            case "sv16":
                {
                    var ix = (Scalar16VarIdentityIndex)index;
                    using Scalar16VarIdentityBatch batch = ix.BeginBatch();
                    for (int x = start; x < end; x++)
                    {
                        int i = order[x];
                        SplitGuid(KeyGuid(i), out ulong hi, out ulong lo);
                        _ = batch.Insert(hi, lo, IdentityBytes(i, 24));
                    }

                    _ = batch.Commit();
                    break;
                }
            case "vs8":
                {
                    var ix = (VarKeyScalar8Index)index;
                    using VarKeyScalar8Batch batch = ix.BeginBatch();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = batch.Insert(KeyBytes(i, 24), Identity8(i)); }
                    _ = batch.Commit();
                    break;
                }
            case "vs16":
                {
                    var ix = (VarKeyScalar16Index)index;
                    using VarKeyScalar16Batch batch = ix.BeginBatch();
                    for (int x = start; x < end; x++)
                    {
                        int i = order[x];
                        SplitGuid(IdentityGuid(i), out ulong hi, out ulong lo);
                        _ = batch.Insert(KeyBytes(i, 24), hi, lo);
                    }

                    _ = batch.Commit();
                    break;
                }
            case "vv":
                {
                    var ix = (VarKeyVarIdentityIndex)index;
                    using VarKeyVarIdentityBatch batch = ix.BeginBatch();
                    for (int x = start; x < end; x++) { int i = order[x]; _ = batch.Insert(KeyBytes(i, 24), IdentityBytes(i, 24)); }
                    _ = batch.Commit();
                    break;
                }
        }
    }

    private static void SeedLibra(ShapeSpec shape, int batchSize, int items, string path, long sv16ReadCacheMaxBytes = 0)
    {
        using IDisposable index = CreateLibraIndex(shape, path, sv16ReadCacheMaxBytes);
        _ = WriteLibraSingle(index, shape, batchSize, BuildSortedOrder(items));
    }

    private static MeasureResult ReadLibra(IDisposable index, ShapeSpec shape, WorkloadSpec workload, int threads, int items)
    {
        if (workload.Id == "count-all-api")
        {
            return RunCountAllHotThroughput(threads, () => LibraCountAll(index, shape));
        }

        int operations = GetOperationCount(workload, items);
        return RunWorkers(threads, operations, i => ReadLibraOperation(index, shape, workload, i, items));
    }

    private static long ReadLibraOperation(IDisposable index, ShapeSpec shape, WorkloadSpec workload, int operation, int items)
    {
        return workload.Id switch
        {
            "lookup-one-identities" => LibraRange(index, shape, PickOrdinal(operation, items), PickOrdinal(operation, items), "identities"),
            "lookup-list-identities" => LibraLookupList(index, shape, operation, items),
            "range-identities" => LibraRange(index, shape, PickOrdinal(operation, items), RangeUpper(PickOrdinal(operation, items), items), "identities"),
            "prefix-identities" => LibraPrefix(index, shape, PickOrdinal(operation, items), "identities"),
            "range-keys" => LibraRange(index, shape, PickOrdinal(operation, items), RangeUpper(PickOrdinal(operation, items), items), "keys"),
            "prefix-keys" => LibraPrefix(index, shape, PickOrdinal(operation, items), "keys"),
            "range-pairs" => LibraRange(index, shape, PickOrdinal(operation, items), RangeUpper(PickOrdinal(operation, items), items), "pairs"),
            "prefix-pairs" => LibraPrefix(index, shape, PickOrdinal(operation, items), "pairs"),
            "count-all-api" => LibraCountAll(index, shape),
            "count-range-api" => LibraCountRange(index, shape, PickOrdinal(operation, items), RangeUpper(PickOrdinal(operation, items), items)),
            "count-prefix-api" => LibraCountPrefix(index, shape, PickOrdinal(operation, items)),
            _ => 0
        };
    }

    private static long LibraLookupList(IDisposable index, ShapeSpec shape, int operation, int items)
    {
        long total = 0;
        int baseOrdinal = (operation * KeyListWidth) % items;
        for (int i = 0; i < KeyListWidth; i++)
        {
            int ordinal = (baseOrdinal + (i * 7919)) % items;
            total += LibraRange(index, shape, ordinal, ordinal, "identities");
        }

        return total;
    }

    private static long LibraRange(IDisposable index, ShapeSpec shape, int lowerOrdinal, int upperOrdinal, string mode)
    {
        return shape.Id switch
        {
            "ss8-8" => GenericRange((LibraDexIndex<ulong, ulong>)index, Key8(lowerOrdinal), Key8(upperOrdinal), mode),
            "ss16-8" => GenericRange((LibraDexIndex<Guid, ulong>)index, KeyGuid(lowerOrdinal), KeyGuid(upperOrdinal), mode),
            "ss8-16" => GenericRange((LibraDexIndex<ulong, Guid>)index, Key8(lowerOrdinal), Key8(upperOrdinal), mode),
            "ss16-16" => GenericRange((LibraDexIndex<Guid, Guid>)index, KeyGuid(lowerOrdinal), KeyGuid(upperOrdinal), mode),
            "fs32-8" => GenericRange((LibraDexIndex<byte[], ulong>)index, KeyBytes(lowerOrdinal, 32), KeyBytes(upperOrdinal, 32), mode),
            "fs32-16" => GenericRange((LibraDexIndex<byte[], Guid>)index, KeyBytes(lowerOrdinal, 32), KeyBytes(upperOrdinal, 32), mode),
            "sv8" => Sv8Range((Scalar8VarIdentityIndex)index, lowerOrdinal, upperOrdinal, mode),
            "sv16" => Sv16Range((Scalar16VarIdentityIndex)index, lowerOrdinal, upperOrdinal, mode),
            "vs8" => Vs8Range((VarKeyScalar8Index)index, lowerOrdinal, upperOrdinal, mode),
            "vs16" => Vs16Range((VarKeyScalar16Index)index, lowerOrdinal, upperOrdinal, mode),
            "vv" => VvRange((VarKeyVarIdentityIndex)index, lowerOrdinal, upperOrdinal, mode),
            _ => 0
        };
    }

    private static long LibraPrefix(IDisposable index, ShapeSpec shape, int ordinal, string mode)
    {
        GetPrefixBounds(shape, ordinal, out byte[] lower, out byte[] upper);
        return shape.Id switch
        {
            "fs32-8" => GenericRange((LibraDexIndex<byte[], ulong>)index, lower, upper, mode),
            "fs32-16" => GenericRange((LibraDexIndex<byte[], Guid>)index, lower, upper, mode),
            "vs8" => Vs8Range((VarKeyScalar8Index)index, lower, upper, mode),
            "vs16" => Vs16Range((VarKeyScalar16Index)index, lower, upper, mode),
            "vv" => VvRange((VarKeyVarIdentityIndex)index, lower, upper, mode),
            _ => 0
        };
    }

    private static long GenericRange<TKey, TIdentity>(LibraDexIndex<TKey, TIdentity> index, TKey lower, TKey upper, string mode)
    {
        using LibraDexRangeReader<TKey, TIdentity> reader = index.OpenRangeReader(lower, upper);
        if (mode == "count")
        {
            return reader.Count;
        }

        long count = 0;
        if (mode == "identities")
        {
            while (reader.TryReadNextIdentity(out _)) count++;
            return count;
        }

        if (mode == "keys")
        {
            while (reader.TryReadNextKey(out _)) count++;
            return count;
        }

        while (reader.TryReadNext(out _, out _)) count++;
        return count;
    }

    private static long Sv8Range(Scalar8VarIdentityIndex index, int lower, int upper, string mode)
    {
        using Scalar8VarIdentityRangeReader reader = index.OpenRangeReader(Key8(lower), Key8(upper));
        if (mode == "count")
        {
            return reader.Count;
        }

        long count = 0;
        while (reader.MoveNext())
        {
            if (mode == "keys")
            {
                _ = reader.CurrentEncodedKey;
            }
            else if (mode == "identities")
            {
                _ = reader.CurrentIdentityLength;
            }
            else
            {
                _ = reader.CurrentEncodedKey;
                _ = reader.CurrentIdentityLength;
            }

            count++;
        }

        return count;
    }

    private static long Sv16Range(Scalar16VarIdentityIndex index, int lower, int upper, string mode)
    {
        SplitGuid(KeyGuid(lower), out ulong lowerHigh, out ulong lowerLow);
        SplitGuid(KeyGuid(upper), out ulong upperHigh, out ulong upperLow);
        using Scalar16VarIdentityRangeReader reader = index.OpenRangeReader(lowerHigh, lowerLow, upperHigh, upperLow);
        if (mode == "count")
        {
            return reader.Count;
        }

        long count = 0;
        while (reader.MoveNext())
        {
            if (mode == "keys")
            {
                _ = reader.CurrentEncodedKeyHigh;
                _ = reader.CurrentEncodedKeyLow;
            }
            else if (mode == "identities")
            {
                _ = reader.CurrentIdentityLength;
            }
            else
            {
                _ = reader.CurrentEncodedKeyHigh;
                _ = reader.CurrentEncodedKeyLow;
                _ = reader.CurrentIdentityLength;
            }

            count++;
        }

        return count;
    }

    private static long Vs8Range(VarKeyScalar8Index index, int lower, int upper, string mode)
    {
        byte[] lowerKey = KeyBytes(lower, 24);
        byte[] upperKey = KeyBytes(upper, 24);
        return Vs8Range(index, lowerKey, upperKey, mode);
    }

    private static long Vs8Range(VarKeyScalar8Index index, byte[] lowerKey, byte[] upperKey, string mode)
    {
        using VarKeyScalar8RangeReader reader = index.OpenRangeReader(lowerKey, upperKey);
        if (mode == "count")
        {
            return reader.Count;
        }

        long count = 0;
        while (reader.MoveNext())
        {
            if (mode == "keys")
            {
                _ = reader.CurrentKeyLength;
            }
            else if (mode == "identities")
            {
                _ = reader.CurrentEncodedIdentity;
            }
            else
            {
                _ = reader.CurrentKeyLength;
                _ = reader.CurrentEncodedIdentity;
            }

            count++;
        }

        return count;
    }

    private static long Vs16Range(VarKeyScalar16Index index, int lower, int upper, string mode)
    {
        byte[] lowerKey = KeyBytes(lower, 24);
        byte[] upperKey = KeyBytes(upper, 24);
        return Vs16Range(index, lowerKey, upperKey, mode);
    }

    private static long Vs16Range(VarKeyScalar16Index index, byte[] lowerKey, byte[] upperKey, string mode)
    {
        using VarKeyScalar16RangeReader reader = index.OpenRangeReader(lowerKey, upperKey);
        if (mode == "count")
        {
            return reader.Count;
        }

        long count = 0;
        while (reader.MoveNext())
        {
            if (mode == "keys")
            {
                _ = reader.CurrentKeyLength;
            }
            else if (mode == "identities")
            {
                reader.ReadCurrentIdentity(out ulong high, out ulong low);
                _ = high ^ low;
            }
            else
            {
                _ = reader.CurrentKeyLength;
                reader.ReadCurrentIdentity(out ulong high, out ulong low);
                _ = high ^ low;
            }

            count++;
        }

        return count;
    }

    private static long VvRange(VarKeyVarIdentityIndex index, int lower, int upper, string mode)
    {
        byte[] lowerKey = KeyBytes(lower, 24);
        byte[] upperKey = KeyBytes(upper, 24);
        return VvRange(index, lowerKey, upperKey, mode);
    }

    private static long VvRange(VarKeyVarIdentityIndex index, byte[] lowerKey, byte[] upperKey, string mode)
    {
        using VarKeyVarIdentityRangeReader reader = index.OpenRangeReader(lowerKey, upperKey);
        if (mode == "count")
        {
            return reader.Count;
        }

        long count = 0;
        while (reader.MoveNext())
        {
            if (mode == "keys")
            {
                _ = reader.CurrentKeyLength;
            }
            else if (mode == "identities")
            {
                _ = reader.CurrentIdentityLength;
            }
            else
            {
                _ = reader.CurrentKeyLength;
                _ = reader.CurrentIdentityLength;
            }

            count++;
        }

        return count;
    }

    private static long LibraCountAll(IDisposable index, ShapeSpec shape)
    {
        return shape.Id switch
        {
            "ss8-8" => ((LibraDexIndex<ulong, ulong>)index).Count(),
            "ss16-8" => ((LibraDexIndex<Guid, ulong>)index).Count(),
            "ss8-16" => ((LibraDexIndex<ulong, Guid>)index).Count(),
            "ss16-16" => ((LibraDexIndex<Guid, Guid>)index).Count(),
            "fs32-8" => ((LibraDexIndex<byte[], ulong>)index).Count(),
            "fs32-16" => ((LibraDexIndex<byte[], Guid>)index).Count(),
            "sv8" => ((Scalar8VarIdentityIndex)index).CountOrdinaryIdentities(),
            "sv16" => ((Scalar16VarIdentityIndex)index).CountOrdinaryIdentities(),
            "vs8" => ((VarKeyScalar8Index)index).CountOrdinaryIdentities(),
            "vs16" => ((VarKeyScalar16Index)index).CountOrdinaryIdentities(),
            "vv" => ((VarKeyVarIdentityIndex)index).CountOrdinaryIdentities(),
            _ => throw new NotSupportedException($"Shape '{shape.Id}' does not expose a public Count API.")
        };
    }

    private static long LibraCountRange(IDisposable index, ShapeSpec shape, int lowerOrdinal, int upperOrdinal)
    {
        return shape.Id switch
        {
            "ss8-8" => CountCondition((LibraDexIndex<ulong, ulong>)index, Key8(lowerOrdinal), Key8(upperOrdinal)),
            "ss16-8" => CountCondition((LibraDexIndex<Guid, ulong>)index, KeyGuid(lowerOrdinal), KeyGuid(upperOrdinal)),
            "ss8-16" => CountCondition((LibraDexIndex<ulong, Guid>)index, Key8(lowerOrdinal), Key8(upperOrdinal)),
            "ss16-16" => CountCondition((LibraDexIndex<Guid, Guid>)index, KeyGuid(lowerOrdinal), KeyGuid(upperOrdinal)),
            "fs32-8" => CountCondition((LibraDexIndex<byte[], ulong>)index, KeyBytes(lowerOrdinal, 32), KeyBytes(upperOrdinal, 32)),
            "fs32-16" => CountCondition((LibraDexIndex<byte[], Guid>)index, KeyBytes(lowerOrdinal, 32), KeyBytes(upperOrdinal, 32)),
            "sv8" => ((Scalar8VarIdentityIndex)index).CountIdentityRange(Key8(lowerOrdinal), Key8(upperOrdinal)),
            "sv16" => CountScalar16VarIdentityRange((Scalar16VarIdentityIndex)index, KeyGuid(lowerOrdinal), KeyGuid(upperOrdinal)),
            "vs8" => ((VarKeyScalar8Index)index).CountIdentityRange(KeyBytes(lowerOrdinal, 24), KeyBytes(upperOrdinal, 24)),
            "vs16" => ((VarKeyScalar16Index)index).CountIdentityRange(KeyBytes(lowerOrdinal, 24), KeyBytes(upperOrdinal, 24)),
            "vv" => ((VarKeyVarIdentityIndex)index).CountIdentityRange(KeyBytes(lowerOrdinal, 24), KeyBytes(upperOrdinal, 24)),
            _ => throw new NotSupportedException($"Shape '{shape.Id}' does not expose a public Count(condition) API.")
        };
    }

    private static long LibraCountPrefix(IDisposable index, ShapeSpec shape, int ordinal)
    {
        GetPrefixBounds(shape, ordinal, out byte[] lower, out byte[] upper);
        return shape.Id switch
        {
            "fs32-8" => CountCondition((LibraDexIndex<byte[], ulong>)index, lower, upper),
            "fs32-16" => CountCondition((LibraDexIndex<byte[], Guid>)index, lower, upper),
            "vs8" => ((VarKeyScalar8Index)index).CountEncodedIdentityPrefix(GetEncodedVarKeyPrefix(shape, ordinal)),
            "vs16" => ((VarKeyScalar16Index)index).CountEncodedIdentityPrefix(GetEncodedVarKeyPrefix(shape, ordinal)),
            "vv" => ((VarKeyVarIdentityIndex)index).CountEncodedIdentityPrefix(GetEncodedVarKeyPrefix(shape, ordinal)),
            _ => throw new NotSupportedException($"Shape '{shape.Id}' does not expose a public Count(condition) prefix API.")
        };
    }

    private static long CountCondition<TKey, TIdentity>(LibraDexIndex<TKey, TIdentity> index, TKey lower, TKey upper)
    {
        return index.Count(index.Where.Between(lower, upper).EndCondition);
    }

    /// <summary>
    /// Counts `SV16` identities for an ordinal range by encoding the GUID-like benchmark keys once and calling the shape-native range-count primitive.<br/>
    /// This keeps ShapeBench count workloads on the aggregate path instead of timing range-reader construction and reader count fallback.<br/>
    /// </summary>
    /// <param name="index">The opened `SV16` benchmark index.<br/></param>
    /// <param name="lower">The inclusive logical lower key.<br/></param>
    /// <param name="upper">The inclusive logical upper key.<br/></param>
    /// <returns>The number of identities in the encoded scalar-16 key range.<br/></returns>
    private static long CountScalar16VarIdentityRange(Scalar16VarIdentityIndex index, Guid lower, Guid upper)
    {
        SplitGuid(lower, out ulong lowerHigh, out ulong lowerLow);
        SplitGuid(upper, out ulong upperHigh, out ulong upperLow);
        return index.CountIdentityRange(lowerHigh, lowerLow, upperHigh, upperLow);
    }

    private static SqliteConnection OpenSqlite(string path)
    {
        SqliteConnection cn = new($"Data Source={path};Pooling=False");
        cn.Open();
        return cn;
    }

    private static ConnectionSet OpenSqliteConnections(string path, int count)
    {
        SqliteConnection[] connections = new SqliteConnection[count];
        for (int i = 0; i < connections.Length; i++)
        {
            connections[i] = OpenSqlite(path);
        }

        return new ConnectionSet(connections);
    }

    private static void CreateSqliteSchema(SqliteConnection cn, ShapeSpec shape)
    {
        using SqliteCommand cmd = cn.CreateCommand();
        string keyType = shape.KeyWidth == 8 ? "INTEGER NOT NULL" : "BLOB NOT NULL";
        string idType = shape.IdentityWidth == 8 ? "INTEGER NOT NULL" : "BLOB NOT NULL";
        cmd.CommandText = $"CREATE TABLE ix(k {keyType}, id {idType}); CREATE INDEX ix_k_id ON ix(k, id);";
        _ = cmd.ExecuteNonQuery();
    }

    private static long WriteSqliteSingle(SqliteConnection cn, ShapeSpec shape, int batchSize, int[] order)
    {
        long inserted = 0;
        int i = 0;
        while (i < order.Length)
        {
            int end = Math.Min(order.Length, i + batchSize);
            using SqliteTransaction tx = cn.BeginTransaction();
            using SqliteCommand cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO ix(k, id) VALUES ($k, $id);";
            SqliteParameter key = cmd.Parameters.Add("$k", shape.KeyWidth == 8 ? SqliteType.Integer : SqliteType.Blob);
            SqliteParameter id = cmd.Parameters.Add("$id", shape.IdentityWidth == 8 ? SqliteType.Integer : SqliteType.Blob);
            for (; i < end; i++)
            {
                SetSqliteParams(shape, key, id, order[i]);
                _ = cmd.ExecuteNonQuery();
                inserted++;
            }

            tx.Commit();
        }

        return inserted;
    }

    private static WriteMeasurement WriteSqliteThreaded(SqliteConnection[] connections, ShapeSpec shape, int batchSize, int[][] orders)
    {
        long inserted = 0;
        object writeSync = new();
        int threads = connections.Length;
        Thread[] workers = new Thread[threads];
        ThreadWorkerMeasurement[] measurements = new ThreadWorkerMeasurement[threads];
        using CountdownEvent ready = new(threads);
        using ManualResetEventSlim start = new(false);
        ExceptionDispatchInfo? failure = null;
        long startTimestamp = 0;
        for (int t = 0; t < workers.Length; t++)
        {
            int worker = t;
            workers[t] = new Thread(() =>
            {
                SqliteConnection cn = connections[worker];
                ready.Signal();
                start.Wait();
                try
                {
                    int[] order = orders[worker];
                    long workerItems = 0;
                    long workerChunks = 0;
                    for (int chunkStart = 0; chunkStart < order.Length; chunkStart += batchSize)
                    {
                        int end = Math.Min(order.Length, chunkStart + batchSize);
                        lock (writeSync)
                        {
                            using SqliteTransaction tx = cn.BeginTransaction();
                            using SqliteCommand cmd = cn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = "INSERT INTO ix(k, id) VALUES ($k, $id);";
                            SqliteParameter key = cmd.Parameters.Add("$k", shape.KeyWidth == 8 ? SqliteType.Integer : SqliteType.Blob);
                            SqliteParameter id = cmd.Parameters.Add("$id", shape.IdentityWidth == 8 ? SqliteType.Integer : SqliteType.Blob);
                            for (int i = chunkStart; i < end; i++)
                            {
                                SetSqliteParams(shape, key, id, order[i]);
                                _ = cmd.ExecuteNonQuery();
                            }

                            tx.Commit();
                        }

                        workerItems += end - chunkStart;
                        workerChunks++;
                    }

                    long elapsed = Math.Max(1, Stopwatch.GetTimestamp() - startTimestamp);
                    measurements[worker] = new ThreadWorkerMeasurement(workerItems, workerChunks, workerChunks, elapsed);
                    Interlocked.Add(ref inserted, workerItems);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                }
            });
            workers[t].Start();
        }

        ready.Wait();
        startTimestamp = Stopwatch.GetTimestamp();
        start.Set();
        for (int i = 0; i < workers.Length; i++) workers[i].Join();
        long elapsedTimestampTicks = Math.Max(1, Stopwatch.GetTimestamp() - startTimestamp);
        failure?.Throw();
        return new WriteMeasurement(inserted, measurements, elapsedTimestampTicks);
    }

    private static void SeedSqlite(ShapeSpec shape, int batchSize, int items, string path)
    {
        using SqliteConnection cn = OpenSqlite(path);
        CreateSqliteSchema(cn, shape);
        _ = WriteSqliteSingle(cn, shape, batchSize, BuildSortedOrder(items));
    }

    private static MeasureResult ReadSqlite(SqliteConnection[] connections, ShapeSpec shape, WorkloadSpec workload, int threads, int items)
    {
        if (workload.Id == "count-all-api")
        {
            using SqliteReadCommand countCommand = new(connections[0], shape, workload);
            return RunCountAllHotThroughput(threads, countCommand.ExecuteCountAll);
        }

        int operations = GetOperationCount(workload, items);
        int workers = Math.Max(1, threads);
        long resultItems = 0;
        Thread[] active = new Thread[workers];
        SqliteReadCommand[] commands = new SqliteReadCommand[workers];
        for (int t = 0; t < workers; t++)
        {
            int worker = t;
            commands[worker] = new SqliteReadCommand(connections[worker], shape, workload);
            active[t] = new Thread(() =>
            {
                SqliteReadCommand command = commands[worker];
                long localItems = 0;
                for (int i = worker; i < operations; i += workers)
                {
                    localItems += command.Execute(i, items);
                }

                Interlocked.Add(ref resultItems, localItems);
            });
            active[t].Start();
        }

        for (int i = 0; i < active.Length; i++) active[i].Join();
        for (int i = 0; i < commands.Length; i++) commands[i].Dispose();
        return new MeasureResult(operations, resultItems);
    }

    private static long ReadSqliteOperation(SqliteConnection cn, ShapeSpec shape, WorkloadSpec workload, int operation, int items)
    {
        return workload.Id switch
        {
            "lookup-one-identities" => SqliteRange(cn, shape, PickOrdinal(operation, items), PickOrdinal(operation, items), "identities"),
            "lookup-list-identities" => SqliteLookupList(cn, shape, operation, items),
            "range-identities" => SqliteRange(cn, shape, PickOrdinal(operation, items), RangeUpper(PickOrdinal(operation, items), items), "identities"),
            "prefix-identities" => SqlitePrefix(cn, shape, PickOrdinal(operation, items), "identities"),
            "range-keys" => SqliteRange(cn, shape, PickOrdinal(operation, items), RangeUpper(PickOrdinal(operation, items), items), "keys"),
            "prefix-keys" => SqlitePrefix(cn, shape, PickOrdinal(operation, items), "keys"),
            "range-pairs" => SqliteRange(cn, shape, PickOrdinal(operation, items), RangeUpper(PickOrdinal(operation, items), items), "pairs"),
            "prefix-pairs" => SqlitePrefix(cn, shape, PickOrdinal(operation, items), "pairs"),
            "count-all-api" => SqliteCountAll(cn),
            "count-range-api" => SqliteCount(cn, shape, PickOrdinal(operation, items), RangeUpper(PickOrdinal(operation, items), items)),
            "count-prefix-api" => SqlitePrefix(cn, shape, PickOrdinal(operation, items), "count"),
            _ => 0
        };
    }

    private static long SqliteLookupList(SqliteConnection cn, ShapeSpec shape, int operation, int items)
    {
        long total = 0;
        int baseOrdinal = (operation * KeyListWidth) % items;
        for (int i = 0; i < KeyListWidth; i++)
        {
            int ordinal = (baseOrdinal + (i * 7919)) % items;
            total += SqliteRange(cn, shape, ordinal, ordinal, "identities");
        }

        return total;
    }

    private static long SqliteRange(SqliteConnection cn, ShapeSpec shape, int lowerOrdinal, int upperOrdinal, string mode)
    {
        using SqliteCommand cmd = cn.CreateCommand();
        cmd.CommandText = mode switch
        {
            "identities" => "SELECT id FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
            "keys" => "SELECT k FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
            "pairs" => "SELECT k, id FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
            _ => throw new NotSupportedException(mode)
        };
        SqliteParameter key = cmd.Parameters.Add("$k", shape.KeyWidth == 8 ? SqliteType.Integer : SqliteType.Blob);
        SqliteParameter upper = cmd.Parameters.Add("$u", shape.KeyWidth == 8 ? SqliteType.Integer : SqliteType.Blob);
        SetSqliteKeyParam(shape, key, lowerOrdinal);
        SetSqliteKeyParam(shape, upper, upperOrdinal);
        long count = 0;
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            count++;
            if (mode == "identities")
            {
                ConsumeSqliteValue(reader, 0);
            }
            else if (mode == "keys")
            {
                ConsumeSqliteValue(reader, 0);
            }
            else
            {
                ConsumeSqliteValue(reader, 0);
                ConsumeSqliteValue(reader, 1);
            }
        }

        return count;
    }

    private static long SqlitePrefix(SqliteConnection cn, ShapeSpec shape, int ordinal, string mode)
    {
        GetPrefixBounds(shape, ordinal, out byte[] lower, out byte[] upper);
        using SqliteCommand cmd = cn.CreateCommand();
        cmd.CommandText = mode switch
        {
            "identities" => "SELECT id FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
            "keys" => "SELECT k FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
            "pairs" => "SELECT k, id FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
            "count" => "SELECT COUNT(*) FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
            _ => throw new NotSupportedException(mode)
        };
        SqliteParameter key = cmd.Parameters.Add("$k", SqliteType.Blob);
        SqliteParameter upperParam = cmd.Parameters.Add("$u", SqliteType.Blob);
        key.Value = lower;
        upperParam.Value = upper;
        if (mode == "count")
        {
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        long count = 0;
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            count++;
            if (mode == "identities")
            {
                ConsumeSqliteValue(reader, 0);
            }
            else if (mode == "keys")
            {
                ConsumeSqliteValue(reader, 0);
            }
            else
            {
                ConsumeSqliteValue(reader, 0);
                ConsumeSqliteValue(reader, 1);
            }
        }

        return count;
    }

    private static long SqliteCount(SqliteConnection cn, ShapeSpec shape, int lowerOrdinal, int upperOrdinal)
    {
        using SqliteCommand cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;";
        SqliteParameter key = cmd.Parameters.Add("$k", shape.KeyWidth == 8 ? SqliteType.Integer : SqliteType.Blob);
        SqliteParameter upper = cmd.Parameters.Add("$u", shape.KeyWidth == 8 ? SqliteType.Integer : SqliteType.Blob);
        SetSqliteKeyParam(shape, key, lowerOrdinal);
        SetSqliteKeyParam(shape, upper, upperOrdinal);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long SqliteCountAll(SqliteConnection cn)
    {
        using SqliteCommand cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ix INDEXED BY ix_k_id;";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static MeasureResult RunWorkers(int threads, int operations, Func<int, long> operation)
    {
        int workers = Math.Max(1, threads);
        long resultItems = 0;
        Thread[] active = new Thread[workers];
        for (int t = 0; t < workers; t++)
        {
            int worker = t;
            active[t] = new Thread(() =>
            {
                long local = 0;
                for (int i = worker; i < operations; i += workers)
                {
                    local += operation(i);
                }

                Interlocked.Add(ref resultItems, local);
            });
            active[t].Start();
        }

        for (int i = 0; i < active.Length; i++) active[i].Join();
        return new MeasureResult(operations, resultItems);
    }

    /// <summary>
    /// Separates first-session count latency from stable same-thread count throughput.<br/>
    /// One first call is timed independently, then the same caller invokes the discrete count primitive repeatedly for at least the configured hot window without creating an OS thread per operation.<br/>
    /// The returned elapsed ticks cover only the hot loop so first-call latency remains a distinct diagnostic rather than contaminating throughput.<br/>
    /// </summary>
    /// <param name="threads">The requested caller count; canonical cross-engine count throughput currently requires one caller.<br/></param>
    /// <param name="operation">The shape-native or SQLite count operation.<br/></param>
    /// <returns>The hot-loop operation count, stable logical cardinality, first-call latency note, and exact hot elapsed ticks.<br/></returns>
    private static MeasureResult RunCountAllHotThroughput(int threads, Func<long> operation)
    {
        if (threads != 1)
        {
            throw new NotSupportedException("Threaded count-all requires a persistent-worker scaling contract and is not part of the T1 cross-engine read campaign.");
        }

        long firstStarted = Stopwatch.GetTimestamp();
        long logicalResultItems = operation();
        long firstElapsedTicks = Math.Max(1, Stopwatch.GetTimestamp() - firstStarted);
        long minimumHotTicks = checked((long)(Stopwatch.Frequency * (CountAllHotMeasurementMilliseconds / 1000D)));
        long hotStarted = Stopwatch.GetTimestamp();
        long hotElapsedTicks;
        long operations = 0;
        do
        {
            for (int i = 0; i < CountAllHotClockCheckInterval; i++)
            {
                long current = operation();
                if (current != logicalResultItems)
                {
                    throw new InvalidDataException($"Count-all changed during hot measurement: first={logicalResultItems}, current={current}.");
                }

                operations++;
            }

            hotElapsedTicks = Stopwatch.GetTimestamp() - hotStarted;
        }
        while (hotElapsedTicks < minimumHotTicks);

        double firstMilliseconds = firstElapsedTicks * 1000D / Stopwatch.Frequency;
        double hotMilliseconds = hotElapsedTicks * 1000D / Stopwatch.Frequency;
        string notes = FormattableString.Invariant(
            $"Count-all contract: same-thread hot throughput; count-first-ms={firstMilliseconds:F6}; hot-ms={hotMilliseconds:F3}; hot-operations={operations}; logical-count={logicalResultItems}.");
        return new MeasureResult(
            operations,
            logicalResultItems,
            notes,
            ElapsedTimestampTicks: hotElapsedTicks);
    }

    /// <summary>
    /// Converts one adaptive count-all wave into the logical index cardinality that should be compared across engines.<br/>
    /// Count-all timing may intentionally run extra waves when noise is high, so summing every sampled count would make engines with different retry counts look cardinality-incompatible even when both return the same count.<br/>
    /// </summary>
    /// <param name="current">The previously observed logical count, or a negative value before the first wave.<br/></param>
    /// <param name="waveResultItems">The sum of count results returned by all workers in one wave.<br/></param>
    /// <param name="workers">The number of count callers used in the wave.<br/></param>
    /// <returns>The stable logical count for one count-all operation.<br/></returns>
    private static long MergeCountAllLogicalResultItems(long current, long waveResultItems, int workers)
    {
        if (workers <= 0)
        {
            throw new InvalidOperationException("Count-all adaptive sampling requires at least one worker.");
        }

        if (waveResultItems % workers != 0)
        {
            throw new InvalidDataException(
                $"Count-all sample wave returned non-uniform worker results: total={waveResultItems}, workers={workers}.");
        }

        long logical = waveResultItems / workers;
        if (current >= 0 && current != logical)
        {
            throw new InvalidDataException(
                $"Count-all sample waves returned inconsistent logical counts: first={current}, current={logical}.");
        }

        return logical;
    }

    /// <summary>
    /// Runs one count-all sample wave and records its wall-clock time.<br/>
    /// The wave uses the same worker distribution helper as other read workloads so LibraDex and SQLite receive the same caller concurrency shape.<br/>
    /// </summary>
    /// <param name="workers">The number of concurrent count callers in this wave.<br/></param>
    /// <param name="operation">The worker-indexed count operation.<br/></param>
    /// <param name="waveMilliseconds">The destination for measured wave time in milliseconds.<br/></param>
    /// <returns>The sum of count results returned by every worker in the wave.<br/></returns>
    private static long RunCountAllSampleWave(int workers, Func<int, long> operation, List<double> waveMilliseconds)
    {
        Stopwatch sw = Stopwatch.StartNew();
        MeasureResult measured = RunWorkers(workers, workers, operation);
        sw.Stop();
        waveMilliseconds.Add(sw.Elapsed.TotalMilliseconds);
        return measured.ResultItems;
    }

    /// <summary>
    /// Calculates the best-to-worst percentage spread for adaptive sample timings.<br/>
    /// A zero or single-sample set has no spread and does not trigger retry sampling.<br/>
    /// </summary>
    /// <param name="values">The sample timings to compare.<br/></param>
    /// <returns>The best-to-worst spread as a percentage of the fastest sample.<br/></returns>
    private static double GetSpreadPercent(List<double> values)
    {
        if (values.Count <= 1)
        {
            return 0D;
        }

        double min = Min(values);
        if (min <= 0D)
        {
            return 0D;
        }

        return ((Max(values) - min) / min) * 100D;
    }

    /// <summary>
    /// Returns the arithmetic mean for adaptive sample timings.<br/>
    /// The helper is intentionally allocation-free beyond the caller-owned list because it runs inside benchmark measurement reporting.<br/>
    /// </summary>
    /// <param name="values">The sampled timing values.<br/></param>
    /// <returns>The average timing value, or zero for an empty set.<br/></returns>
    private static double Average(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0D;
        }

        double total = 0D;
        for (int i = 0; i < values.Count; i++) total += values[i];
        return total / values.Count;
    }

    /// <summary>
    /// Returns the smallest timing value in an adaptive sample set.<br/>
    /// Empty sets return zero so spread calculation can remain defensive if a caller changes sample policy later.<br/>
    /// </summary>
    /// <param name="values">The sampled timing values.<br/></param>
    /// <returns>The minimum sampled value, or zero for an empty set.<br/></returns>
    private static double Min(List<double> values)
    {
        double min = double.MaxValue;
        for (int i = 0; i < values.Count; i++) if (values[i] < min) min = values[i];
        return min == double.MaxValue ? 0D : min;
    }

    /// <summary>
    /// Returns the largest timing value in an adaptive sample set.<br/>
    /// Empty sets return zero so notes and spread calculations have deterministic fallback values.<br/>
    /// </summary>
    /// <param name="values">The sampled timing values.<br/></param>
    /// <returns>The maximum sampled value, or zero for an empty set.<br/></returns>
    private static double Max(List<double> values)
    {
        double max = 0D;
        for (int i = 0; i < values.Count; i++) if (values[i] > max) max = values[i];
        return max;
    }

    private static void SetSqliteParams(ShapeSpec shape, SqliteParameter key, SqliteParameter id, int ordinal)
    {
        SetSqliteKeyParam(shape, key, ordinal);
        if (shape.IdentityWidth == 8)
        {
            id.SqliteType = SqliteType.Integer;
            id.Value = unchecked((long)Identity8(ordinal));
        }
        else
        {
            id.SqliteType = SqliteType.Blob;
            id.Value = shape.IdentityWidth == 16 ? GuidBytes(IdentityGuid(ordinal)) : IdentityBytes(ordinal, 24);
        }
    }

    private static void SetSqliteKeyParam(ShapeSpec shape, SqliteParameter key, int ordinal)
    {
        if (shape.KeyWidth == 8)
        {
            key.SqliteType = SqliteType.Integer;
            key.Value = unchecked((long)Key8(ordinal));
        }
        else
        {
            key.SqliteType = SqliteType.Blob;
            key.Value = shape.KeyWidth == 16 ? GuidBytes(KeyGuid(ordinal)) : KeyBytes(ordinal, shape.KeyWidth < 0 ? -shape.KeyWidth : shape.KeyWidth);
        }
    }

    private static long ConsumeVarIdentityBuffer(LibraDexVarIdentityBuffer buffer)
    {
        long count = buffer.Count;
        _ = buffer.PayloadLength;
        return count;
    }

    private static long ConsumeVarKeyBuffer(LibraDexVarKeyBuffer buffer)
    {
        long count = buffer.Count;
        _ = buffer.PayloadLength;
        return count;
    }

    private static void ConsumeSqliteValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetFieldType(ordinal) == typeof(long))
        {
            _ = reader.GetInt64(ordinal);
            return;
        }

        _ = ((byte[])reader.GetValue(ordinal)).Length;
    }

    private static int GetOperationCount(WorkloadSpec workload, int items)
    {
        return workload.Id switch
        {
            "lookup-one-identities" => Math.Min(items, PointLookupOperations),
            "lookup-list-identities" => Math.Min(items, LookupListOperations),
            "range-identities" or "range-keys" or "range-pairs" => Math.Min(items, RangeReadOperations),
            "prefix-identities" or "prefix-keys" or "prefix-pairs" => Math.Min(items, PrefixReadOperations),
            "count-range-api" => Math.Min(items, RangeCountOperations),
            "count-prefix-api" => Math.Min(items, PrefixCountOperations),
            "count-all-api" => 1,
            _ => items
        };
    }

    private static int PickOrdinal(int operation, int items)
    {
        return (int)(((long)operation * 48271L) % items);
    }

    private static int RangeUpper(int lower, int items)
    {
        return Math.Min(items - 1, lower + RangeWidth - 1);
    }

    private static int[] BuildSortedOrder(int items)
    {
        int[] order = new int[items];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        return order;
    }

    private static int[] BuildRandomOrder(int items)
    {
        int[] order = BuildSortedOrder(items);
        ShuffleOrder(order, 0x9E37_79B9U);
        return order;
    }

    /// <summary>
    /// Builds the caller-owned write partitions before timing begins.<br/>
    /// Ordinary writes receive one sorted or shuffled partition, while explicit concurrency diagnostics receive equal fixed worker partitions so no worker can monopolize a shared claim queue.<br/>
    /// </summary>
    /// <param name="workload">The selected benchmark workload and optional concurrency locality.<br/></param>
    /// <param name="items">The total number of unique benchmark tuples.<br/></param>
    /// <param name="threads">The requested number of caller threads.<br/></param>
    /// <returns>One deterministic ordinal array per worker.<br/></returns>
    private static int[][] BuildWriteOrders(WorkloadSpec workload, int items, int threads, int batchSize)
    {
        if (workload.ConcurrencyLocality.Length == 0)
        {
            return [IsRandomInsert(workload) ? BuildRandomOrder(items) : BuildSortedOrder(items)];
        }

        int workers = workload.ConcurrencyPlanWorkers > 0
            ? workload.ConcurrencyPlanWorkers
            : Math.Max(1, threads);
        List<int>[] assigned = new List<int>[workers];
        int capacity = (items + workers - 1) / workers;
        for (int worker = 0; worker < workers; worker++)
        {
            assigned[worker] = new List<int>(capacity);
        }

        if (workload.ConcurrencyLocality == "disjoint")
        {
            int baseGroup = 0;
            for (int worker = 0; worker < workers; worker++)
            {
                int count = items / workers + (worker < items % workers ? 1 : 0);
                int groupCount = (count + Scalar8RootPrefixGroupSize - 1) / Scalar8RootPrefixGroupSize;
                for (int i = 0; i < count; i++)
                {
                    assigned[worker].Add(checked((baseGroup * Scalar8RootPrefixGroupSize) + i));
                }

                baseGroup += groupCount;
            }

            if (baseGroup > 256)
            {
                throw new InvalidDataException($"Disjoint concurrency plan requires {baseGroup} scalar root-prefix groups; the encoded scalar plan supports at most 256.");
            }
        }
        else if (workload.ConcurrencyLocality == "overlap")
        {
            for (int ordinal = 0; ordinal < items; ordinal++)
            {
                assigned[ordinal % workers].Add(ordinal);
            }
        }
        else if (workload.ConcurrencyLocality == "mixed")
        {
            int[] targetCounts = new int[workers];
            int[] sharedCounts = new int[workers];
            int privateBaseGroup = 0;
            for (int worker = 0; worker < workers; worker++)
            {
                int target = items / workers + (worker < items % workers ? 1 : 0);
                int privateCount = target * 3 / 4;
                targetCounts[worker] = target;
                sharedCounts[worker] = target - privateCount;
                int privateGroupCount = (privateCount + Scalar8RootPrefixGroupSize - 1) / Scalar8RootPrefixGroupSize;
                for (int i = 0; i < privateCount; i++)
                {
                    assigned[worker].Add(checked((privateBaseGroup * Scalar8RootPrefixGroupSize) + i));
                }

                privateBaseGroup += privateGroupCount;
            }

            int sharedCursor = privateBaseGroup * Scalar8RootPrefixGroupSize;
            bool remaining = true;
            while (remaining)
            {
                remaining = false;
                for (int worker = 0; worker < workers; worker++)
                {
                    if (sharedCounts[worker] == 0)
                    {
                        continue;
                    }

                    assigned[worker].Add(sharedCursor++);
                    sharedCounts[worker]--;
                    remaining = true;
                }
            }

            int plannedItems = assigned.Sum(static worker => worker.Count);
            if (plannedItems != items)
            {
                throw new InvalidDataException($"Mixed concurrency plan assigned {plannedItems} of {items} ordinals.");
            }

            int finalGroupCount = (sharedCursor + Scalar8RootPrefixGroupSize - 1) / Scalar8RootPrefixGroupSize;
            if (finalGroupCount > 256)
            {
                throw new InvalidDataException($"Mixed concurrency plan requires {finalGroupCount} scalar root-prefix groups; the encoded scalar plan supports at most 256.");
            }

            for (int worker = 0; worker < workers; worker++)
            {
                if (assigned[worker].Count != targetCounts[worker])
                {
                    throw new InvalidDataException($"Mixed concurrency worker {worker} received {assigned[worker].Count} of {targetCounts[worker]} ordinals.");
                }
            }
        }
        else
        {
            throw new NotSupportedException($"Unknown concurrency locality '{workload.ConcurrencyLocality}'.");
        }

        int[][] orders = new int[workers][];
        for (int worker = 0; worker < workers; worker++)
        {
            orders[worker] = assigned[worker].ToArray();
            ShuffleOrder(orders[worker], unchecked(0x9E37_79B9U + (uint)(worker * 0x85EB_CA6B)));
        }

        if (workload.ConcurrencyPlanWorkers > 0)
        {
            return [FlattenWorkerPlan(orders, batchSize)];
        }

        return orders;
    }

    /// <summary>
    /// Replays a planned multi-caller tuple order on one thread while preserving each caller's chunk boundaries and caller-local order.<br/>
    /// Chunks are interleaved in deterministic caller order so the serial control mirrors the concurrent submission plan without measuring overlap.<br/>
    /// </summary>
    /// <param name="orders">The prebuilt caller-local tuple orders.<br/></param>
    /// <param name="batchSize">The caller batch boundary used by the concurrent scenario.<br/></param>
    /// <returns>One serial tuple order containing every planned tuple exactly once.<br/></returns>
    private static int[] FlattenWorkerPlan(int[][] orders, int batchSize)
    {
        int total = orders.Sum(static order => order.Length);
        int[] flattened = new int[total];
        int write = 0;
        for (int chunkStart = 0; ; chunkStart += batchSize)
        {
            bool copied = false;
            for (int worker = 0; worker < orders.Length; worker++)
            {
                int[] order = orders[worker];
                if (chunkStart >= order.Length)
                {
                    continue;
                }

                int count = Math.Min(batchSize, order.Length - chunkStart);
                order.AsSpan(chunkStart, count).CopyTo(flattened.AsSpan(write));
                write += count;
                copied = true;
            }

            if (!copied)
            {
                break;
            }
        }

        if (write != total)
        {
            throw new InvalidDataException($"Serial concurrency-plan replay copied {write} of {total} tuples.");
        }

        return flattened;
    }

    /// <summary>
    /// Measures the final physical shelf and immediate-parent-router domains touched by each planned caller.<br/>
    /// The route walk runs after the timed write interval, so locality proof does not contaminate throughput while still validating the topology produced by the measured tuple plan.<br/>
    /// </summary>
    /// <param name="index">The populated LibraDex index.<br/></param>
    /// <param name="shape">The fixed-scalar shape whose typed route walker should be used.<br/></param>
    /// <param name="orders">The caller-local tuple orders used by the measured scenario.<br/></param>
    /// <returns>Physical-domain sharing counts and item percentages for the final topology.<br/></returns>
    private static PhysicalLocalityMetrics MeasurePhysicalLocality(
        IDisposable index,
        ShapeSpec shape,
        int[][] orders)
    {
        if (orders.Length == 0)
        {
            return default;
        }

        HashSet<long>[] workerShelves = new HashSet<long>[orders.Length];
        HashSet<long>[] workerParents = new HashSet<long>[orders.Length];
        Dictionary<long, HashSet<int>> shelfWorkers = [];
        Dictionary<long, HashSet<int>> parentWorkers = [];
        Dictionary<long, long> shelfItems = [];
        Dictionary<long, long> parentItems = [];
        long totalItems = 0;
        for (int worker = 0; worker < orders.Length; worker++)
        {
            HashSet<long> shelves = [];
            HashSet<long> parents = [];
            workerShelves[worker] = shelves;
            workerParents[worker] = parents;
            int[] order = orders[worker];
            for (int i = 0; i < order.Length; i++)
            {
                PhysicalRouteDomain domain = GetPhysicalRouteDomain(index, shape, order[i]);
                if (domain.ShelfOffset <= 0 || domain.ParentRouterOffset <= 0)
                {
                    throw new InvalidDataException(
                        $"Physical locality route was incomplete for shape={shape.Id}, worker={worker}, ordinal={order[i]}, shelf={domain.ShelfOffset}, parent={domain.ParentRouterOffset}.");
                }

                shelves.Add(domain.ShelfOffset);
                parents.Add(domain.ParentRouterOffset);
                AddDomainWorker(shelfWorkers, domain.ShelfOffset, worker);
                AddDomainWorker(parentWorkers, domain.ParentRouterOffset, worker);
                AddDomainItem(shelfItems, domain.ShelfOffset);
                AddDomainItem(parentItems, domain.ParentRouterOffset);
                totalItems++;
            }
        }

        GetWorkerDomainRange(workerShelves, out int minimumShelves, out int maximumShelves);
        GetWorkerDomainRange(workerParents, out int minimumParents, out int maximumParents);
        GetSharedDomainMetrics(shelfWorkers, shelfItems, totalItems, out int sharedShelves, out int maximumWorkersPerShelf, out double sharedShelfItemPercent);
        GetSharedDomainMetrics(parentWorkers, parentItems, totalItems, out int sharedParents, out int maximumWorkersPerParent, out double sharedParentItemPercent);
        return new PhysicalLocalityMetrics(
            shelfWorkers.Count,
            minimumShelves,
            maximumShelves,
            sharedShelves,
            maximumWorkersPerShelf,
            sharedShelfItemPercent,
            parentWorkers.Count,
            minimumParents,
            maximumParents,
            sharedParents,
            maximumWorkersPerParent,
            sharedParentItemPercent);
    }

    private static PhysicalRouteDomain GetPhysicalRouteDomain(IDisposable index, ShapeSpec shape, int ordinal)
    {
        switch (shape.Id)
        {
            case "ss8-8":
            {
                LibraDexIndex<ulong, ulong> typed = (LibraDexIndex<ulong, ulong>)index;
                Scalar8Scalar8RoutePathTarget path = typed.Session.WalkScalar8Scalar8RoutePathTarget(
                    typed.RootRouterOffset,
                    typed.EncodeKey8(Key8(ordinal)),
                    maxRouterHops: 8,
                    readPolicy: Scalar8Scalar8RouteReadPolicy.PreferArenaCache);
                return new PhysicalRouteDomain(path.Target.Offset, path.ParentRouterOffset);
            }
            case "ss16-8":
            {
                LibraDexIndex<Guid, ulong> typed = (LibraDexIndex<Guid, ulong>)index;
                LibraDexGenericScalarCodec<Guid>.Encode16(KeyGuid(ordinal), out ulong high, out ulong low);
                Scalar16Scalar8RoutePathTarget path = typed.Session.WalkScalar16Scalar8RoutePathTarget(
                    typed.RootRouterOffset,
                    high,
                    low,
                    maxRouterHops: 8);
                return new PhysicalRouteDomain(path.Target.Offset, path.ParentRouterOffset);
            }
            case "ss8-16":
            {
                LibraDexIndex<ulong, Guid> typed = (LibraDexIndex<ulong, Guid>)index;
                Scalar8Scalar16RoutePathTarget path = typed.Session.WalkScalar8Scalar16RoutePathTarget(
                    typed.RootRouterOffset,
                    typed.EncodeKey8(Key8(ordinal)),
                    maxRouterHops: 8);
                return new PhysicalRouteDomain(path.Target.Offset, path.ParentRouterOffset);
            }
            case "ss16-16":
            {
                LibraDexIndex<Guid, Guid> typed = (LibraDexIndex<Guid, Guid>)index;
                LibraDexGenericScalarCodec<Guid>.Encode16(KeyGuid(ordinal), out ulong high, out ulong low);
                Scalar16Scalar16RoutePathTarget path = typed.Session.WalkScalar16Scalar16RoutePathTarget(
                    typed.RootRouterOffset,
                    high,
                    low,
                    maxRouterHops: 8);
                return new PhysicalRouteDomain(path.Target.Offset, path.ParentRouterOffset);
            }
            case "fs32-8":
            {
                LibraDexIndex<byte[], ulong> typed = (LibraDexIndex<byte[], ulong>)index;
                LibraDexGenericScalarCodec<byte[]>.Encode32(KeyBytes(ordinal, 32), out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                Fixed32Scalar8RoutePathTarget path = typed.Session.WalkFixed32Scalar8RoutePathTarget(
                    typed.RootRouterOffset,
                    key0,
                    key1,
                    key2,
                    key3,
                    maxRouterHops: 40);
                return new PhysicalRouteDomain(path.Target.Offset, path.ParentRouterOffset);
            }
            case "fs32-16":
            {
                LibraDexIndex<byte[], Guid> typed = (LibraDexIndex<byte[], Guid>)index;
                LibraDexGenericScalarCodec<byte[]>.Encode32(KeyBytes(ordinal, 32), out ulong key0, out ulong key1, out ulong key2, out ulong key3);
                Fixed32Scalar16RoutePathTarget path = typed.Session.WalkFixed32Scalar16RoutePathTarget(
                    typed.RootRouterOffset,
                    key0,
                    key1,
                    key2,
                    key3,
                    maxRouterHops: 40);
                return new PhysicalRouteDomain(path.Target.Offset, path.ParentRouterOffset);
            }
            default:
                return default;
        }
    }

    private static void AddDomainWorker(Dictionary<long, HashSet<int>> domains, long offset, int worker)
    {
        if (!domains.TryGetValue(offset, out HashSet<int>? workers))
        {
            workers = [];
            domains.Add(offset, workers);
        }

        workers.Add(worker);
    }

    private static void AddDomainItem(Dictionary<long, long> items, long offset)
    {
        items.TryGetValue(offset, out long count);
        items[offset] = count + 1;
    }

    private static void GetWorkerDomainRange(HashSet<long>[] domains, out int minimum, out int maximum)
    {
        minimum = int.MaxValue;
        maximum = 0;
        for (int i = 0; i < domains.Length; i++)
        {
            int count = domains[i].Count;
            minimum = Math.Min(minimum, count);
            maximum = Math.Max(maximum, count);
        }

        if (minimum == int.MaxValue)
        {
            minimum = 0;
        }
    }

    private static void GetSharedDomainMetrics(
        Dictionary<long, HashSet<int>> domainWorkers,
        Dictionary<long, long> domainItems,
        long totalItems,
        out int sharedDomainCount,
        out int maximumWorkers,
        out double sharedItemPercent)
    {
        sharedDomainCount = 0;
        maximumWorkers = 0;
        long sharedItems = 0;
        foreach (KeyValuePair<long, HashSet<int>> domain in domainWorkers)
        {
            int workerCount = domain.Value.Count;
            maximumWorkers = Math.Max(maximumWorkers, workerCount);
            if (workerCount <= 1)
            {
                continue;
            }

            sharedDomainCount++;
            sharedItems += domainItems[domain.Key];
        }

        sharedItemPercent = totalItems == 0 ? 0D : sharedItems * 100D / totalItems;
    }

    /// <summary>
    /// Shuffles a caller-owned ordinal array with a deterministic worker-specific sequence.<br/>
    /// The shuffle is completed before timing so allocation and setup do not affect measured write throughput.<br/>
    /// </summary>
    /// <param name="order">The ordinal array to shuffle in place.<br/></param>
    /// <param name="state">The non-cryptographic deterministic seed.<br/></param>
    private static void ShuffleOrder(int[] order, uint state)
    {
        for (int i = order.Length - 1; i > 0; i--)
        {
            state = unchecked((state * 1664525U) + 1013904223U);
            int j = (int)(state % (uint)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }
    }

    private static IEnumerable<RunScenario> EnumerateScenarios(string[] args, int items)
    {
        string shapeFilter = GetString(args, "--shape", "");
        string workloadFilter = GetString(args, "--workload", GetString(args, "--test", ""));
        int batchFilter = GetInt(args, "--batch", 0);
        int threadFilter = GetInt(args, "--threads", 0);
        foreach (ShapeSpec shape in Shapes)
        {
            if (shapeFilter.Length != 0 && !StringComparer.OrdinalIgnoreCase.Equals(shape.Id, shapeFilter)) continue;
            foreach (int batch in DefaultBatchSizes)
            {
                if (batchFilter != 0 && batch != batchFilter) continue;
                foreach (WorkloadSpec workload in Workloads)
                {
                    if (workloadFilter.Length != 0 && !StringComparer.OrdinalIgnoreCase.Equals(workload.Id, workloadFilter)) continue;
                    if (!SupportsWorkload(shape, workload)) continue;
                    if (workload.WritePath == "concurrent-writer" && batch != 1000) continue;
                    foreach (int threads in DefaultThreads)
                    {
                        if (threadFilter != 0 && threadFilter != threads) continue;
                        if (workload.IsWrite &&
                            workload.WritePath is not ("concurrent-writer" or "concurrent-batch") &&
                            threads != 1)
                        {
                            continue;
                        }

                        if (workload.WritePath is "concurrent-writer" or "concurrent-batch")
                        {
                            if (workload.ConcurrencyLocality.Length == 0 && threads != 1) continue;
                            if (workload.ConcurrencyPlanWorkers > 0 && threads != 1) continue;
                            if (workload.ConcurrencyLocality.Length != 0 &&
                                workload.ConcurrencyPlanWorkers == 0 &&
                                threads == 1)
                            {
                                continue;
                            }
                        }

                        yield return new RunScenario(shape, workload, batch, threads, items);
                    }
                }
            }
        }
    }

    private static bool SupportsWorkload(ShapeSpec shape, WorkloadSpec workload)
    {
        if (workload.WritePath == "concurrent-writer")
        {
            return shape.Id is "ss8-8" or "ss16-8" or "ss8-16" or "ss16-16" or "fs32-8" or "fs32-16";
        }

        if (workload.WritePath == "concurrent-batch")
        {
            return shape.Id == "ss8-8";
        }

        if (workload.Id == "count-all-api")
        {
            return shape.Id is "ss8-8" or "ss16-8" or "ss8-16" or "ss16-16" or "fs32-8" or "fs32-16" or "sv8" or "sv16" or "vs8" or "vs16" or "vv";
        }

        if (workload.Id == "count-range-api")
        {
            return shape.Id is "ss8-8" or "ss16-8" or "ss8-16" or "ss16-16" or "fs32-8" or "fs32-16" or "sv8" or "sv16" or "vs8" or "vs16" or "vv";
        }

        if (workload.Id == "count-prefix-api")
        {
            return shape.Id is "fs32-8" or "fs32-16" or "vs8" or "vs16" or "vv";
        }

        if (!workload.Id.Contains("prefix", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return shape.KeyWidth == 32 || shape.KeyWidth < 0;
    }

    private static ShapeSpec FindShape(string id)
    {
        foreach (ShapeSpec shape in Shapes)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(shape.Id, id)) return shape;
        }

        throw new ArgumentException($"Unknown shape '{id}'.");
    }

    private static WorkloadSpec FindWorkload(string id)
    {
        foreach (WorkloadSpec workload in Workloads)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(workload.Id, id)) return workload;
        }

        throw new ArgumentException($"Unknown workload '{id}'.");
    }

    private static ulong Key8(int ordinal)
    {
        ulong prefix = (ulong)(ordinal / Scalar8RootPrefixGroupSize);
        ulong local = (ulong)(ordinal % Scalar8RootPrefixGroupSize) + 1UL;
        return (prefix << 56) | local;
    }

    private static ulong Identity8(int ordinal) => (ulong)ordinal + 10_000_000UL;

    private static Guid KeyGuid(int ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, OrderedWideKeyHigh(ordinal));
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Key8(ordinal));
        return new Guid(bytes);
    }

    private static Guid IdentityGuid(int ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, 0x4000_0000_0000_0000UL | OrderedWideKeyHigh(ordinal));
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Identity8(ordinal));
        return new Guid(bytes);
    }

    private static byte[] KeyBytes(int ordinal, int length)
    {
        byte[] bytes = new byte[length];
        uint group = (uint)(ordinal / PrefixGroupSize);
        uint local = (uint)(ordinal % PrefixGroupSize);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), group);
        if (length >= 8)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), local);
        }

        ulong ordered = (ulong)ordinal + 1UL;
        int offset = 8;
        while (offset + sizeof(ulong) <= length - sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset, sizeof(ulong)), ordered);
            ordered = unchecked((ordered * 0x9E37_79B9_7F4A_7C15UL) + 0xBF58_476D_1CE4_E5B9UL);
            offset += sizeof(ulong);
        }

        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(length - 8), Key8(ordinal));
        return bytes;
    }

    private static byte[] IdentityBytes(int ordinal, int length)
    {
        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), 0x4000_0000_0000_0000UL | OrderedWideKeyHigh(ordinal));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(length - 8), Identity8(ordinal));
        return bytes;
    }

    private static void GetPrefixBounds(ShapeSpec shape, int ordinal, out byte[] lower, out byte[] upper)
    {
        int width = shape.KeyWidth < 0 ? -shape.KeyWidth : shape.KeyWidth;
        byte[] source = KeyBytes(ordinal, width);
        lower = new byte[width];
        upper = new byte[width];
        int prefix = Math.Min(PrefixByteLength, width);
        source.AsSpan(0, prefix).CopyTo(lower);
        source.AsSpan(0, prefix).CopyTo(upper);
        upper.AsSpan(prefix).Fill(0xFF);
    }

    /// <summary>
    /// Encodes the benchmark's logical key-prefix bytes into LibraDex's variable-key physical prefix contract.<br/>
    /// SQLite compares the raw logical prefix as a covering-index range; LibraDex variable-key prefix counting must add the value sentinel so routing observes the same physical order used by inserts and range readers.<br/>
    /// </summary>
    /// <param name="shape">The variable-key benchmark shape being counted.<br/></param>
    /// <param name="ordinal">The deterministic benchmark ordinal used to choose the prefix group.<br/></param>
    /// <returns>The encoded prefix to pass to shape-native variable-key prefix-count APIs.<br/></returns>
    private static byte[] GetEncodedVarKeyPrefix(ShapeSpec shape, int ordinal)
    {
        int width = shape.KeyWidth < 0 ? -shape.KeyWidth : shape.KeyWidth;
        byte[] source = KeyBytes(ordinal, width);
        int prefix = Math.Min(PrefixByteLength, width);
        return LibraDexVarLenKeyCodec.Encode(source.AsSpan(0, prefix), maxPhysicalLength: 64, nameof(ordinal));
    }

    /// <summary>
    /// Produces the ordered high lane used by 16-byte benchmark keys.<br/>
    /// The high lane must be monotonic by benchmark ordinal because range workloads construct lower and upper keys from ascending ordinals.<br/>
    /// Keep this independent from <see cref="Key8(int)"/> because that value deliberately encodes a root-prefix byte and sparse local lane; shifting it can discard ordering bits at group boundaries.<br/>
    /// </summary>
    private static ulong OrderedWideKeyHigh(int ordinal) => (ulong)ordinal + 1UL;

    private static byte[] GuidBytes(Guid value)
    {
        byte[] bytes = new byte[16];
        value.TryWriteBytes(bytes);
        return bytes;
    }

    private static void SplitGuid(Guid value, out ulong high, out ulong low)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
    }

    private static long GetDirectoryBytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) total += new FileInfo(file).Length;
        return total;
    }

    private static DirectoryActivity GetDirectoryActivity(string path)
    {
        if (!Directory.Exists(path))
        {
            return default;
        }

        long total = 0;
        long newestTicks = 0;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                FileInfo info = new(file);
                if (!info.Exists)
                {
                    continue;
                }

                total += info.Length;
                long ticks = info.LastWriteTimeUtc.Ticks;
                if (ticks > newestTicks)
                {
                    newestTicks = ticks;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new DirectoryActivity(total, newestTicks);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
    }

    private static double ToMiB(long bytes) => bytes / 1024D / 1024D;

    private static HashSet<string> ReadCompletedScenarioKeys(string csvPath)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(csvPath)) return keys;
        using StreamReader reader = new(csvPath, Encoding.UTF8);
        string header = reader.ReadLine() ?? "";
        Dictionary<string, int> columns = GetCsvColumnMap(header);
        while (reader.ReadLine() is { } line)
        {
            string[] cols = SplitCsvLine(line);
            keys.Add(GetScenarioKey(
                GetCsvColumn(cols, columns, "shelf_shape"),
                GetCsvColumn(cols, columns, "workload"),
                ParseInt(GetCsvColumn(cols, columns, "batch_size")),
                ParseInt(GetCsvColumn(cols, columns, "threads")),
                ParseInt(GetCsvColumn(cols, columns, "dataset_items")),
                GetCsvColumn(cols, columns, "dataset_kind"),
                ParseInt(GetCsvColumn(cols, columns, "scenario_schema_version")),
                GetCsvColumn(cols, columns, "binary_sha256")));
        }

        return keys;
    }

    private static int GetNextResultId(string csvPath)
    {
        if (!File.Exists(csvPath)) return 1;
        int max = 0;
        using StreamReader reader = new(csvPath, Encoding.UTF8);
        _ = reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            string[] cols = SplitCsvLine(line);
            if (cols.Length > 0 && int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > max) max = id;
        }

        return max + 1;
    }

    private static string GetScenarioKey(
        string shape,
        string workload,
        int batchSize,
        int threads,
        int datasetItems,
        string datasetKind,
        int scenarioSchemaVersion,
        string binarySha256)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{shape}|{workload}|{batchSize}|{threads}|{datasetItems}|{datasetKind}|{scenarioSchemaVersion}|{binarySha256}");
    }

    private static string JoinNotes(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return right;
        if (string.IsNullOrWhiteSpace(right)) return left;
        return left + " " + right;
    }

    /// <summary>
    /// Reads one invariant floating-point metric embedded as `name=value` in a child result note.<br/>
    /// Aggregate campaign rows use this only for separately timed diagnostics that are intentionally excluded from the primary operations-per-second window.<br/>
    /// </summary>
    /// <param name="notes">The child result note text.<br/></param>
    /// <param name="name">The exact metric name preceding the equals sign.<br/></param>
    /// <returns>The parsed metric value.<br/></returns>
    private static double ReadNoteMetric(string notes, string name)
    {
        string marker = name + "=";
        int start = notes.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidDataException($"Child result note does not contain required metric '{name}'.");
        }

        start += marker.Length;
        int end = notes.IndexOfAny([';', ' '], start);
        if (end < 0)
        {
            end = notes.Length;
        }

        string value = notes[start..end].TrimEnd('.');
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            throw new InvalidDataException($"Child result metric '{name}' is not a valid invariant number: '{value}'.");
        }

        return parsed;
    }

    private static void WriteHtmlReport(string csvPath, string reportPath)
    {
        BenchReportRow[] rows = ReadReportRows(csvPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        string generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        StringBuilder html = new();
        html.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>LibraDex ShapeBench Report</title>
<style>
:root{--bg:#f5f6f8;--fg:#17191f;--muted:#616978;--line:#d8dce3;--band:#fff;--band2:#eef2f6;--good:#16703a;--bad:#b42318;--warn:#a16207;--accent:#0f766e}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.42 system-ui,Segoe UI,Arial,sans-serif}header{position:sticky;top:0;z-index:4;background:#fff;border-bottom:1px solid var(--line);padding:14px 18px}h1{font-size:21px;margin:0 0 6px}.meta{color:var(--muted);font-size:12px;display:flex;gap:16px;flex-wrap:wrap}main{padding:18px;max-width:1560px;margin:0 auto}.controls{display:flex;gap:8px;flex-wrap:wrap;margin-top:12px}.controls input,.controls select,button{height:34px;padding:6px 9px;border:1px solid var(--line);border-radius:6px;background:var(--band);color:var(--fg)}.controls input{min-width:240px}
.kpis{display:grid;grid-template-columns:repeat(6,minmax(145px,1fr));gap:10px;margin:18px 0}.kpi,.panel,.findings{background:var(--band);border:1px solid var(--line);border-radius:8px;overflow:hidden}.kpi{padding:12px}.label{font-size:12px;color:var(--muted)}.value{font-size:24px;font-weight:720}.sub{font-size:12px;color:var(--muted)}.grid2{display:grid;grid-template-columns:1fr 1fr;gap:12px}.findings{padding:14px;margin-bottom:12px}.findings h2,.panel h2{font-size:16px;margin:0 0 8px}.panel h2{padding:11px 12px;background:var(--band2);border-bottom:1px solid var(--line)}.provenance-warning{border:1px solid #f2c94c;background:#fff8db;color:#7a4b00;border-radius:8px;padding:11px 13px;margin:0 0 12px;font-weight:650}.scroll{overflow:auto}table{width:100%;border-collapse:collapse;white-space:nowrap}th,td{border-bottom:1px solid var(--line);padding:7px 8px;text-align:right}th{background:var(--band2);color:#3d4654;font-size:12px;text-transform:uppercase}td:first-child,th:first-child,.left{text-align:left}.good{color:var(--good);font-weight:720}.bad{color:var(--bad);font-weight:720}.warn{color:var(--warn);font-weight:720}.tiny{font-size:12px;color:var(--muted)}details{background:var(--band);border:1px solid var(--line);border-radius:8px;margin:10px 0;overflow:hidden}summary{cursor:pointer;padding:10px 12px;font-weight:700;background:var(--band2)}.detail{padding:0 12px 12px;overflow:auto}@media(max-width:1100px){.kpis,.grid2{grid-template-columns:1fr 1fr}}@media(max-width:650px){.kpis,.grid2{grid-template-columns:1fr}.controls input{width:100%}}
</style>
</head>
<body>
<header><h1>LibraDex ShapeBench Report</h1><div class="meta"><span id="rowCount"></span><span>generated: 
""");
        html.Append(EscapeHtml(generatedAt));
        html.Append("</span><span>source: ");
        html.Append(EscapeHtml(csvPath));
        html.Append("""
</span></div><div class="controls"><input id="search" placeholder="filter text"><select id="shape"></select><select id="workload"></select><select id="kind"></select><select id="batch"></select><select id="threads"></select><select id="dataset"></select><select id="campaign"></select><button id="reset">Reset</button></div></header>
<main>
<section id="provenance"></section>
<section class="kpis" id="kpis"></section>
<section class="findings"><h2>What This Run Says</h2><ol id="findings"></ol></section>
<section style="margin-top:12px"><h2>Concurrent Write Capability</h2><div class="tiny">Primary comparison: LibraDex concurrent callers against the matching one-thread LibraDex API baseline. SQLite is retained only as secondary single-writer context. Concurrent writes measure bounded threaded usage, cancellation, fairness, and storage-safe publication; logical conflicts from overlapping keys remain caller-owned unless an explicit coordination API is used.</div><div class="kpis" id="concurrencyKpis"></div></section>
<section class="panel"><h2>Concurrent Write Detail</h2><div class="scroll"><table id="concurrency"></table></div></section>
<section class="panel" style="margin-top:12px"><h2>Write Admission And Shelf Wait</h2><div class="tiny" style="padding:0 12px 10px">Counters are captured after measured workers finish. Queue wait excludes fast-path admissions; shelf wait replaces CPU spinning when another writer owns the same physical shelf.</div><div class="scroll"><table id="admission"></table></div></section>
<section class="panel" style="margin-top:12px"><h2>Concurrent Batch Latency</h2><div class="tiny" style="padding:0 12px 10px">End-to-end batch latency includes inserts, conflict-driven intermediate publication/retry, and final publication. Publish latency is only the explicit final Publish() call.</div><div class="scroll"><table id="batchLatency"></table></div></section>
<section class="grid2"><div class="panel"><h2>Shape Scoreboard</h2><div class="scroll"><table id="shapeScore"></table></div></div><div class="panel"><h2>Workload Scoreboard</h2><div class="scroll"><table id="workloadScore"></table></div></div></section>
<section class="grid2" style="margin-top:12px"><div class="panel"><h2>Biggest LibraDex Wins</h2><div class="scroll"><table id="wins"></table></div></div><div class="panel"><h2>Biggest Gaps vs SQLite</h2><div class="scroll"><table id="losses"></table></div></div></section>
<section style="margin-top:18px"><h2>Shape Drilldown</h2><div id="groups"></div></section>
<section style="margin-top:18px"><details><summary>Raw Rows</summary><div class="detail"><table id="rows"></table></div></details></section>
</main>
<script id="data" type="application/json">
""");
        html.Append(json);
        html.Append("""
</script>
<script>
const rows=JSON.parse(document.getElementById('data').textContent),$=id=>document.getElementById(id);
const esc=v=>String(v??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
const fmt=(n,d=1)=>Number(n||0).toLocaleString(undefined,{maximumFractionDigits:d});
const ratio=r=>r&&r.sqliteOperationsPerSecond>0?r.libradexOperationsPerSecond/r.sqliteOperationsPerSecond:0;
const itemRatio=r=>r&&r.sqliteItemsPerSecond>0?r.libradexItemsPerSecond/r.sqliteItemsPerSecond:0;
const cls=x=>x>=1?'good':x>=.75?'warn':'bad';
const verdict=x=>x>=2?'much faster':x>=1.1?'faster':x>=.9?'similar':x>=.5?'slower':'much slower';
function uniq(k){return [...new Set(rows.map(r=>r[k]))].sort((a,b)=>String(a).localeCompare(String(b),undefined,{numeric:true}))}
function fill(id,k,label){$(id).innerHTML='<option value="">'+label+'</option>'+uniq(k).map(v=>'<option>'+esc(v)+'</option>').join('')}
fill('shape','shelfShape','all shapes');fill('workload','workload','all workloads');fill('kind','workloadKind','all kinds');fill('batch','batchSize','all batches');fill('threads','threads','all threads');fill('dataset','datasetItems','all datasets');fill('campaign','campaignId','all campaigns');
function filtered(){const q=$('search').value.toLowerCase();return rows.filter(r=>(!$('shape').value||r.shelfShape==$('shape').value)&&(!$('workload').value||r.workload==$('workload').value)&&(!$('kind').value||r.workloadKind==$('kind').value)&&(!$('batch').value||String(r.batchSize)==$('batch').value)&&(!$('threads').value||String(r.threads)==$('threads').value)&&(!$('dataset').value||String(r.datasetItems)==$('dataset').value)&&(!$('campaign').value||r.campaignId==$('campaign').value)&&(!q||JSON.stringify(r).toLowerCase().includes(q)))}
function avg(a){return a.length?a.reduce((s,x)=>s+x,0)/a.length:0}function median(a){if(!a.length)return 0;const b=[...a].sort((x,y)=>x-y),m=b.length>>1;return b.length%2?b[m]:(b[m-1]+b[m])/2}
function group(rs,k){const m=new Map();for(const r of rs){const v=typeof k==='function'?k(r):r[k];if(!m.has(v))m.set(v,[]);m.get(v).push(r)}return [...m.entries()]}
function summary(rs){const a=rs.map(ratio);return{rows:rs.length,med:median(a),avg:avg(a),wins:rs.filter(r=>ratio(r)>=1).length,best:[...rs].sort((a,b)=>ratio(b)-ratio(a))[0],worst:[...rs].sort((a,b)=>ratio(a)-ratio(b))[0],disk:avg(rs.map(r=>r.libradexDiskMb)),sdisk:avg(rs.map(r=>r.sqliteDiskMb))}}
function kpi(l,v,s){return '<div class="kpi"><div class="label">'+esc(l)+'</div><div class="value">'+v+'</div><div class="sub">'+esc(s||'')+'</div></div>'}
function renderKpis(rs){const s=summary(rs);$('kpis').innerHTML=[kpi('Rows',fmt(s.rows,0),new Set(rs.map(r=>r.shelfShape)).size+' shapes'),kpi('Median op ratio','<span class="'+cls(s.med)+'">'+fmt(s.med,2)+'x</span>',verdict(s.med)+' than SQLite'),kpi('Wins',fmt(s.wins,0),fmt(s.rows?100*s.wins/s.rows:0,0)+'% of rows'),kpi('Best','<span class="good">'+fmt(ratio(s.best),1)+'x</span>',s.best?s.best.shelfShape+' '+s.best.workload:''),kpi('Worst','<span class="bad">'+fmt(ratio(s.worst),3)+'x</span>',s.worst?s.worst.shelfShape+' '+s.worst.workload:''),kpi('Avg disk',fmt(s.disk,1)+' MB','SQLite '+fmt(s.sdisk,1)+' MB')].join('');$('rowCount').textContent=fmt(rs.length,0)+' rows'}
function row(r){const x=ratio(r),ix=itemRatio(r),sha=r.binarySha256?r.binarySha256.slice(0,12):'unavailable';return '<tr><td>'+r.id+'</td><td class="left">'+esc(r.shelfShape)+'</td><td class="left">'+esc(r.workload)+'</td><td>'+r.batchSize+'</td><td>'+r.threads+'</td><td>'+fmt(r.operations,0)+'</td><td class="'+cls(x)+'">'+fmt(x,3)+'x</td><td>'+fmt(r.libradexOperationsPerSecond,0)+'</td><td>'+fmt(r.sqliteOperationsPerSecond,0)+'</td><td>'+fmt(r.libradexThreadAverageItemsPerSecond,0)+'</td><td>'+fmt(r.libradexThreadMaximumItemsPerSecond,0)+'</td><td>'+fmt(r.resultItems,0)+'</td><td class="'+cls(ix)+'">'+fmt(ix,3)+'x</td><td>'+fmt(r.libradexDiskMb,1)+' / '+fmt(r.sqliteDiskMb,1)+'</td><td>'+esc(r.datasetItems||'unavailable')+'</td><td>'+esc(r.datasetKind||'unavailable')+'</td><td>'+esc(r.scenarioSchemaVersion||'unavailable')+'</td><td class="left">'+esc(r.campaignId||'unavailable')+'</td><td class="left">'+esc(sha)+'</td><td class="left tiny">'+esc(r.measurementUtc||'unavailable')+'</td><td class="left tiny">'+esc(r.notes)+'</td></tr>'}
function table(rs){return '<thead><tr><th>id</th><th>shape</th><th>workload</th><th>batch</th><th>threads</th><th>ops</th><th>op ratio</th><th>Libra ops/s</th><th>SQLite ops/s</th><th>Libra avg thread</th><th>Libra best thread</th><th>items</th><th>item ratio</th><th>disk L/S</th><th>dataset</th><th>dataset kind</th><th>schema</th><th>campaign</th><th>binary</th><th>measured UTC</th><th>notes</th></tr></thead><tbody>'+rs.map(row).join('')+'</tbody>'}
function scoreRow(name,items){const s=summary(items);return '<tr><td class="left"><b>'+esc(name)+'</b><div class="tiny">'+items.length+' rows, '+fmt(items.length?100*s.wins/items.length:0,0)+'% wins</div></td><td class="'+cls(s.med)+'">'+fmt(s.med,2)+'x</td><td>'+fmt(s.avg,2)+'x</td><td>'+fmt(s.disk,1)+' / '+fmt(s.sdisk,1)+'</td><td class="left tiny">'+esc(verdict(s.med))+'</td></tr>'}
function renderScores(rs){$('shapeScore').innerHTML='<thead><tr><th>shape</th><th>median</th><th>avg</th><th>disk L/S</th><th>takeaway</th></tr></thead><tbody>'+group(rs,'shelfShape').sort((a,b)=>summary(b[1]).med-summary(a[1]).med).map(x=>scoreRow(x[0],x[1])).join('')+'</tbody>';$('workloadScore').innerHTML='<thead><tr><th>workload</th><th>median</th><th>avg</th><th>disk L/S</th><th>takeaway</th></tr></thead><tbody>'+group(rs,'workload').sort((a,b)=>summary(b[1]).med-summary(a[1]).med).map(x=>scoreRow(x[0],x[1])).join('')+'</tbody>'}
function compact(rs){return '<thead><tr><th>shape</th><th>workload</th><th>batch</th><th>threads</th><th>op ratio</th><th>Libra ops/s</th><th>SQLite ops/s</th></tr></thead><tbody>'+rs.map(r=>'<tr><td class="left">'+esc(r.shelfShape)+'</td><td class="left">'+esc(r.workload)+'</td><td>'+r.batchSize+'</td><td>'+r.threads+'</td><td class="'+cls(ratio(r))+'">'+fmt(ratio(r),3)+'x</td><td>'+fmt(r.libradexOperationsPerSecond,0)+'</td><td>'+fmt(r.sqliteOperationsPerSecond,0)+'</td></tr>').join('')+'</tbody>'}
const concurrencyPath=r=>r.workload.includes('concurrent-batch')?'batch':r.workload.includes('concurrent-writer')?'writer':'';
const concurrencyLocality=r=>r.concurrencyLocality||'legacy';
const isConcurrency=r=>!!concurrencyPath(r);
function noteMetric(r,k){const m=new RegExp('(?:^|[ ;])'+k+'=([0-9.]+)').exec(r.notes||'');return m?Number(m[1]):0}
function concurrencyBaseline(r){const p=concurrencyPath(r);return rows.find(x=>x.shelfShape===r.shelfShape&&x.batchSize===r.batchSize&&x.threads===1&&concurrencyPath(x)===p&&concurrencyLocality(x)===concurrencyLocality(r)&&x.concurrencyPlanWorkers===r.concurrencyPlanWorkers)}
function renderConcurrency(rs){const cr=rs.filter(r=>isConcurrency(r)&&r.libradexActiveThreads>0),details=cr.map(r=>{const one=concurrencyBaseline(r),single=one?.libradexOperationsPerSecond||0,aggregateRatio=single>0?r.libradexOperationsPerSecond/single:0,avgRatio=single>0?r.libradexThreadAverageItemsPerSecond/single:0,bestRatio=single>0?r.libradexThreadMaximumItemsPerSecond/single:0,fairness=r.libradexThreadMaximumItemsPerSecond>0?r.libradexThreadMinimumItemsPerSecond/r.libradexThreadMaximumItemsPerSecond:0;return{r,one,single,aggregateRatio,avgRatio,bestRatio,fairness}}),multi=details.filter(x=>x.r.threads>1),body=details.sort((a,b)=>a.r.shelfShape.localeCompare(b.r.shelfShape)||concurrencyPath(a.r).localeCompare(concurrencyPath(b.r))||a.r.batchSize-b.r.batchSize||a.r.threads-b.r.threads||concurrencyLocality(a.r).localeCompare(concurrencyLocality(b.r))).map(x=>{const r=x.r,p=concurrencyPath(r),batch=p==='writer'?'n/a':r.batchSize;return '<tr><td class="left">'+esc(r.shelfShape)+'</td><td>'+p+'</td><td>'+concurrencyLocality(r)+'</td><td>'+batch+'</td><td>'+r.threads+'</td><td>'+r.concurrencyPlanWorkers+'</td><td>'+fmt(x.single,0)+'</td><td>'+fmt(r.libradexOperationsPerSecond,0)+'</td><td class="'+cls(x.aggregateRatio)+'">'+fmt(x.aggregateRatio,3)+'x</td><td>'+fmt(r.libradexThreadAverageItemsPerSecond,0)+'</td><td class="'+cls(x.avgRatio)+'">'+fmt(x.avgRatio,3)+'x</td><td>'+fmt(r.libradexThreadMaximumItemsPerSecond,0)+'</td><td class="'+cls(x.bestRatio)+'">'+fmt(x.bestRatio,3)+'x</td><td>'+fmt(r.libradexThreadMinimumItemsPerSecond,0)+'</td><td class="'+cls(x.fairness)+'">'+fmt(100*x.fairness,1)+'%</td><td>'+r.libradexActiveThreads+' / '+r.threads+'</td><td>'+fmt(r.libradexThreadMinimumItems,0)+' / '+fmt(r.libradexThreadMaximumItems,0)+'</td><td>'+fmt(r.libradexPublications,0)+'</td><td>'+fmt(r.libradexItemsPerPublication,1)+'</td><td>'+fmt(r.libradexDistinctShelves,0)+'</td><td>'+fmt(r.libradexSharedShelves,0)+'</td><td>'+fmt(r.libradexMaximumWorkersPerShelf,0)+'</td><td>'+fmt(r.libradexSharedShelfItemPercent,1)+'%</td><td>'+fmt(r.libradexDistinctParentRouters,0)+'</td><td>'+fmt(r.libradexSharedParentRouters,0)+'</td><td>'+fmt(r.libradexMaximumWorkersPerParentRouter,0)+'</td><td>'+fmt(r.libradexSharedParentRouterItemPercent,1)+'%</td><td>'+fmt(r.sqliteOperationsPerSecond,0)+'</td></tr>'}).join('');$('concurrency').innerHTML='<thead><tr><th>shape</th><th>API</th><th>locality</th><th>batch</th><th>threads</th><th>plan workers</th><th>single baseline</th><th>aggregate</th><th>aggregate/single</th><th>avg thread</th><th>avg/single</th><th>best thread</th><th>best/single</th><th>slowest thread</th><th>rate fairness</th><th>active</th><th>items min/max</th><th>publications</th><th>items/publication</th><th>shelves</th><th>shared shelves</th><th>max workers/shelf</th><th>shared shelf items</th><th>parents</th><th>shared parents</th><th>max workers/parent</th><th>shared parent items</th><th>SQLite context</th></tr></thead><tbody>'+body+'</tbody>';const aggregate=median(multi.map(x=>x.aggregateRatio)),best=median(multi.map(x=>x.bestRatio)),fair=median(multi.map(x=>x.fairness)),active=multi.length?100*multi.filter(x=>x.r.libradexActiveThreads===x.r.threads).length/multi.length:0;$('concurrencyKpis').innerHTML=[kpi('Concurrent rows',fmt(multi.length,0),group(multi,x=>concurrencyLocality(x.r)).length+' locality modes'),kpi('Median aggregate/single','<span class="'+cls(aggregate)+'">'+fmt(aggregate,2)+'x</span>','total useful write progress'),kpi('Median best/single','<span class="'+cls(best)+'">'+fmt(best,2)+'x</span>','best caller vs one thread'),kpi('Median fairness','<span class="'+cls(fair)+'">'+fmt(100*fair,0)+'%</span>','slowest divided by fastest'),kpi('All callers active',fmt(active,0)+'%','rows with every requested writer progressing'),kpi('SQLite role','context','single-writer capability contrast')].join('')}
function renderAdmission(rs){const ar=rs.filter(r=>isConcurrency(r)&&r.notes.includes('admissionQueued=')),body=ar.sort((a,b)=>a.shelfShape.localeCompare(b.shelfShape)||concurrencyPath(a).localeCompare(concurrencyPath(b))||a.batchSize-b.batchSize||a.threads-b.threads||concurrencyLocality(a).localeCompare(concurrencyLocality(b))).map(r=>'<tr><td class="left">'+esc(r.shelfShape)+'</td><td>'+concurrencyPath(r)+'</td><td>'+concurrencyLocality(r)+'</td><td>'+(concurrencyPath(r)==='writer'?'n/a':r.batchSize)+'</td><td>'+r.threads+'</td><td>'+fmt(noteMetric(r,'admissionQueued'),0)+'</td><td>'+fmt(noteMetric(r,'admissionGranted'),0)+'</td><td>'+fmt(noteMetric(r,'admissionMaxPending'),0)+'</td><td>'+fmt(noteMetric(r,'admissionAvgWaitMs'),3)+'</td><td>'+fmt(noteMetric(r,'admissionMaxWaitMs'),3)+'</td><td>'+fmt(noteMetric(r,'admissionCanceled'),0)+'</td><td>'+fmt(noteMetric(r,'admissionTimedOut'),0)+'</td><td>'+fmt(noteMetric(r,'admissionRejected'),0)+'</td><td>'+fmt(noteMetric(r,'shelfWaits'),0)+'</td><td>'+fmt(noteMetric(r,'shelfWaitMs'),3)+'</td></tr>').join('');$('admission').innerHTML='<thead><tr><th>shape</th><th>API</th><th>locality</th><th>batch</th><th>threads</th><th>queued</th><th>granted</th><th>max pending</th><th>avg queue ms</th><th>max queue ms</th><th>canceled</th><th>timed out</th><th>rejected</th><th>shelf waits</th><th>shelf wait ms</th></tr></thead><tbody>'+body+'</tbody>'}
function renderBatchLatency(rs){const br=rs.filter(r=>r.libradexBatchSamples>0).sort((a,b)=>a.shelfShape.localeCompare(b.shelfShape)||a.batchSize-b.batchSize||a.threads-b.threads||concurrencyLocality(a).localeCompare(concurrencyLocality(b))),body=br.map(r=>'<tr><td class="left">'+esc(r.shelfShape)+'</td><td>'+concurrencyLocality(r)+'</td><td>'+r.batchSize+'</td><td>'+r.threads+'</td><td>'+r.libradexBatchSamples+'</td><td>'+fmt(r.libradexBatchLatencyP50Milliseconds,3)+'</td><td>'+fmt(r.libradexBatchLatencyP95Milliseconds,3)+'</td><td>'+fmt(r.libradexBatchLatencyP99Milliseconds,3)+'</td><td>'+fmt(r.libradexBatchLatencyMaximumMilliseconds,3)+'</td><td>'+fmt(r.libradexPublishLatencyP50Milliseconds,3)+'</td><td>'+fmt(r.libradexPublishLatencyP95Milliseconds,3)+'</td><td>'+fmt(r.libradexPublishLatencyP99Milliseconds,3)+'</td><td>'+fmt(r.libradexConflictedBatches,0)+'</td><td>'+fmt(r.libradexConflictedBatchLatencyP95Milliseconds,3)+'</td><td>'+fmt(r.libradexUnconflictedBatchLatencyP95Milliseconds,3)+'</td></tr>').join('');$('batchLatency').innerHTML='<thead><tr><th>shape</th><th>locality</th><th>batch</th><th>threads</th><th>samples</th><th>batch p50 ms</th><th>batch p95 ms</th><th>batch p99 ms</th><th>batch max ms</th><th>publish p50 ms</th><th>publish p95 ms</th><th>publish p99 ms</th><th>conflicted batches</th><th>conflicted p95 ms</th><th>unconflicted p95 ms</th></tr></thead><tbody>'+body+'</tbody>'}
function renderProvenance(rs){const missing=rs.some(r=>!r.datasetItems||!r.datasetKind||!r.scenarioSchemaVersion||!r.campaignId||!r.binarySha256),datasets=new Set(rs.map(r=>(r.datasetItems||'unavailable')+'|'+(r.datasetKind||'unavailable'))),schemas=new Set(rs.map(r=>r.scenarioSchemaVersion||'unavailable')),binaries=new Set(rs.map(r=>r.binarySha256||'unavailable')),campaigns=new Set(rs.map(r=>r.campaignId||'unavailable')),mixed=datasets.size>1||schemas.size>1||binaries.size>1||campaigns.size>1,warnings=[];if(missing)warnings.push('Some rows predate row-level provenance; unavailable values must not be interpreted as measured defaults.');if(mixed)warnings.push('Mixed provenance detected: dataset, schema, campaign, or binary identity differs across rows. Filter before comparing or merging them.');const body=group(rs,r=>(r.datasetItems||'unavailable')+'|'+(r.datasetKind||'unavailable')+'|'+(r.scenarioSchemaVersion||'unavailable')+'|'+(r.campaignId||'unavailable')+'|'+(r.binarySha256||'unavailable')+'|'+(r.machineName||'unavailable')+'|'+(r.runtimeVersion||'unavailable')).map(([,items])=>{const r=items[0],sha=r.binarySha256?r.binarySha256.slice(0,16):'unavailable';return '<tr><td>'+items.length+'</td><td>'+esc(r.datasetItems||'unavailable')+'</td><td>'+esc(r.datasetKind||'unavailable')+'</td><td>'+esc(r.scenarioSchemaVersion||'unavailable')+'</td><td class="left">'+esc(r.campaignId||'unavailable')+'</td><td class="left">'+esc(sha)+'</td><td class="left">'+esc(r.machineName||'unavailable')+'</td><td class="left">'+esc(r.runtimeVersion||'unavailable')+'</td></tr>'}).join('');$('provenance').innerHTML=warnings.map(x=>'<div class="provenance-warning">'+esc(x)+'</div>').join('')+'<section class="panel"><h2>Result Provenance</h2><div class="scroll"><table><thead><tr><th>rows</th><th>dataset items</th><th>dataset kind</th><th>schema</th><th>campaign</th><th>binary SHA-256</th><th>machine</th><th>runtime</th></tr></thead><tbody>'+body+'</tbody></table></div></section>'}
function renderFindings(rs){const s=summary(rs),sh=group(rs,'shelfShape').map(x=>({n:x[0],s:summary(x[1])})).sort((a,b)=>b.s.med-a.s.med),wk=group(rs,'workload').map(x=>({n:x[0],s:summary(x[1])})).sort((a,b)=>b.s.med-a.s.med);$('findings').innerHTML=['Overall median operation ratio is '+fmt(s.med,2)+'x, so LibraDex is '+verdict(s.med)+' than SQLite for the filtered index workloads.','Best shape is '+(sh[0]?.n||'n/a')+' at '+fmt(sh[0]?.s.med,2)+'x; weakest is '+(sh.at(-1)?.n||'n/a')+' at '+fmt(sh.at(-1)?.s.med,2)+'x.','Best workload is '+(wk[0]?.n||'n/a')+' at '+fmt(wk[0]?.s.med,2)+'x; weakest workload is '+(wk.at(-1)?.n||'n/a')+' at '+fmt(wk.at(-1)?.s.med,2)+'x.','Count rows compare count APIs; retrieval rows compare materialized row/key/identity reads.'].map(x=>'<li>'+esc(x)+'</li>').join('')}
function renderGroups(rs){$('groups').innerHTML=group(rs,'shelfShape').sort((a,b)=>a[0].localeCompare(b[0],undefined,{numeric:true})).map(([shape,items])=>'<details><summary>'+esc(shape)+' - '+items.length+' rows, median '+fmt(summary(items).med,2)+'x</summary><div class="detail">'+table(items)+'</div></details>').join('')}
function render(){const rs=filtered(),ranked=rs.filter(r=>!isConcurrency(r));renderProvenance(rows);renderKpis(ranked);renderFindings(ranked);renderScores(ranked);renderConcurrency(rs);renderAdmission(rs);renderBatchLatency(rs);$('wins').innerHTML=compact([...ranked].sort((a,b)=>ratio(b)-ratio(a)).slice(0,14));$('losses').innerHTML=compact([...ranked].sort((a,b)=>ratio(a)-ratio(b)).slice(0,14));renderGroups(rs);$('rows').innerHTML=table(rs)}
for(const el of [$('search'),$('shape'),$('workload'),$('kind'),$('batch'),$('threads'),$('dataset'),$('campaign')])el.addEventListener('input',render);$('reset').onclick=()=>{for(const id of ['search','shape','workload','kind','batch','threads','dataset','campaign'])$(id).value='';render()};render();
</script>
""");
        html.Append("<style>").Append(InteractiveTableCss).Append("</style><script>").Append(InteractiveTableScript).Append("</script></body></html>");
        File.WriteAllText(reportPath, html.ToString(), Encoding.UTF8);
    }

    private static BenchReportRow[] ReadReportRows(string csvPath)
    {
        List<BenchReportRow> rows = [];
        using StreamReader reader = new(csvPath, Encoding.UTF8);
        _ = reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] cols = SplitCsvLine(line);
            if (cols.Length < 17) continue;
            rows.Add(new BenchReportRow(
                ParseInt(cols[0]), cols[1], cols[2], cols[3], ParseInt(cols[4]), ParseInt(cols[5]), ParseLong(cols[6]),
                ParseDouble(cols[7]), ParseDouble(cols[8]), ParseLong(cols[9]), ParseDouble(cols[10]), ParseDouble(cols[11]),
                ParseDouble(cols[12]), ParseDouble(cols[13]), ParseDouble(cols[14]), ParseDouble(cols[15]), cols[16],
                ParseOptionalInt(cols, 17), ParseOptionalInt(cols, 18),
                ParseOptionalDouble(cols, 19), ParseOptionalDouble(cols, 20),
                ParseOptionalDouble(cols, 21), ParseOptionalDouble(cols, 22),
                ParseOptionalDouble(cols, 23), ParseOptionalDouble(cols, 24),
                ParseOptionalDouble(cols, 35), ParseOptionalDouble(cols, 36),
                ParseOptionalLong(cols, 25), ParseOptionalLong(cols, 26),
                ParseOptionalLong(cols, 27), ParseOptionalLong(cols, 28),
                ParseOptionalLong(cols, 29), ParseOptionalLong(cols, 30),
                ParseOptionalLong(cols, 31), ParseOptionalLong(cols, 32),
                ParseOptionalDouble(cols, 33), ParseOptionalDouble(cols, 34),
                cols.Length > 37 ? cols[37] : "",
                ParseOptionalInt(cols, 38),
                ParseOptionalInt(cols, 39), ParseOptionalInt(cols, 40), ParseOptionalInt(cols, 41),
                ParseOptionalInt(cols, 42), ParseOptionalInt(cols, 43), ParseOptionalDouble(cols, 44),
                ParseOptionalInt(cols, 45), ParseOptionalInt(cols, 46), ParseOptionalInt(cols, 47),
                ParseOptionalInt(cols, 48), ParseOptionalInt(cols, 49), ParseOptionalDouble(cols, 50),
                ParseOptionalInt(cols, 51), ParseOptionalString(cols, 52), ParseOptionalInt(cols, 53),
                ParseOptionalString(cols, 54), ParseOptionalString(cols, 55), ParseOptionalString(cols, 56),
                ParseOptionalString(cols, 57), ParseOptionalString(cols, 58),
                ParseOptionalInt(cols, 59),
                ParseOptionalDouble(cols, 60), ParseOptionalDouble(cols, 61), ParseOptionalDouble(cols, 62), ParseOptionalDouble(cols, 63),
                ParseOptionalDouble(cols, 64), ParseOptionalDouble(cols, 65), ParseOptionalDouble(cols, 66), ParseOptionalDouble(cols, 67),
                ParseOptionalInt(cols, 68),
                ParseOptionalDouble(cols, 69), ParseOptionalDouble(cols, 70), ParseOptionalDouble(cols, 71),
                ParseOptionalDouble(cols, 72), ParseOptionalDouble(cols, 73), ParseOptionalDouble(cols, 74)));
        }

        return rows.ToArray();
    }

    private static string[] SplitCsvLine(string line)
    {
        List<string> cols = [];
        StringBuilder col = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { col.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else col.Append(ch);
            }
            else if (ch == ',') { cols.Add(col.ToString()); col.Clear(); }
            else if (ch == '"') quoted = true;
            else col.Append(ch);
        }

        cols.Add(col.ToString());
        return cols.ToArray();
    }

    private static string EscapeHtml(string value)
    {
        return value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static int ParseInt(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static long ParseLong(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ParseDouble(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an optional integer report column while preserving compatibility with ShapeBench CSV files created before concurrency telemetry was added.<br/>
    /// Missing or blank columns return zero because non-concurrency rows have no thread-level measurement.<br/>
    /// </summary>
    /// <param name="columns">The parsed CSV columns for one benchmark row.<br/></param>
    /// <param name="index">The optional zero-based column index.<br/></param>
    /// <returns>The parsed integer value, or zero when the column is absent or blank.<br/></returns>
    private static int ParseOptionalInt(string[] columns, int index)
    {
        return index < columns.Length &&
            int.TryParse(columns[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }

    /// <summary>
    /// Parses an optional long report column while preserving compatibility with ShapeBench CSV files created before concurrency telemetry was added.<br/>
    /// Missing or blank columns return zero because non-concurrency rows have no thread-level measurement.<br/>
    /// </summary>
    /// <param name="columns">The parsed CSV columns for one benchmark row.<br/></param>
    /// <param name="index">The optional zero-based column index.<br/></param>
    /// <returns>The parsed long value, or zero when the column is absent or blank.<br/></returns>
    private static long ParseOptionalLong(string[] columns, int index)
    {
        return index < columns.Length &&
            long.TryParse(columns[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : 0;
    }

    /// <summary>
    /// Parses an optional floating-point report column while preserving compatibility with ShapeBench CSV files created before concurrency telemetry was added.<br/>
    /// Missing or blank columns return zero because non-concurrency rows have no thread-level measurement.<br/>
    /// </summary>
    /// <param name="columns">The parsed CSV columns for one benchmark row.<br/></param>
    /// <param name="index">The optional zero-based column index.<br/></param>
    /// <returns>The parsed floating-point value, or zero when the column is absent or blank.<br/></returns>
    private static double ParseOptionalDouble(string[] columns, int index)
    {
        return index < columns.Length &&
            double.TryParse(columns[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0D;
    }

    /// <summary>
    /// Reads an optional text report column without inventing provenance for legacy ShapeBench rows.<br/>
    /// Missing values remain blank so the HTML can label them unavailable and warn when historical and current rows are mixed.<br/>
    /// </summary>
    /// <param name="columns">The parsed CSV columns for one benchmark row.<br/></param>
    /// <param name="index">The optional zero-based column index.<br/></param>
    /// <returns>The stored text, or an empty string when the column is absent.<br/></returns>
    private static string ParseOptionalString(string[] columns, int index)
    {
        return index < columns.Length ? columns[index] : "";
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string GetString(string[] args, string name, string fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(args[i], name)) return args[i + 1];
        }

        return fallback;
    }

    private static int GetInt(string[] args, string name, int fallback)
    {
        string value = GetString(args, name, "");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Reads one optional signed 64-bit command-line value using invariant integer syntax.<br/>
    /// A missing or malformed value returns the supplied fallback so the caller retains ownership of semantic validation.<br/>
    /// </summary>
    /// <param name="args">The complete command-line token array.<br/></param>
    /// <param name="name">The option name whose following token contains the value.<br/></param>
    /// <param name="fallback">The value returned when the option is absent or cannot be parsed.<br/></param>
    /// <returns>The parsed 64-bit integer or <paramref name="fallback"/>.<br/></returns>
    private static long GetLong(string[] args, string name, long fallback)
    {
        string value = GetString(args, name, "");
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : fallback;
    }

    private static bool Has(string[] args, string name)
    {
        return args.Any(arg => StringComparer.OrdinalIgnoreCase.Equals(arg, name));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>
    /// Reopens one completed `SS8-8` concurrency workload and proves its exact tuple set against the deterministic benchmark corpus.<br/>
    /// The validation removes every observed key from an expected key-to-identity map, rejecting duplicate keys, substituted identities, unexpected tuples, and missing tuples even when final cardinality happens to match.<br/>
    /// A commutative two-word digest is reported only as durable evidence; correctness is decided by the exact map comparison rather than by hash equality.<br/>
    /// </summary>
    /// <param name="path">The completed LibraDex file path to reopen.<br/></param>
    /// <param name="orders">The exact deterministic worker-local ordinal plan used by the measured workload.<br/></param>
    /// <returns>A compact exact-parity and tuple-digest note for the scenario result.<br/></returns>
    private static string ValidateScalar8Scalar8ExactFinalState(string path, int[][] orders)
    {
        int items = orders.Sum(static order => order.Length);
        Dictionary<ulong, ulong> expected = new(items);
        ulong expectedXor = 0;
        ulong expectedSum = 0;
        ulong minimumKey = ulong.MaxValue;
        ulong maximumKey = ulong.MinValue;
        for (int worker = 0; worker < orders.Length; worker++)
        {
            int[] order = orders[worker];
            for (int x = 0; x < order.Length; x++)
            {
                int ordinal = order[x];
                ulong key = Key8(ordinal);
                ulong identity = Identity8(ordinal);
                expected.Add(key, identity);
                minimumKey = Math.Min(minimumKey, key);
                maximumKey = Math.Max(maximumKey, key);
                ulong pairHash = MixTupleDigest(key, identity);
                expectedXor ^= pairHash;
                expectedSum += pairHash;
            }
        }

        ulong actualXor = 0;
        ulong actualSum = 0;
        int actualCount = 0;
        using LibraDexIndex<ulong, ulong> reopened = Indexes.Open<ulong, ulong>(path);
        using LibraDexRangeReader<ulong, ulong> reader = reopened.OpenRangeReader(minimumKey, maximumKey);
        while (reader.TryReadNext(out ulong key, out ulong identity))
        {
            actualCount++;
            if (!expected.Remove(key, out ulong expectedIdentity) ||
                expectedIdentity != identity)
            {
                throw new InvalidDataException(
                    $"SS8-8 exact reopen parity failed at key {key}: observed identity {identity}, expected identity {expectedIdentity}.");
            }

            ulong pairHash = MixTupleDigest(key, identity);
            actualXor ^= pairHash;
            actualSum += pairHash;
        }

        if (actualCount != items ||
            expected.Count != 0 ||
            actualXor != expectedXor ||
            actualSum != expectedSum)
        {
            KeyValuePair<ulong, ulong> firstMissing = expected.FirstOrDefault();
            IReadOnlyList<ulong> pointIdentities = expected.Count == 0
                ? Array.Empty<ulong>()
                : reopened.GetIdentities(
                    reopened.Where.EqualTo(firstMissing.Key).EndCondition,
                    deduplication: IdentityDeduplication.Preserve);
            string pointIdentityText = pointIdentities.Count == 0
                ? "none"
                : string.Join(",", pointIdentities);
            throw new InvalidDataException(
                $"SS8-8 exact reopen parity failed: expected {items} tuples, range observed {actualCount}, count API {reopened.Count()}, missing {expected.Count}, first missing key/identity {firstMissing.Key}/{firstMissing.Value}, point identities {pointIdentityText}, expected digest {expectedXor:X16}-{expectedSum:X16}, actual digest {actualXor:X16}-{actualSum:X16}.");
        }

        return $"exactReopenParity=true; tupleDigest={actualXor:X16}-{actualSum:X16}";
    }

    /// <summary>
    /// Mixes one scalar key/identity tuple into a stable 64-bit value suitable for commutative diagnostic aggregation.<br/>
    /// This digest is not used as the correctness oracle; exact key-to-identity comparison is performed separately before the digest is accepted.<br/>
    /// </summary>
    /// <param name="key">The encoded scalar key.<br/></param>
    /// <param name="identity">The encoded scalar identity.<br/></param>
    /// <returns>A stable mixed value for the tuple.<br/></returns>
    private static ulong MixTupleDigest(ulong key, ulong identity)
    {
        ulong rotatedIdentity = (identity << 29) | (identity >> 35);
        ulong value = key ^ rotatedIdentity ^ 0x9E3779B97F4A7C15UL;
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ShapeBench commands:");
        Console.WriteLine("  list [--shape ss8-8] [--workload range-identities] [--batch 1000] [--threads 8] [--items 250000]");
        Console.WriteLine("  run-all --items 250000 --root artifacts\\shape-bench\\run --out artifacts\\shape-bench\\run\\results.csv --report artifacts\\shape-bench\\run\\report.html [--campaign-id run-name] [--dataset-kind canonical|diagnostic] [--resume]");
        Console.WriteLine("  run-reads --items 250000 --repetitions 3 --root artifacts\\shape-bench\\reads --out artifacts\\shape-bench\\reads\\results.csv --report artifacts\\shape-bench\\reads\\report.html [--resume]");
        Console.WriteLine("  run-scaling --items 250000 --root artifacts\\shape-bench\\scaling --out artifacts\\shape-bench\\scaling\\results.csv [--shape sv8] [--workload lookup-list-identities] [--batch 1000] [--repetitions 3]");
        Console.WriteLine("  run-one --engine libradex|sqlite --shape ss8-8 --workload range-identities --batch 1000 --threads 1 --items 250000 --root artifacts\\shape-bench\\one");
        Console.WriteLine("  report --in artifacts\\shape-bench\\run\\results.csv --report artifacts\\shape-bench\\run\\report.html");
        Console.WriteLine("  report-reads --in artifacts\\shape-bench\\reads\\results.csv --report artifacts\\shape-bench\\reads\\report.html [--expected-cells 291] [--repetitions 3]");
        Console.WriteLine("  diagnose-vs8-exact --path artifacts\\shape-bench\\run\\work\\vs8\\lookup-one-identities\\b5000\\t1\\libradex\\measured\\index.lbdx --items 250000 --samples 3");
        Console.WriteLine("Workloads:");
        foreach (WorkloadSpec workload in Workloads) Console.WriteLine($"  {workload.Id} ({workload.Kind}) - {workload.Description}");
    }

    /// <summary>
    /// Measures current range-based `VS8` exact reads against exact route selection on an existing ShapeBench index.<br/>
    /// The command keeps the persisted benchmark topology unchanged and reports logical route expansion, shelf decoding, allocation, elapsed time, and DataKernel reads for evenly spaced keys.<br/>
    /// </summary>
    /// <param name="args">Command arguments containing the existing index path, logical item count, and sample count.<br/></param>
    /// <returns>Zero when every sampled exact read returns one identity and both exact walkers select the same leaf.<br/></returns>
    private static int DiagnoseVs8Exact(string[] args)
    {
        string path = Path.GetFullPath(GetString(args, "--path", ""));
        int items = GetInt(args, "--items", DefaultItems);
        int samples = GetInt(args, "--samples", 3);
        if (!File.Exists(path))
        {
            return Fail($"The VS8 exact-read diagnostic index does not exist: {path}");
        }

        if (items <= 0 || samples <= 0)
        {
            return Fail("The VS8 exact-read diagnostic requires positive --items and --samples values.");
        }

        using VarKeyScalar8Index index = Indexes.VS8.Open(path, maxKeyLength: 64);
        _ = index.DiagnoseExactRead(KeyBytes(0, 24));
        Console.WriteLine("vs8 exact-read traversal proof");
        Console.WriteLine($"path {path}");
        Console.WriteLine($"items {items}");
        Console.WriteLine($"samples {samples}");
        Console.WriteLine("| ordinal | results | range ms | range alloc | routers | mb routers | route probes | shelves | slots decoded | logical bytes | DK reads | DK backing | DK bytes | exact routers | exact mb | exact route us | exact alloc | same target |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|");

        bool passed = true;
        for (int i = 0; i < samples; i++)
        {
            int ordinal = samples == 1
                ? items / 2
                : checked((int)(((long)i * (items - 1)) / (samples - 1)));
            VarKeyScalar8ExactReadProof proof = index.DiagnoseExactRead(KeyBytes(ordinal, 24));
            VarKeyScalar8RangeReadDiagnostics range = proof.RangeDiagnostics;
            VarKeyScalar8ExactRouteDiagnostics exact = proof.ExactRoute;
            long logicalBytes = checked(range.RouterBytesTouched + range.ShelfBytesTouched + range.TerminalBytesTouched);
            double rangeMs = proof.RangeTicks * 1000D / Stopwatch.Frequency;
            double exactUs = proof.ExactRouteTicks * 1_000_000D / Stopwatch.Frequency;
            Console.WriteLine(FormattableString.Invariant(
                $"| {ordinal} | {proof.ResultCount} | {rangeMs:F3} | {proof.RangeAllocatedBytes} | {range.RoutersVisited} | {range.MultiByteRoutersVisited} | {range.RouterRoutesExamined} | {range.ShelvesVisited} | {range.ShelfSlotsDecoded} | {logicalBytes} | {proof.RangeReadTelemetry.ReadCallCount} | {proof.RangeReadTelemetry.BackingReadCallCount} | {proof.RangeReadTelemetry.BytesRead} | {exact.RoutersVisited} | {exact.MultiByteRoutersVisited} | {exactUs:F3} | {proof.ExactRouteAllocatedBytes} | {proof.MatchesProductionRouteWalker} |"));
            passed &= proof.ResultCount == 1 && proof.MatchesProductionRouteWalker;
        }

        return passed ? 0 : 1;
    }

    /// <summary>
    /// Creates immutable provenance shared by every measured row written by one ShapeBench parent process.<br/>
    /// A full unfiltered 250,000-item matrix is labeled canonical by default; filtered or differently sized runs are labeled diagnostic unless the caller supplies an explicit dataset kind.<br/>
    /// The loaded ShapeBench assembly hash is captured once outside measured children so provenance work cannot perturb benchmark timing.<br/>
    /// </summary>
    /// <param name="args">The parent command arguments, including optional campaign and dataset-kind overrides.<br/></param>
    /// <param name="root">The resolved campaign root used to derive the default campaign identifier.<br/></param>
    /// <param name="items">The deterministic corpus item count represented by every scenario in this parent run.<br/></param>
    /// <returns>Stable campaign, dataset, binary, machine, and runtime provenance for CSV rows.<br/></returns>
    private static RunProvenance CreateRunProvenance(string[] args, string root, int items)
    {
        bool filtered = Has(args, "--shape") || Has(args, "--workload") || Has(args, "--batch") || Has(args, "--threads");
        string defaultDatasetKind = items == DefaultItems && !filtered ? "canonical" : "diagnostic";
        string datasetKind = GetString(args, "--dataset-kind", defaultDatasetKind).Trim();
        string campaignId = GetString(args, "--campaign-id", new DirectoryInfo(root).Name).Trim();
        if (datasetKind.Length == 0) throw new ArgumentException("ShapeBench dataset kind cannot be blank.");
        if (campaignId.Length == 0) throw new ArgumentException("ShapeBench campaign id cannot be blank.");

        string binaryPath = typeof(Program).Assembly.Location;
        if (binaryPath.Length == 0) throw new InvalidOperationException("Cannot resolve the loaded ShapeBench assembly path for provenance.");
        using FileStream binary = File.OpenRead(binaryPath);
        string binarySha256 = Convert.ToHexString(SHA256.HashData(binary));
        return new RunProvenance(
            datasetKind,
            campaignId,
            binarySha256,
            Environment.MachineName,
            ".NET " + Environment.Version.ToString());
    }

    /// <summary>
    /// Rejects resume into a CSV whose header cannot represent the current row schema.<br/>
    /// This prevents new rows from being appended beneath legacy 37-column or 51-column headers, which would silently shift or hide provenance fields.<br/>
    /// Legacy CSV remains readable by the report command and can be migrated separately through structured CSV handling.<br/>
    /// </summary>
    /// <param name="csvPath">The existing result CSV requested for resume.<br/></param>
    private static void ValidateResumeCsvSchema(string csvPath)
    {
        using StreamReader reader = new(csvPath, Encoding.UTF8);
        string header = (reader.ReadLine() ?? "").TrimStart('\uFEFF');
        if (!StringComparer.Ordinal.Equals(header, CsvHeader))
        {
            throw new InvalidDataException(
                $"Cannot resume '{csvPath}' because its CSV schema is not the current provenance-aware schema. " +
                "Generate a new result CSV or migrate the legacy rows before resuming.");
        }
    }

    /// <summary>
    /// Rejects resume into a read-campaign CSV whose columns do not match the aggregate repetition contract.<br/>
    /// Exact header matching prevents a partially compatible legacy report from accepting rows with shifted provenance or timing fields.<br/>
    /// </summary>
    /// <param name="csvPath">The existing read result CSV requested for resume.<br/></param>
    private static void ValidateReadResumeCsvSchema(string csvPath)
    {
        using StreamReader reader = new(csvPath, Encoding.UTF8);
        string header = (reader.ReadLine() ?? "").TrimStart('\uFEFF');
        if (!StringComparer.Ordinal.Equals(header, ReadCsvHeader))
        {
            throw new InvalidDataException(
                $"Cannot resume '{csvPath}' because its CSV schema is not the current aggregate read schema. Generate a new read campaign CSV instead.");
        }
    }

    /// <summary>
    /// Reads completed aggregate read cells so a resumed campaign skips only rows produced by the same corpus, repetition contract, and binary.<br/>
    /// Scenario identity deliberately excludes campaign label because the binary and deterministic corpus are the reproducibility boundary.<br/>
    /// </summary>
    /// <param name="csvPath">The existing aggregate read CSV.<br/></param>
    /// <returns>Case-sensitive stable keys for all structurally complete rows.<br/></returns>
    private static HashSet<string> ReadCompletedReadScenarioKeys(string csvPath)
    {
        using StreamReader reader = new(csvPath, Encoding.UTF8);
        string headerLine = (reader.ReadLine() ?? "").TrimStart('\uFEFF');
        Dictionary<string, int> columns = GetCsvColumnMap(headerLine);
        HashSet<string> completed = [];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] values = SplitCsvLine(line);
            completed.Add(GetReadScenarioKey(
                GetCsvColumn(values, columns, "shape"),
                GetCsvColumn(values, columns, "workload"),
                ParseInt(GetCsvColumn(values, columns, "batch_size")),
                ParseInt(GetCsvColumn(values, columns, "dataset_items")),
                ParseInt(GetCsvColumn(values, columns, "repetitions")),
                GetCsvColumn(values, columns, "binary_sha256")));
        }

        return completed;
    }

    /// <summary>
    /// Creates the stable resume key for one aggregate read cell.<br/>
    /// Including the full binary hash prevents results from different implementations from being merged during resume.<br/>
    /// </summary>
    /// <param name="shape">The persisted shelf-shape identifier.<br/></param>
    /// <param name="workload">The read workload identifier.<br/></param>
    /// <param name="batchSize">The deterministic corpus ingest batch size.<br/></param>
    /// <param name="datasetItems">The deterministic corpus size.<br/></param>
    /// <param name="repetitions">The number of isolated samples represented by the row.<br/></param>
    /// <param name="binarySha256">The full ShapeBench binary hash.<br/></param>
    /// <returns>A stable ordinal resume key.<br/></returns>
    private static string GetReadScenarioKey(
        string shape,
        string workload,
        int batchSize,
        int datasetItems,
        int repetitions,
        string binarySha256)
    {
        return string.Join('|', shape, workload, batchSize, datasetItems, repetitions, binarySha256);
    }

    /// <summary>
    /// Regenerates the compact canonical-read HTML report from every flushed aggregate CSV row.<br/>
    /// The report is replaced atomically and self-refreshes so an open browser follows campaign progress without reading an incomplete file.<br/>
    /// </summary>
    /// <param name="csvPath">The flushed aggregate read CSV.<br/></param>
    /// <param name="reportPath">The HTML report path to replace.<br/></param>
    /// <param name="expectedCells">The complete supported cell count after command filters.<br/></param>
    /// <param name="repetitions">The isolated sample count required per engine and cell.<br/></param>
    private static void WriteReadHtmlReport(string csvPath, string reportPath, int expectedCells, int repetitions)
    {
        string csvText;
        using (FileStream csvStream = new(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (StreamReader csvReader = new(csvStream, Encoding.UTF8))
        {
            csvText = csvReader.ReadToEnd();
        }

        string[] lines = csvText.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("The aggregate read CSV has no header.");
        }

        Dictionary<string, int> columns = GetCsvColumnMap(lines[0]);
        StringBuilder rows = new();
        List<double> ratios = [];
        for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            string[] values = SplitCsvLine(lines[lineIndex]);
            string ratioText = GetCsvColumn(values, columns, "libradex_over_sqlite");
            _ = double.TryParse(ratioText, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio);
            ratios.Add(ratio);
            string ratioClass = ratio >= 1D ? "win" : "loss";
            rows.Append("<tr><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "id")))
                .Append("</td><td class=left>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "shape")))
                .Append("</td><td class=left>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "workload")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "batch_size")))
                .Append("</td><td class=").Append(ratioClass).Append('>')
                .Append(System.Net.WebUtility.HtmlEncode(ratioText)).Append("x</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "libradex_mean_operations_per_second")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "sqlite_mean_operations_per_second")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "libradex_median_operations_per_second")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "sqlite_median_operations_per_second")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "libradex_minimum_operations_per_second")))
                .Append(" / ")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "libradex_maximum_operations_per_second")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "sqlite_minimum_operations_per_second")))
                .Append(" / ")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "sqlite_maximum_operations_per_second")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "libradex_spread_percent")))
                .Append("%</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "sqlite_spread_percent")))
                .Append("%</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "libradex_cold_operations_per_second")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "sqlite_cold_operations_per_second")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "libradex_first_count_latency_milliseconds")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "sqlite_first_count_latency_milliseconds")))
                .Append("</td><td>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "result_items")))
                .Append("</td><td class=tiny>")
                .Append(System.Net.WebUtility.HtmlEncode(GetCsvColumn(values, columns, "measurement_utc")))
                .Append("</td></tr>");
        }

        ratios.Sort();
        double medianRatio = ratios.Count == 0
            ? 0D
            : (ratios.Count & 1) != 0
                ? ratios[ratios.Count >> 1]
                : (ratios[(ratios.Count >> 1) - 1] + ratios[ratios.Count >> 1]) * 0.5D;
        int wins = ratios.Count(static value => value >= 1D);
        double completePercent = expectedCells == 0 ? 0D : ratios.Count * 100D / expectedCells;
        string html = $$"""
<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="refresh" content="60"><title>LibraDex canonical reads</title>
<style>body{font:14px system-ui;background:#10151d;color:#e8edf5;margin:24px}h1{margin-bottom:4px}.meta{color:#9eabc0;margin-bottom:18px}.kpis{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:20px}.kpi{background:#192231;border:1px solid #2b3a50;border-radius:8px;padding:12px 16px;min-width:150px}.v{font-size:24px;font-weight:700}table{border-collapse:collapse;width:100%;background:#151d29}th,td{padding:7px 9px;border-bottom:1px solid #293649;text-align:right}th{position:sticky;top:0;background:#202b3c}.left{text-align:left}.win{color:#6ee7a8;font-weight:700}.loss{color:#ff8f8f;font-weight:700}.tiny{font-size:11px;color:#aab5c6}</style></head><body>
<h1>LibraDex canonical single-thread reads</h1><div class="meta">Generated {{DateTime.UtcNow:O}}; HTML refreshes every 60 seconds. Each engine/cell uses one isolated process, one discarded cold pass, and {{repetitions}} measured warm passes in the same open session. Commands/readers are recreated per complete pass.</div>
<div class="kpis"><div class="kpi"><div>Progress</div><div class="v">{{ratios.Count}} / {{expectedCells}}</div><div>{{completePercent:0.0}}%</div></div><div class="kpi"><div>Median LibraDex/SQLite</div><div class="v">{{medianRatio:0.###}}x</div></div><div class="kpi"><div>LibraDex wins</div><div class="v">{{wins}} / {{ratios.Count}}</div></div></div>
<table><thead><tr><th>id</th><th class="left">shape</th><th class="left">workload</th><th>batch</th><th>ratio</th><th>Libra warm mean</th><th>SQLite warm mean</th><th>Libra median</th><th>SQLite median</th><th>Libra min / max</th><th>SQLite min / max</th><th>Libra spread</th><th>SQLite spread</th><th>Libra cold</th><th>SQLite cold</th><th>Libra first count ms</th><th>SQLite first count ms</th><th>results</th><th>measured UTC</th></tr></thead><tbody>{{rows}}</tbody></table>
<style>{{InteractiveTableCss}}</style><script>{{InteractiveTableScript}}</script></body></html>
""";
        string temporaryPath = reportPath + ".tmp";
        File.WriteAllText(temporaryPath, html, Encoding.UTF8);
        File.Move(temporaryPath, reportPath, overwrite: true);
    }

    /// <summary>
    /// Builds a case-insensitive name-to-position map from a ShapeBench CSV header.<br/>
    /// Header lookup keeps resume identity explicit and avoids coupling provenance fields to fragile magic indexes.<br/>
    /// </summary>
    /// <param name="header">The raw first CSV line, optionally beginning with a UTF-8 byte-order marker.<br/></param>
    /// <returns>A map containing every nonblank CSV column name and its zero-based position.<br/></returns>
    private static Dictionary<string, int> GetCsvColumnMap(string header)
    {
        string[] names = SplitCsvLine(header.TrimStart('\uFEFF'));
        Dictionary<string, int> columns = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Length > 0) columns[names[i]] = i;
        }

        return columns;
    }

    /// <summary>
    /// Reads one required CSV value by header name and fails with a provenance-specific error when the column or row value is absent.<br/>
    /// Resume never guesses legacy dataset or build identity because doing so could skip a materially different benchmark scenario.<br/>
    /// </summary>
    /// <param name="values">The parsed values for one result row.<br/></param>
    /// <param name="columns">The header-derived column-position map.<br/></param>
    /// <param name="name">The required column name.<br/></param>
    /// <returns>The exact stored column value.<br/></returns>
    private static string GetCsvColumn(string[] values, Dictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out int index) || index >= values.Length || values[index].Length == 0)
        {
            throw new InvalidDataException($"Cannot resume because result column '{name}' is missing or blank.");
        }

        return values[index];
    }

    private sealed record ShapeSpec(string Id, string Family, int KeyWidth, int IdentityWidth);

    private sealed record WorkloadSpec(
        string Id,
        string Kind,
        string Description,
        bool IsWrite,
        bool MaterializesRows,
        string WritePath = "",
        string ConcurrencyLocality = "",
        int ConcurrencyPlanWorkers = 0);

    private sealed record RunScenario(ShapeSpec Shape, WorkloadSpec Workload, int BatchSize, int Threads, int Items);

    private readonly record struct DirectoryActivity(long Bytes, long NewestWriteTicks);

    private readonly record struct MeasureResult(
        long Operations,
        long ResultItems,
        string Notes = "",
        ThreadWorkerMeasurement[]? Workers = null,
        long ElapsedTimestampTicks = 0,
        PhysicalLocalityMetrics Locality = default,
        BatchMetrics BatchMetrics = default);

    private readonly record struct WriteMeasurement(
        long Inserted,
        ThreadWorkerMeasurement[] Workers,
        long ElapsedTimestampTicks,
        string Notes = "",
        BatchMetrics BatchMetrics = default);

    private readonly record struct ConcurrentBatchAttributionMeasurement(
        long OwnershipConflicts,
        long ConflictPublications,
        long EmptyContextAborts,
        long TopologyFallbacks,
        long MaximumStagedMutations,
        long StagedMutationsBeforeConflict);

    private readonly record struct BatchTimingMeasurement(
        long BatchTimestampTicks,
        long PublishTimestampTicks,
        bool HadOwnershipConflict);

    private readonly record struct PhysicalRouteDomain(long ShelfOffset, long ParentRouterOffset);

    private readonly record struct PhysicalLocalityMetrics(
        int DistinctShelfCount,
        int MinimumShelvesPerWorker,
        int MaximumShelvesPerWorker,
        int SharedShelfCount,
        int MaximumWorkersPerShelf,
        double SharedShelfItemPercent,
        int DistinctParentRouterCount,
        int MinimumParentRoutersPerWorker,
        int MaximumParentRoutersPerWorker,
        int SharedParentRouterCount,
        int MaximumWorkersPerParentRouter,
        double SharedParentRouterItemPercent);

    private readonly record struct ThreadWorkerMeasurement(
        long Items,
        long Chunks,
        long Publications,
        long ActiveTimestampTicks);

    private readonly record struct ThreadMetrics(
        int ActiveThreadCount,
        double AverageItemsPerSecond,
        double MedianItemsPerSecond,
        double MaximumItemsPerSecond,
        double MinimumItemsPerSecond,
        long MinimumItems,
        long MaximumItems,
        long ChunkCount,
        long PublicationCount,
        double ItemsPerPublication)
    {
        /// <summary>
        /// Aggregates fixed-size worker counters after the benchmark stopwatch has stopped.<br/>
        /// Each rate spans the worker's first claimed chunk through completion of its last chunk, including coordination delays while excluding pre-work thread startup.<br/>
        /// </summary>
        /// <param name="workers">The per-worker counters and timestamps captured during measured execution.<br/></param>
        /// <returns>Thread-rate, work-distribution, chunk, and publication statistics for active workers.<br/></returns>
        internal static ThreadMetrics Create(ThreadWorkerMeasurement[]? workers)
        {
            if (workers is null)
            {
                return default;
            }

            ThreadWorkerMeasurement[] active = workers.Where(static worker => worker.Items > 0).ToArray();
            if (active.Length == 0)
            {
                return default;
            }

            double[] rates = new double[active.Length];
            long minimumItems = long.MaxValue;
            long maximumItems = 0;
            long chunks = 0;
            long publications = 0;
            for (int i = 0; i < active.Length; i++)
            {
                ThreadWorkerMeasurement worker = active[i];
                rates[i] = worker.ActiveTimestampTicks <= 0
                    ? 0D
                    : worker.Items * (double)Stopwatch.Frequency / worker.ActiveTimestampTicks;
                minimumItems = Math.Min(minimumItems, worker.Items);
                maximumItems = Math.Max(maximumItems, worker.Items);
                chunks += worker.Chunks;
                publications += worker.Publications;
            }

            Array.Sort(rates);
            double average = rates.Average();
            int middle = rates.Length / 2;
            double median = rates.Length % 2 == 0
                ? (rates[middle - 1] + rates[middle]) / 2D
                : rates[middle];
            return new ThreadMetrics(
                active.Length,
                average,
                median,
                rates[^1],
                rates[0],
                minimumItems,
                maximumItems,
                chunks,
                publications,
                publications == 0 ? 0D : active.Sum(static worker => worker.Items) / (double)publications);
        }
    }

    private readonly record struct BatchMetrics(
        int SampleCount,
        double BatchLatencyP50Milliseconds,
        double BatchLatencyP95Milliseconds,
        double BatchLatencyP99Milliseconds,
        double BatchLatencyMaximumMilliseconds,
        double PublishLatencyP50Milliseconds,
        double PublishLatencyP95Milliseconds,
        double PublishLatencyP99Milliseconds,
        double PublishLatencyMaximumMilliseconds,
        int ConflictedBatchCount,
        double ConflictedBatchLatencyP50Milliseconds,
        double ConflictedBatchLatencyP95Milliseconds,
        double ConflictedBatchLatencyP99Milliseconds,
        double UnconflictedBatchLatencyP50Milliseconds,
        double UnconflictedBatchLatencyP95Milliseconds,
        double UnconflictedBatchLatencyP99Milliseconds)
    {
        /// <summary>
        /// Aggregates caller-visible concurrent-batch service and final-publication latency after measured worker execution has ended.<br/>
        /// End-to-end batch latency includes inserts, conflict-driven intermediate publication/retry, and final publication; publish latency isolates only the explicit final <c>Publish()</c> call.<br/>
        /// Ownership-conflicted and unconflicted percentile cohorts remain separate so contention cost is visible without adding stopwatch work to LibraDex synchronization internals.<br/>
        /// </summary>
        /// <param name="workers">Exact-size per-worker timing arrays populated during measured concurrent-batch execution.<br/></param>
        /// <returns>Nearest-rank p50/p95/p99, maximum latency, and conflict cohort counts in milliseconds.<br/></returns>
        internal static BatchMetrics Create(BatchTimingMeasurement[][] workers)
        {
            int sampleCount = 0;
            for (int i = 0; i < workers.Length; i++) sampleCount = checked(sampleCount + workers[i].Length);
            if (sampleCount == 0) return default;

            double tickMilliseconds = 1000D / Stopwatch.Frequency;
            double[] batch = new double[sampleCount];
            double[] publish = new double[sampleCount];
            double[] conflicted = new double[sampleCount];
            double[] unconflicted = new double[sampleCount];
            int sample = 0;
            int conflictedCount = 0;
            int unconflictedCount = 0;
            for (int worker = 0; worker < workers.Length; worker++)
            {
                BatchTimingMeasurement[] workerTimings = workers[worker];
                for (int i = 0; i < workerTimings.Length; i++)
                {
                    BatchTimingMeasurement timing = workerTimings[i];
                    double batchMilliseconds = timing.BatchTimestampTicks * tickMilliseconds;
                    batch[sample] = batchMilliseconds;
                    publish[sample] = timing.PublishTimestampTicks * tickMilliseconds;
                    sample++;
                    if (timing.HadOwnershipConflict)
                    {
                        conflicted[conflictedCount++] = batchMilliseconds;
                    }
                    else
                    {
                        unconflicted[unconflictedCount++] = batchMilliseconds;
                    }
                }
            }

            Array.Sort(batch);
            Array.Sort(publish);
            Array.Sort(conflicted, 0, conflictedCount);
            Array.Sort(unconflicted, 0, unconflictedCount);
            return new BatchMetrics(
                sampleCount,
                Percentile(batch, sampleCount, 50),
                Percentile(batch, sampleCount, 95),
                Percentile(batch, sampleCount, 99),
                batch[^1],
                Percentile(publish, sampleCount, 50),
                Percentile(publish, sampleCount, 95),
                Percentile(publish, sampleCount, 99),
                publish[^1],
                conflictedCount,
                Percentile(conflicted, conflictedCount, 50),
                Percentile(conflicted, conflictedCount, 95),
                Percentile(conflicted, conflictedCount, 99),
                Percentile(unconflicted, unconflictedCount, 50),
                Percentile(unconflicted, unconflictedCount, 95),
                Percentile(unconflicted, unconflictedCount, 99));
        }

        /// <summary>
        /// Returns a nearest-rank percentile from an already sorted prefix without allocating or interpolating synthetic observations.<br/>
        /// Small diagnostic cohorts therefore report p99 as the observed maximum, preserving a truthful worst-sample anchor.<br/>
        /// </summary>
        /// <param name="sorted">The ascending values whose populated prefix is being summarized.<br/></param>
        /// <param name="count">The number of populated values beginning at index zero.<br/></param>
        /// <param name="percentile">The requested percentile from 1 through 100.<br/></param>
        /// <returns>The observed nearest-rank value, or zero for an empty cohort.<br/></returns>
        private static double Percentile(double[] sorted, int count, int percentile)
        {
            if (count <= 0) return 0D;
            int index = Math.Clamp((int)Math.Ceiling(count * percentile / 100D) - 1, 0, count - 1);
            return sorted[index];
        }
    }

    /// <summary>
    /// Owns one SQLite command for exactly one complete cold or warm workload pass.<br/>
    /// Rebinding parameters within the pass measures covering B-tree traversal without repeating SQL compilation for every logical operation.<br/>
    /// Disposing the pass finalizes its prepared statement so compiled command state never crosses cold or warm sample boundaries.<br/>
    /// </summary>
    private sealed class SqliteReadCommand : IDisposable
    {
        private readonly ShapeSpec shape;
        private readonly WorkloadSpec workload;
        private readonly SqliteCommand command;
        private readonly SqliteParameter? lower;
        private readonly SqliteParameter? upper;
        private readonly string mode;

        /// <summary>
        /// Creates the covering-index command used only by the current complete workload pass.<br/>
        /// Command construction remains inside pass timing, while connection-local page and schema caches remain owned by the longer measurement connection.<br/>
        /// </summary>
        /// <param name="connection">The already-open unpooled measurement connection.<br/></param>
        /// <param name="shape">The deterministic key and identity shape.<br/></param>
        /// <param name="workload">The exact read or count workload executed by this pass.<br/></param>
        public SqliteReadCommand(SqliteConnection connection, ShapeSpec shape, WorkloadSpec workload)
        {
            this.shape = shape;
            this.workload = workload;
            command = connection.CreateCommand();
            mode = workload.Id switch
            {
                "lookup-one-identities" or "lookup-list-identities" or "range-identities" or "prefix-identities" => "identities",
                "range-keys" or "prefix-keys" => "keys",
                "range-pairs" or "prefix-pairs" => "pairs",
                "count-range-api" or "count-prefix-api" or "count-all-api" => "count",
                _ => throw new NotSupportedException(workload.Id)
            };

            if (workload.Id == "count-all-api")
            {
                command.CommandText = "SELECT COUNT(*) FROM ix INDEXED BY ix_k_id;";
                return;
            }

            command.CommandText = mode switch
            {
                "identities" => "SELECT id FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
                "keys" => "SELECT k FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
                "pairs" => "SELECT k, id FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
                "count" => "SELECT COUNT(*) FROM ix INDEXED BY ix_k_id WHERE k >= $k AND k <= $u;",
                _ => throw new NotSupportedException(mode)
            };
            SqliteType keyType = workload.Id.StartsWith("prefix-", StringComparison.Ordinal)
                ? SqliteType.Blob
                : shape.KeyWidth == 8 ? SqliteType.Integer : SqliteType.Blob;
            lower = command.Parameters.Add("$k", keyType);
            upper = command.Parameters.Add("$u", keyType);
        }

        /// <summary>
        /// Executes one deterministic logical operation by rebinding the pass-owned covering-index command.<br/>
        /// Lookup-list operations intentionally execute all member point ranges through the same prepared command.<br/>
        /// </summary>
        /// <param name="operation">The zero-based deterministic operation ordinal.<br/></param>
        /// <param name="items">The corpus item count used to derive key ordinals.<br/></param>
        /// <returns>The number of materialized or counted index entries.<br/></returns>
        public long Execute(int operation, int items)
        {
            if (workload.Id == "lookup-list-identities")
            {
                long total = 0;
                int baseOrdinal = (operation * KeyListWidth) % items;
                for (int i = 0; i < KeyListWidth; i++)
                {
                    int ordinal = (baseOrdinal + (i * 7919)) % items;
                    BindRange(ordinal, ordinal);
                    total += ExecuteBound();
                }

                return total;
            }

            int lowerOrdinal = PickOrdinal(operation, items);
            if (workload.Id.StartsWith("prefix-", StringComparison.Ordinal) || workload.Id == "count-prefix-api")
            {
                GetPrefixBounds(shape, lowerOrdinal, out byte[] lowerBytes, out byte[] upperBytes);
                lower!.Value = lowerBytes;
                upper!.Value = upperBytes;
            }
            else
            {
                int upperOrdinal = workload.Id == "lookup-one-identities"
                    ? lowerOrdinal
                    : RangeUpper(lowerOrdinal, items);
                BindRange(lowerOrdinal, upperOrdinal);
            }

            return ExecuteBound();
        }

        /// <summary>
        /// Executes the pass-owned count-all statement for the existing hot-throughput timing loop.<br/>
        /// The command is recreated for every complete cold or warm pass and reused only within that pass.<br/>
        /// </summary>
        /// <returns>The current covering-index entry count.<br/></returns>
        public long ExecuteCountAll()
        {
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Finalizes the pass-owned SQLite statement at the cold or warm sample boundary.<br/>
        /// The surrounding measurement connection remains open so only connection-level B-tree cache state survives.<br/>
        /// </summary>
        public void Dispose()
        {
            command.Dispose();
        }

        /// <summary>
        /// Rebinds the logical inclusive key bounds on the pass-owned command without changing its compiled SQL.<br/>
        /// Shape-native integer or fixed-width BLOB encoding remains identical to corpus construction.<br/>
        /// </summary>
        /// <param name="lowerOrdinal">The inclusive lower logical key ordinal.<br/></param>
        /// <param name="upperOrdinal">The inclusive upper logical key ordinal.<br/></param>
        private void BindRange(int lowerOrdinal, int upperOrdinal)
        {
            SetSqliteKeyParam(shape, lower!, lowerOrdinal);
            SetSqliteKeyParam(shape, upper!, upperOrdinal);
        }

        /// <summary>
        /// Executes the currently bound covering-index query and consumes every projected value required by the workload.<br/>
        /// Count workloads return the scalar directly; retrieval workloads enumerate the complete reader before command reuse.<br/>
        /// </summary>
        /// <returns>The number of counted or materialized index entries.<br/></returns>
        private long ExecuteBound()
        {
            if (mode == "count")
            {
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            long count = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                count++;
                ConsumeSqliteValue(reader, 0);
                if (mode == "pairs") ConsumeSqliteValue(reader, 1);
            }

            return count;
        }
    }

    private sealed class ConnectionSet : IDisposable
    {
        public ConnectionSet(SqliteConnection[] connections)
        {
            Connections = connections;
        }

        public SqliteConnection[] Connections { get; }

        public void Dispose()
        {
            for (int i = 0; i < Connections.Length; i++)
            {
                Connections[i].Dispose();
            }
        }
    }

    private sealed record RunResult(
        string Engine,
        string Shape,
        string Workload,
        string WorkloadKind,
        int BatchSize,
        int Threads,
        string ConcurrencyLocality,
        int ConcurrencyPlanWorkers,
        string Pass,
        long Operations,
        double OperationsPerSecond,
        long ResultItems,
        double ItemsPerSecond,
        long RamBytes,
        long DiskBytes,
        string Notes,
        int ThreadActiveCount,
        double ThreadAverageItemsPerSecond,
        double ThreadMedianItemsPerSecond,
        double ThreadMaximumItemsPerSecond,
        double ThreadMinimumItemsPerSecond,
        long ThreadMinimumItems,
        long ThreadMaximumItems,
        long ThreadChunkCount,
        long PublicationCount,
        double ItemsPerPublication,
        int DistinctShelfCount,
        int MinimumShelvesPerWorker,
        int MaximumShelvesPerWorker,
        int SharedShelfCount,
        int MaximumWorkersPerShelf,
        double SharedShelfItemPercent,
        int DistinctParentRouterCount,
        int MinimumParentRoutersPerWorker,
        int MaximumParentRoutersPerWorker,
        int SharedParentRouterCount,
        int MaximumWorkersPerParentRouter,
        double SharedParentRouterItemPercent,
        int BatchSampleCount,
        double BatchLatencyP50Milliseconds,
        double BatchLatencyP95Milliseconds,
        double BatchLatencyP99Milliseconds,
        double BatchLatencyMaximumMilliseconds,
        double PublishLatencyP50Milliseconds,
        double PublishLatencyP95Milliseconds,
        double PublishLatencyP99Milliseconds,
        double PublishLatencyMaximumMilliseconds,
        int ConflictedBatchCount,
        double ConflictedBatchLatencyP50Milliseconds,
        double ConflictedBatchLatencyP95Milliseconds,
        double ConflictedBatchLatencyP99Milliseconds,
        double UnconflictedBatchLatencyP50Milliseconds,
        double UnconflictedBatchLatencyP95Milliseconds,
        double UnconflictedBatchLatencyP99Milliseconds,
        double ColdOperationsPerSecond = 0D,
        long ColdResultItems = 0,
        double[]? WarmOperationsPerSecondSamples = null,
        long[]? WarmOperationSamples = null,
        double[]? WarmFirstCountLatencyMillisecondsSamples = null);

    private sealed record BenchReportRow(
        int Id,
        string ShelfShape,
        string Workload,
        string WorkloadKind,
        int BatchSize,
        int Threads,
        long Operations,
        double LibradexOperationsPerSecond,
        double SqliteOperationsPerSecond,
        long ResultItems,
        double LibradexItemsPerSecond,
        double SqliteItemsPerSecond,
        double LibradexRamMb,
        double SqliteRamMb,
        double LibradexDiskMb,
        double SqliteDiskMb,
        string Notes,
        int LibradexActiveThreads,
        int SqliteActiveThreads,
        double LibradexThreadAverageItemsPerSecond,
        double SqliteThreadAverageItemsPerSecond,
        double LibradexThreadMedianItemsPerSecond,
        double SqliteThreadMedianItemsPerSecond,
        double LibradexThreadMaximumItemsPerSecond,
        double SqliteThreadMaximumItemsPerSecond,
        double LibradexThreadMinimumItemsPerSecond,
        double SqliteThreadMinimumItemsPerSecond,
        long LibradexThreadMinimumItems,
        long SqliteThreadMinimumItems,
        long LibradexThreadMaximumItems,
        long SqliteThreadMaximumItems,
        long LibradexChunks,
        long SqliteChunks,
        long LibradexPublications,
        long SqliteTransactions,
        double LibradexItemsPerPublication,
        double SqliteItemsPerTransaction,
        string ConcurrencyLocality,
        int ConcurrencyPlanWorkers,
        int LibradexDistinctShelves,
        int LibradexMinimumShelvesPerWorker,
        int LibradexMaximumShelvesPerWorker,
        int LibradexSharedShelves,
        int LibradexMaximumWorkersPerShelf,
        double LibradexSharedShelfItemPercent,
        int LibradexDistinctParentRouters,
        int LibradexMinimumParentRoutersPerWorker,
        int LibradexMaximumParentRoutersPerWorker,
        int LibradexSharedParentRouters,
        int LibradexMaximumWorkersPerParentRouter,
        double LibradexSharedParentRouterItemPercent,
        int DatasetItems,
        string DatasetKind,
        int ScenarioSchemaVersion,
        string MeasurementUtc,
        string CampaignId,
        string BinarySha256,
        string MachineName,
        string RuntimeVersion,
        int LibradexBatchSamples,
        double LibradexBatchLatencyP50Milliseconds,
        double LibradexBatchLatencyP95Milliseconds,
        double LibradexBatchLatencyP99Milliseconds,
        double LibradexBatchLatencyMaximumMilliseconds,
        double LibradexPublishLatencyP50Milliseconds,
        double LibradexPublishLatencyP95Milliseconds,
        double LibradexPublishLatencyP99Milliseconds,
        double LibradexPublishLatencyMaximumMilliseconds,
        int LibradexConflictedBatches,
        double LibradexConflictedBatchLatencyP50Milliseconds,
        double LibradexConflictedBatchLatencyP95Milliseconds,
        double LibradexConflictedBatchLatencyP99Milliseconds,
        double LibradexUnconflictedBatchLatencyP50Milliseconds,
        double LibradexUnconflictedBatchLatencyP95Milliseconds,
        double LibradexUnconflictedBatchLatencyP99Milliseconds);

    private sealed record RunProvenance(
        string DatasetKind,
        string CampaignId,
        string BinarySha256,
        string MachineName,
        string RuntimeVersion);
}
