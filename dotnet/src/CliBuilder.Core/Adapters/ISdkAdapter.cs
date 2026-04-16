using CliBuilder.Core.Models;

namespace CliBuilder.Core.Adapters;

public interface ISdkAdapter
{
    AdapterResult Extract(AdapterOptions options);
}
