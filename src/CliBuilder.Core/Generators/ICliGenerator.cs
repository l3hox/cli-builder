using CliBuilder.Core.Models;

namespace CliBuilder.Core.Generators;

public interface ICliGenerator
{
    GeneratorResult Generate(SdkMetadata metadata, GeneratorOptions options);
}
