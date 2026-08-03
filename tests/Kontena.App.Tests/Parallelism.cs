using Xunit;

// The shell is not a value: constructing a MainWindowViewModel calls BackendChips.Learn, which writes
// a process-wide table. xUnit runs test classes in parallel, so any class that builds a shell can
// overwrite that table between another class's arrange and assert — BackendChipTests failed exactly
// once that way, and passed on its own and on three full reruns.
//
// The alternative is naming every class that builds a shell and putting them in one collection, which
// is the list that grows and misses the next one. Serialising costs this assembly about three seconds
// — 620 tests, one second to four — and removes the whole class of flake.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
