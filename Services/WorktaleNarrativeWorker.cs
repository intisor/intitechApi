namespace IntitechApi.Services;

public class WorktaleNarrativeWorker : BackgroundService
{
    private readonly IWorktaleQueue _queue;
    private readonly IWorktaleStore _store;
    private readonly FreeAiNarrativeService _narrativeService;
    private readonly ILogger<WorktaleNarrativeWorker> _logger;

    public WorktaleNarrativeWorker(
        IWorktaleQueue queue,
        IWorktaleStore store,
        FreeAiNarrativeService narrativeService,
        ILogger<WorktaleNarrativeWorker> logger)
    {
        _queue = queue;
        _store = store;
        _narrativeService = narrativeService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string entryId;
            try
            {
                entryId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var entry = await _store.GetByIdAsync(entryId, stoppingToken);
                if (entry is null)
                {
                    _logger.LogWarning("Queued entry {EntryId} was not found in store", entryId);
                    continue;
                }

                if (!string.Equals(entry.Type, "commit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = await _narrativeService.GenerateForCommitAsync(entry, stoppingToken);
                await _store.UpdateNarrativeAsync(entryId, result.Narrative, result.IsFallback, result.Error, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected worker failure while processing queued entry {EntryId}", entryId);
            }
        }
    }
}