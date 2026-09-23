namespace Bzapper;

public sealed partial class BzapperClient
{
    /// <summary>
    /// Versão desta SDK (enviada no header <c>X-Bzapper-Client: bzapper-dotnet/&lt;versão&gt;</c> de toda requisição).
    /// Igual à <c>&lt;Version&gt;</c> do <c>Bzapper.csproj</c> — o <c>scripts/release-sdks.sh</c> bumpa as duas.
    /// </summary>
    public const string Version = "0.8.0";
}
