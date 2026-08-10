namespace DotNetMcp.Core;

public sealed record GeneratorIdentity(
    string AssemblyName,
    string TypeFullName,
    string Version);
