using Xunit;

namespace Flowbit.Tests;

// The real five-second timer characterization must not compete with the
// PostgreSQL/CPU-heavy integration collections for thread-pool scheduling.
[CollectionDefinition("instance-refresh-timing", DisableParallelization = true)]
public sealed class InstanceRefreshTimingCollection;
