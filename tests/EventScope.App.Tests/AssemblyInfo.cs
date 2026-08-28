// Avalonia's headless platform sets up a single application/dispatcher instance for the
// whole process; tests in this assembly share it and cannot run as concurrent threads.
// Parallelization is disabled via xunit.runner.json instead of the now-obsolete
// CollectionBehaviorAttribute.DisableTestParallelization.
