using Xunit;

namespace Monergy.JobManagement.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PhysicalDatabaseSerialGroup
{
    public const string Name = "Physical database";
}
