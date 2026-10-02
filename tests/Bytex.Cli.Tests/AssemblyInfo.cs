// Every test here starts the real `bytex` as a child process, some of them a whole trading node. Running them side by
// side only makes each one slower, so they run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
